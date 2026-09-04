# Qianniu Chat Automation Progress / 千牛聊天自动化进度

Last updated / 最后更新：2026-09-04 16:40 +08:00 之后

本文件只记录**当前生产状态和仍需验证的事项**。早期 discovery / message lifecycle 研究证据保留在独立文档中，不再把历史实验 TODO 当作当前生产 TODO。

## 1. Current production baseline / 当前生产基线

- Default branch / 默认分支：`master`
- Verified master commit / 已验证主线提交：`6291c3d137bdaa8154d18ab3a1a488b034d959a3`
- Current formal release / 当前正式版本：`bot-v1.1.1213`
- Release target commit：`6291c3d137bdaa8154d18ab3a1a488b034d959a3`
- x64 package SHA-256：`933cba62c3f8e93eec47b34ec24f851adb9fa5661c8f0cd8f55596d5a1607122`
- PR #220 合并前 Windows CI、API control plane CI、Windows x64 Release build 均成功

## 2. Completed / 已完成

### 2.1 Chat event ingestion / 聊天事件接入
- WebSocket / CDP business session discovery 已完成；
- 买家文本、订单卡片、图片消息进入统一处理链；
- 非买家会话隔离已完成；
- 重复 CDP 页面 exact payload 已增加长窗去重，避免恢复窗口重复入站。

### 2.2 Coalescing / Generation
- 买家 burst coalescing 已实现；
- 前置规则等待有上限；
- generation cancellation / fail-open 已实现；
- 正常文本 AI 总预算仍为 50 秒；
- PR #208 增加 ThreadPool starvation guard；
- PR #209 增加 dedicated background thread absolute-age watchdog；
- PR #220 将 watchdog 从“采样到 `Generating` 才登记”升级为“从 `BuyerActionAccepted` 起登记活动 generation”；
- 55 秒绝对期限覆盖 `Observed/Coalescing/Processing/Generating/Ready/Sending/Waiting`，终态才移除；
- watch registry 不再依赖 64 条 `RecentEvents` 诊断环，因此不会因高密度事件淘汰 accepted event 而失效。

### 2.3 Smart Reply / Knowledge
- Semantic Embedding 前台 true async；
- embedding 子 deadline 约 2.2 秒；
- Knowledge V2 FactKey 已细化；
- 人工答案学习与异常诊断 JSON 解析已切换为结构化恢复器；
- Knowledge V2 迟到答案在进入 Ready 前有 generation freshness / age barrier。

### 2.4 Order events / 订单事件
- OrderEventHub 跨进程原子持久化完成；
- order action durable ledger 完成；
- 固定回复按 segment / 业务动作幂等；
- 必要订单字段 enrichment 有界执行；
- 订单/CDP 入站重复 payload 有精确去重保护。

### 2.5 Reliable send / 可靠发送
- Bot-owned exact draft 校验；
- CDP / 安全 HWND / UIA 分层发送；
- fallback 前重新验证 seller/buyer/current conversation 和 owned draft；
- verified submission / seller echo / delivery verification 作为成功证据；
- 已提交等待回显时禁止盲目 resend；
- stale/cancelled Bot-owned draft 使用精确安全清理；
- 平台“服务态度提醒”走安全处理，不把“继续发送”作为普通自动 fallback；
- PR #220 给 CDP `_executeGate` 增加 1.5 秒排队上限；
- `EnsureActiveBuyerForSendAsync` 保留最多 22 次快速确认，但增加 9 秒总 wall-clock deadline。

### 2.6 Vision / OCR
- 本地 OCR worker/model 已从 Windows release 移除；
- 服务端 `/api/runtime/v1/ocr` 已实现；
- 客户端使用本店 `ShopControlPlaneConnectionStore` URL/token 自动解析 OCR endpoint；
- 图片续问允许 15 秒来源 clock skew；
- 图片派生 lease 保留原 `BuyerSessionAgent`、`SessionGeneration`、continuation context 和 cancellation token。

## 3. Remaining runtime work / 当前剩余运行时工作

### P1 — `bot-v1.1.1213` runtime verification / 新版真实日志验证

下一份完整日志重点检查：
- 55 秒 absolute-age deadline 是否从 buyer action 起稳定生效；
- 是否仍出现数分钟迟到 generation；
- `Ready/Sending/Waiting` 是否仍受同一 deadline 约束；
- CDP execute gate 拥塞是否在约 1.5 秒内 fail-fast；
- active buyer confirmation 异常是否在约 9 秒总预算内结束；
- 图片续问是否保留 generation cancellation 且不因轻微时钟逆序误判过期；
- 发送成功是否继续具有可信 submission/卖家 echo/delivery verification；
- OCR 是否使用 `shop-control-plane`；
- CDP 页面通道是否持续单调增长。

### P2 — CDP lifecycle observation / 生命周期观察

当前没有足够证据证明 page-channel 持续泄漏。保持观察；只有 `1.1.1213` 长时日志出现持续增长、旧 target/session 不释放时才继续改生命周期代码。

## 4. Validation rule / 验证规则

任何“已修复”至少满足其一：

1. 生产代码 + CI/静态契约证明；或
2. 新版本真实运行日志证明。

对于运行时竞态、真实发送、平台 UI、CDP 生命周期等问题，CI 通过不等于线上已验证，必须继续用新正式版本日志复核。

## 5. Next execution / 下一步

1. 客户端更新到 `bot-v1.1.1213`；
2. 做真实买家文本、图片、订单、长耗时 AI、连续多消息、会话切换与平台提醒测试；
3. 保持长时运行以观察 CDP/page-channel 生命周期；
4. 导出完整日志；
5. 按新日志继续挖掘，不凭旧文档重复修复已完成项。
