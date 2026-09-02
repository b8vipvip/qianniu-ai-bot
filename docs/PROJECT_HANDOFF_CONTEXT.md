# Qianniu AI Bot 项目交接上下文

更新时间：2026-09-02 16:35 +08:00 之后  
仓库：`b8vipvip/qianniu-ai-bot`  
默认分支：`master`  
当前稳定基线：`d5a72fd3fac83ced6780ac1213fbacecd784f64f`（PR #208 已合并）  
当前正式客户端：`bot-v1.1.1150`

## 1. 当前目标

继续以真实千牛运行日志和生产代码为准，优先修复：运行时 deadline/cancellation、真实发送与卖家回显、订单幂等、Knowledge V2/人工学习、服务端 OCR，以及 CDP/WebSocket 生命周期稳定性。原则是不为测试而修，测试必须锁定生产语义。

## 2. 已完成并进入 master 的修复

### 2.1 OCR 服务端迁移
- 服务端 `/api/runtime/v1/ocr` 已实现并复用客户端 bearer 鉴权；
- 8MB 默认图片上限、8 秒请求超时、并发限制、SHA256 请求/响应校验；
- Windows 客户端不再携带本地 OCR worker/model；
- 客户端优先从 `ShopControlPlaneConnectionStore` 取得本店控制面 URL/token，不再要求额外伪造 `Type=服务端控制面` AI 端点；
- Docker 构建预取 RapidOCR 模型并包含真实 OCR smoke。

### 2.2 Semantic Embedding 前台异步化
- Smart Reply 前台改为 `BuildPlanAsync`；
- embedding 使用真正异步 `Http.SendAsync`；
- 子 deadline 约 2.2 秒并继承 generation cancellation；
- embedding 超时 fail-open，不允许拖死主回复链。

### 2.3 订单状态与固定回复
- OrderEventHub 使用路径作用域命名 Mutex；
- read-modify-write 在跨进程锁内执行；
- 唯一临时文件 + `Flush(true)` + `File.Replace`；
- bounded retry，失败时保留旧有效状态；
- 订单 action ledger 已强化为跨进程 durable state；
- 人工发送完全相同固定分段时只满足该 segment，不取消后续不同 segment。

### 2.4 平台“服务态度提醒”保护
- 检测千牛重复消息提醒；
- Bot 不自动点击“继续发送”；
- 优先返回修改并将本次发送判为 blocked/failed；
- 防止同文本盲目重试再次触发平台提醒。

### 2.5 Coalescing / generation starvation
- 前置规则等待已有上限与 generation cancellation；
- generation timeout fail-open；
- 1.1.1139 日志确认过“50 秒 deadline 实际延迟数分钟”；
- PR #208 加入运行时 ThreadPool 最低容量保护，让 `CancelAfter`、`Task.Delay`、delivery watchdog 等 continuation 不再被同步工作长期饿死。

### 2.6 Knowledge / 人工学习
- Knowledge V2 FactKey 已细化；
- manual answer learning 不再依赖 first-`{`/last-`}` 截取；
- send-failure / slow-response anomaly 诊断也已切换为 quote/escape-aware structured JSON 恢复器。

## 3. CI / 发布状态

PR #208 合并后：
- merge SHA：`d5a72fd3fac83ced6780ac1213fbacecd784f64f`；
- Windows x64 Release build #1150：成功；
- 自动更新发布链：成功；
- rescue updater asset 发布链：成功；
- 正式 release：`bot-v1.1.1150`。

当前 PR #209 已完成第一轮修复并通过最新一轮：
- Windows CI #970：成功；
- API control plane CI #805：成功；
- Windows x64 Release #1152：成功。

## 4. 当前仍需继续验证 / 修复的真实问题

### P0 — generation 绝对年龄最后防线：已在 PR #209 实现，待合并与真实日志验证

PR #209 在 `BuyerSessionAgentRuntimeBridge` 增加独立 dedicated background Thread：
- 每 250ms 扫描已观察 seller+buyer 的 generation；
- generation 首次进入 `Generating` 后开始独立 wall-clock 计时；
- 活跃时间超过 55 秒时调用 `BuyerSessionAgent.Cancel(..., "absolute_generation_age_exceeded")`；
- 取消会把 generation 从 `ActiveGenerations` 移除；
- 即使下游 provider 完全忽略 cancellation 并迟到返回，现有 `lease.IsCurrent` 也会失败，因此不能继续进入 Ready/Sending；
- 人工客服回复不触发取消；Completed/Cancelled/Failed watch 自动清理。

这道防线不依赖 ThreadPool timer continuation，也不替代正常 50 秒 cancellation。

### P1 — 需要 1.1.1150+ 新运行日志验证

重点证据：
1. generation deadline 是否稳定约在 50–55 秒内；
2. 是否还存在数分钟后 `Generating → Ready → Sending → Completed`；
3. 若出现 `absolute_generation_age_exceeded`，该 generation 后续不得再 Ready/Sending；
4. OCR 是否出现 `endpointSource=shop-control-plane`；
5. 图片是否不再无故退回视觉 AI；
6. CDP 页面通道是否长期单调增长；
7. 平台“服务态度提醒”出现时是否明确 blocked 且无“继续发送”自动点击。

没有新版本真实日志前，不把运行时竞态写成“线上已证明修复”。

### P2 — CDP / WebSocket 生命周期继续观察

1.1.1139 日志里业务 CDP 保持单实例，页面通道数量没有形成明确持续单调增长证据，因此当前不再把它定性为已确认泄漏。若新版本长时日志出现通道持续上涨，再按 session/target 创建与释放链定位。

## 5. 本次重新检查仓库的结论

- 旧 `PENDING_RUNTIME_FIXES_20260902.md` 已不存在，旧清单已经收敛进当前交接文档；
- open issues #46/#47/#48 是历史架构/功能规划，不是本轮运行时回归证据；
- PR #209 是当前唯一运行时修复 PR；
- 旧文档基线 `1.1.1139` 已更新为 `1.1.1150`；
- generation 独立绝对年龄屏障已经编码并通过 Windows/API/x64 三条 CI，下一步是合并与发布后真实日志验证。

## 6. 下一步执行顺序

1. 合并 PR #209；
2. 等待 master x64 build 与自动发布链成功；
3. 用新版本真实聊天测试并导出完整运行日志；
4. 根据日志继续挖掘，不凭旧文档重复修复已经完成的问题。
