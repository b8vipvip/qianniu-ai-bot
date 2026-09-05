# Qianniu AI Bot 项目交接上下文

更新时间：2026-09-04 16:40 +08:00 之后  
仓库：`b8vipvip/qnbot`  
默认分支：`master`  
当前稳定基线：`6291c3d137bdaa8154d18ab3a1a488b034d959a3`（PR #220 已合并）  
当前正式客户端：`bot-v1.1.1213`

## 1. 当前目标

继续以真实千牛运行日志和生产代码为准，优先保证：generation wall-clock deadline/cancellation、正确 seller/buyer 会话、Bot-owned exact draft、真实卖家回显/送达证据、订单幂等、Knowledge V2/人工学习、服务端 OCR，以及 CDP/WebSocket 生命周期稳定性。

原则：不为测试而修；静态测试锁定生产语义；运行时竞态必须继续用新正式版本真实日志复核。

## 2. 已完成并进入 master 的关键修复

### 2.1 OCR / Smart Reply / Knowledge
- 服务端 `/api/runtime/v1/ocr` 已实现并复用客户端 bearer 鉴权；
- Windows 正式包不再携带本地 OCR worker/model；
- OCR 优先使用本店 `ShopControlPlaneConnectionStore` URL/token；
- Semantic Embedding 前台已 true async，子 deadline 约 2.2 秒并继承 generation cancellation；
- Knowledge V2 FactKey 已细化；
- 人工答案学习、发送失败诊断、慢响应诊断统一使用 quote/escape-aware structured JSON 恢复器。

### 2.2 订单 / 固定回复
- OrderEventHub 跨进程原子持久化；
- order action durable ledger；
- 固定回复按业务动作和 segment 幂等；
- 必要订单字段在旧消费者之前进行有界 enrichment；
- 订单/CDP 入站重复 payload 已增加长窗精确去重。

### 2.3 可靠发送
- 发送前验证 seller/buyer/current conversation；
- Bot-owned exact draft 写入和确认；
- CDP DOM / 安全 HWND / UIA 分层发送；
- fallback 前重新验证目标会话和 owned draft；
- 卖家 echo / delivery verification 仍是最终成功证据；
- 已提交但等待回显时禁止盲目二次发送；
- 平台“服务态度提醒”走安全处理，不把“继续发送”作为普通自动 fallback；
- stale/cancelled Bot-owned draft 使用精确安全清理，不触碰人工草稿。

### 2.4 generation / runtime deadline
- 正常文本 AI 总预算保持 50 秒；
- PR #208 增加 ThreadPool starvation guard；
- PR #209 增加 dedicated-thread generation absolute-age watchdog；
- PR #220 修复 watchdog 仍可能被事件环淘汰或快速 `Generating -> Ready` 绕过的问题：
  - generation watch 不再依赖 `RecentEvents` 64 条诊断环存活；
  - 从 `BuyerActionAccepted` 起登记仍活动 generation，而不是等待采样到 `Generating`；
  - 55 秒覆盖 `Observed/Coalescing/Processing/Generating/Ready/Sending/Waiting` 整个端到端生命周期；
  - `Completed/Cancelled/Failed` 才移除 watch；
  - accepted event 时间异常时进行安全归一化。

### 2.5 CDP / 发送锁分钟级放大
PR #220 基于 `bot-v1.1.1197` 真实运行日志继续修复：
- CDP `_executeGate` 增加 1.5 秒排队上限；
- 仍保持单 WebSocket single-flight，不通过增加并发规避问题；
- `EnsureActiveBuyerForSendAsync` 保留最多 22 次快速确认，但增加 9 秒总 wall-clock deadline；
- 避免底层 CDP 8 秒级慢请求在多轮确认中把 `_sendGate` 占用到数分钟。

### 2.6 图片续问
- 图片续问允许 15 秒来源时间轻微 clock skew；
- 重绑最近图片时保留原 `BuyerSessionAgent`、`SessionGeneration`、continuation context 和 generation cancellation token；
- 防止图片派生 lease 脱离原 generation 生命周期。

## 3. 2026-09-04 最新 CI / 发布状态

PR #220 最终 head：`ae4e6a60daae1238151ee7ed22e1568547efabc3`。

合并前已确认：
- Windows CI：成功；
- API control plane CI：成功；
- Windows x64 Release build：成功；
- repository static tests、Windows PowerShell 5.1 updater parser、MSBuild x64、完整运行包组装与验证均通过。

合并后：
- master merge SHA：`6291c3d137bdaa8154d18ab3a1a488b034d959a3`；
- 正式 release：`bot-v1.1.1213`；
- release target commit：`6291c3d137bdaa8154d18ab3a1a488b034d959a3`；
- x64 安装包 SHA-256：`933cba62c3f8e93eec47b34ec24f851adb9fa5661c8f0cd8f55596d5a1607122`；
- rescue updater asset 已随正式 release 发布。

## 4. 当前仍需继续验证的真实问题

### P1 — 需要 `bot-v1.1.1213` 新运行日志验证

重点证据：
1. generation 从买家动作开始后的绝对年龄是否稳定在约 55 秒内被硬截止；
2. 是否还存在数分钟后 `Ready -> Sending -> Completed` 的迟到 generation；
3. `Ready/Sending/Waiting` 状态是否仍受同一 generation deadline 约束；
4. CDP execute gate 拥塞时是否在约 1.5 秒内 fail-fast，而不是静默排队几十秒；
5. 目标买家确认异常时是否在约 9 秒总预算内结束，而不是占用 `_sendGate` 数分钟；
6. 图片续问是否保持原 generation cancellation，且轻微来源时间逆序不再误判过期；
7. 发送成功仍必须有可信 submission/卖家 echo/delivery verification 证据；
8. OCR 是否稳定使用 `shop-control-plane`；
9. CDP 页面通道是否长期持续单调增长。

没有 `1.1.1213` 新真实日志前，不把这些运行时竞态写成“线上已证明彻底消失”。

### P2 — CDP / WebSocket 生命周期继续观察

目前没有足够新证据证明页面通道存在持续资源泄漏。不要仅因为历史心跳数字较大就重写生命周期；只有 `1.1.1213` 长时日志出现持续单调增长、旧 session 不释放或 target 数量无上界时，再按 session/target 创建、订阅、转交、释放链定位。

## 5. 当前仓库结论

- PR #219、#220 已合并；
- 2026-09-03/04 近期修复分支抽查均为 `ahead=0`，有效提交已进入 master；
- 当前没有开放 PR；
- 旧 `PENDING-RUNTIME-FIXES-20260902.md` 的内容已被后续 PR 完成或被当前真实日志结论取代，应删除，避免下一轮重复修旧问题；
- 当前运行时主线没有新的、已被真实日志证明但尚未编码的 P0 修复；下一步应使用 `bot-v1.1.1213` 真实运行日志继续挖掘。

历史功能型 open issues 与本轮运行时稳定性工作分开处理，不应把旧规划 issue 当作当前生产事故证据。

## 6. 下一步执行顺序

1. 客户端更新到 `bot-v1.1.1213`；
2. 进行真实买家文本、图片、订单、长耗时 AI、连续多消息、会话切换和平台提醒测试；
3. 保持运行足够长时间以观察 CDP/page-channel 生命周期；
4. 导出完整运行日志；
5. 只根据新日志中的新证据继续修复；
6. 若运行时稳定，再单独从 open feature issues 选择下一项产品能力开发。
