# Qianniu Chat Automation Progress / 千牛聊天自动化进度

Last updated / 最后更新：2026-09-02 14:40 +08:00

本文件只记录**当前生产状态和仍需验证的事项**。早期 discovery / message lifecycle 研究证据保留在独立文档中，不再把 2026-07 的实验 TODO 当作当前生产 TODO。

## 当前正式基线

- Release: `1.1.1139`
- Commit: `63b25d59b307a210279119665d3c7c9c85755ecc`
- Active PR: `#208 fix/runtime-starvation-ocr-config-20260902`

## 已完成生产能力

### 入站与会话

- 单一权威业务 CDP + 重复页面轻量入站补偿。
- 重复入站短窗去重、已处理消息 key 去重。
- 后台通知漏详细事件时主动切换目标 buyer 并补抓远端历史。
- NonBuyerConversationGuard 前置屏蔽小二/服务商/1688/群聊/平台系统会话。
- BuyerSessionAgent 统一 seller+buyer 时间线；人工 seller reply 只作为学习证据。

### 文本回复

- BuyerMessageBurst 支持独立普通问题并发。
- dependent fragment 语义续问在 180s TTL 内继承 substantive anchor。
- `ModelQuestion` 已进入 Streaming/legacy AI。
- Coalescing 外层重复 gate 已删除；固定规则使用唯一 1.8s gate 并 fail-open。
- Semantic Embedding 前台 async + 2200ms 子预算，失败回退本地 hybrid retrieval。
- AI 文本总预算配置为 50 秒。

### 图片/OCR

- 图片先本地缓存，撤回后可继续识别但禁止发送旧回复。
- 图片指代续问继续走视觉上下文。
- OCR 推理已迁移 API control plane；Windows 不再打包本地 OCR worker/model。
- OCR-first 只有 OCR 高置信 + Knowledge V2 `CanDirectReply` 同时成立才免视觉 API 直答。

### 订单

- OrderEventHub 统一订单事件。
- 固定预设不等待 AI。
- order action durable ledger 跨进程幂等。
- OrderEventHub 状态文件跨进程锁 + 原子 replace。
- exact seller echo 可满足当前固定分段，后续不同分段仍继续。

### 发送

- 发送前验证目标 buyer 与 Bot-owned draft。
- CDP DOM send -> HWND safe point -> UIA fallback 分层执行。
- fallback 前后重新检查 exact draft，避免不确定成功后的重复发送。
- 卖家 echo / delivery verification 是最终成功证据。
- 平台“服务态度提醒”禁止自动确认。
- HWND root/modal 安全边界和 Windows integrity 诊断已存在。

### Knowledge V2 / 学习

- SQLite repository + structured index + working memory + feedback loop。
- FactKey 当前包含 Subject / Predicate / Intent / ProductIds / Entities / Conditions / RequiredContext / Exclusions。
- 人工答案即时对比学习 JSON 已支持 fence/wrapper/array/string/balanced object 恢复。
- 无法恢复结构化结果时 fail-open `skipped`，不污染知识。

## 1.1.1139 真实运行日志结论

### 已验证正常

- 启动数据库完整性 OK。
- Web sync / rules sync / update SSE 正常。
- 业务权威 CDP 始终为 1。
- 多次真实发送均有 `deliveryVerified=True`。
- 非买家/平台系统提示在普通回复链前被丢弃。
- 本地优先高置信 Knowledge V2 直答可在毫秒级返回并真实送达。

### 新发现 P0：50 秒 deadline 实际延迟数分钟

同一 buyer 的多个 generation 在日志中配置 `budgetSeconds=50`，但 timeout continuation 实际延迟到数分钟；最严重 generation 1 的本地答案约 751 秒后才返回，并继续进入 Ready/Sending。

当前判断：CLR ThreadPool 被同步 UIA/CDP/兼容工作占满时，`Task.Delay` / `CancelAfter` continuation 也被饿死。PR #208 已增加启动时 ThreadPool 最小 worker/I/O 容量保护。

**回归通过条件：**连续消息压力下，50 秒 deadline 的实际触发必须接近配置值，不能再出现分钟级漂移；旧 generation 不能在数分钟后迟到发送。

### 新发现 P1：OCR 控制面配置来源错误

客户端已经连接控制面 SSE，但 OCR 仍提示“未配置可用的服务端控制面OCR接口”。PR #208 改为按 seller/shop 从 `ShopControlPlaneConnectionStore` 直接取正式 URL/token，旧 AI endpoint 仅兼容回退。

**回归通过条件：**图片测试应出现 `endpointSource=shop-control-plane` 或 OCR 服务端成功日志；不得再因为没有额外 AI endpoint 而跳过 OCR。

### 新发现 P1：诊断 AI JSON 恢复未完全统一

人工学习已修，但发送失败/慢响应诊断仍发现旧 first/last brace parser。PR #208 正在统一结构化恢复，避免诊断报告因 provider wrapper/fence 失真。

## 当前观察项（不是已确认 bug）

- `页面通道=27`：本次约 1 小时日志内未持续增长，业务 CDP=1；继续观察，不凭单次数字判定泄漏。
- Knowledge V2 `conflicts=93`：当前 FactKey 已细化，先按真实治理冲突处理；只有逐条证据证明误冲突才继续改算法。
- 人工回复不取消 Bot generation：这是当前明确策略，不应被误当 bug；若未来需要“人工回复即接管”，必须作为产品策略单独设计，而不是隐式取消。

## 下一轮真实测试清单

1. 连续快速发送 8–10 条买家文本，覆盖普通问题 + 省略续问。
2. 保持至少一个 AI 请求超过 50 秒，确认 timeout 约在 50–55 秒触发。
3. 确认超时 generation 不会稍后进入 Ready/Sending。
4. 发送包含文字的图片，确认 server OCR 使用当前 shop token。
5. 暂时制造 OCR server failure，确认视觉模型 soft fallback。
6. 继续运行 1 小时以上，比较 `页面通道` 是否单调增长。
7. 订单 Created/Paid 固定回复各测一次，再人工发送完全相同分段验证 exact echo satisfied。

## 发布门槛

任何生产修复必须：分支 -> 回归/静态测试 -> PR -> Windows CI + API control plane CI + Windows x64 release build 全绿 -> merge master -> master release build -> auto-update release。
