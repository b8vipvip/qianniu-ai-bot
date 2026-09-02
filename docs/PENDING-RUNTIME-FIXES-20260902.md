# 运行时待修复清单 / Runtime Pending Fixes — 2026-09-02

> 当前主线优先级：**先完成 OCR 服务端迁移并确保 API CI / Windows CI / Windows x64 Release 全绿**。本文仅登记已获得真实证据但不应阻塞 OCR 合并的后续问题。

## P0 — OCR 主线完成后立即处理

### 1. Semantic Embedding 必须改为真实异步 wall-clock deadline

现状：`SmartReplyRouterService.BuildPlan()` 同步调用 `SemanticEmbeddingService.TryScore()`，后者的 HTTP 请求仍通过同步等待进入调用线程。即使请求本身配置取消令牌，也可能让 Smart Reply 路由线程长时间被占用。

运行日志已出现严重慢响应：总耗时约 96.9 秒，其中答案生成约 92.5 秒；另有文本 AI 总预算 50 秒超时记录。

目标：
- 从路由调用点到 HTTP 请求全链路 `async/await`；
- 使用 linked cancellation token + wall-clock deadline；
- deadline 到达后 fail-open 到本地混合检索，不阻塞主 AI 预算；
- 后台 warmup 与前台请求使用独立预算。

### 2. OrderEventHub 跨进程原子持久化

现状：订单事件状态只有进程内 `lock(Sync)`；固定 `.tmp` 文件 + delete/move 在多实例或残留进程下可能争抢同一路径。

目标：
- 基于状态文件路径的跨进程命名 Mutex；
- 唯一临时文件；
- flush-to-disk；
- 原子 replace/move；
- 短暂 sharing violation 有界重试；
- 写入失败必须保留上一份有效状态；
- 多进程写入前重读并合并，避免 last-writer 丢事件。

### 3. 订单固定预设必须保持“业务动作”语义

目标：
- 固定预设发送不被普通 AI stale-answer 规则错误取消；
- 仍校验 seller/buyer/order identity；
- 若卖家真实回显已经包含完全相同段落，则视为该动作已满足，不重复发送；
- 保持现有动作级幂等，不另建第二套重复机制。

## P1 — 发送与千牛 UI 风控

### 4. 千牛“服务态度提醒”发送拦截窗口

截图证据：发送时千牛可能弹出“服务态度提醒”，提示继续发送可能引起消费者反感或差评，按钮包含“返回修改 / 继续发送”。当前日志没有独立分类该拦截，因此可能最终表现为“发送动作发生但没有卖家回显”。

安全目标：
- 在已验证 seller Desk 的 UIA 树内检测标题/正文特征；
- **Bot 不自动点击“继续发送”**，不绕过千牛风控；
- 自动选择“返回修改”或将本次发送标记为平台拦截，并停止相同文本自动重试；
- 保留草稿供人工修改；
- `LastSendWasCancelled=true`，失败原因明确为 `平台服务态度提醒已拦截发送`；
- watchdog 不得把它误诊断为普通 UIA/CDP 丢回显；
- 增加静态/回归测试，防止未来恢复盲目重发。

### 5. 发送动作后 9 秒无卖家真实回显

日志存在真实案例：CDP 页面按钮 + HWND 安全消息 + UIA 安全回退均进入发送流程，但 9 秒内无相同卖家回显，最终发送失败。

后续需要把原因至少拆成：
- 千牛平台风控/服务态度提醒；
- 输入框或发送按钮动作未真正提交；
- 实际已送达但本地 echo 事件缺失；
- 当前会话切换/目标买家变化；
- CDP 已失效。

原则：证据不足时禁止自动重复发送同一内容。

### 6. CDP `IngressConversationMapProbe` 超时与旧页面生命周期

日志出现 `IngressConversationMapProbe` 调用超时，随后权威 CDP 会话失效并请求 WebSocket 重连。同时运行心跳曾显示业务 CDP=1、页面通道=45。

后续检查：
- 页面关闭后 subscription / websocket / CDP session 是否完全释放；
- 轻量入站补偿通道是否存在长期无价值保留；
- probe 超时是否应该只降级当前 probe，而不是放大成完整会话抖动；
- 增加长期运行资源数量上界测试。

## 已确认无需回退的修复

- generation 在答案就绪前返回时保持 Failed，不再错误覆盖为 Completed；
- OCR 失败目前是 soft fallback，不会因为 OCR 异常直接崩溃进程；
- 卖家真实消息回显已经用于确认真正送达，后续应继续复用这条权威证据链。

## 当前执行顺序

1. PR #203：OCR 服务端迁移 → 三条 CI 全绿 → 合并。
2. 正式 Release / 服务端部署更新并验证 OCR runtime。
3. Embedding async wall-clock deadline。
4. OrderEventHub 跨进程原子持久化 + 固定预设动作语义。
5. 千牛“服务态度提醒”安全处理。
6. 发送失败细分诊断与 CDP/page-channel 生命周期收敛。
