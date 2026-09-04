# 千牛真实发送可靠性问题记录 / Qianniu Send Reliability Incidents

最后更新：2026-09-04 16:40 +08:00

## 1. 判定原则

“Bot 没有回复”不能直接等价为“发送按钮失败”。必须区分：入站漏事件、Coalescing/排队、答案生成、deadline/cancellation、会话稳定性、草稿写入、发送动作、平台 modal、卖家 echo/delivery verification。

成功标准始终是：**正确 seller/buyer + Bot-owned exact draft + 可信提交证据 + 真实送达证据**。输入框清空本身不是充分成功证据。

## 2. 当前生产发送链

截至正式版 `bot-v1.1.1213`：

1. 发送前确认 seller / buyer / 当前会话。
2. 写入 Bot-owned draft，并确认编辑器内容与目标文本一致。
3. 优先尝试可验证的 CDP DOM 独立发送按钮。
4. 再尝试当前 seller HWND 内、同 root 的安全发送点。
5. 必要时进入 UIA fallback。
6. 每次 fallback 前重新确认 seller/buyer/current conversation 与 exact draft。
7. 草稿消失但 echo 未到时，如果已有可信 verified submission，只等待 delivery confirmation，不盲目执行第二次发送动作。
8. 平台“服务态度提醒”走安全处理，不把“继续发送”作为普通自动 fallback。
9. stale/cancelled Bot-owned draft 只做 exact safe cleanup，不清理人工输入。
10. 卖家消息 echo / delivery verification 是最终成功证据。
11. 发送失败进入 `SendFailureAnomalyService`，保留阶段原因并后台诊断。

## 3. 2026-09-02 历史事故：迟到 generation 进入发送

### 现场

`bot-v1.1.1139` 真实日志中，generation 1 配置文本总预算 50 秒，但约 751 秒后才完成，随后仍进入：

`Generating -> Ready -> Sending -> Completed`

最终该条消息有真实 delivery verification，因此当时的核心错误不是“发送按钮没点成功”，而是**12 分钟前的旧 generation 仍有资格进入发送链**。

### 已完成修复

- PR #208：提高 ThreadPool worker/I/O 最低容量，减少 timeout/cancellation continuation 饥饿；
- PR #209：增加独立 dedicated-thread generation absolute-age watchdog；
- PR #220：进一步修复 watchdog 的两个绕过窗口：
  - 不再依赖 `RecentEvents` 诊断环持续保存 `BuyerActionAccepted`；
  - 不再要求 250ms 采样恰好看到短暂的 `Generating`；
  - 从 `BuyerActionAccepted` 起登记活动 generation，55 秒覆盖到 `Ready/Sending/Waiting`；
  - 终态才移除 watch。

因此当前发送链的最后资格门不再只依赖正常 50 秒 `CancelAfter`。

## 4. 2026-09-03/04 发送分钟级阻塞与重复提交风险

后续生产修复继续发现：即使单次 CDP request 有 8 秒 timeout，**等待进入 CDP execute 串行门本身**以及**发送前最多 22 轮 active buyer confirmation**仍可能把一次发送放大到数十秒甚至数分钟。

### 已完成修复

- `_executeGate` 增加 1.5 秒排队上限；
- 保持单 WebSocket single-flight，不通过提高并发规避串行一致性问题；
- `EnsureActiveBuyerForSendAsync` 增加 9 秒总 wall-clock deadline；
- 保留最多 22 次快速确认能力，但每轮等待受剩余总预算约束；
- verified composer submission 已纳入 delivery watchdog，避免“动作其实已提交、echo 迟到”时再次发送；
- stale buyer reply / stale generation 被归类为不可重试，避免 ghost continue-send；
- Bot-owned cancelled/stale draft 采用 exact safe clear。

## 5. 2026-09-04 `bot-v1.1.1197` 日志对应 PR #220

该轮真实日志继续暴露：

1. generation watchdog 可能因高密度事件挤出 64 条诊断环而失去发现入口；
2. Knowledge V2 本地直答可能在一次 250ms watchdog 采样间隔内快速进入 `Ready`；
3. CDP execute gate 外排队没有上限；
4. active buyer confirmation 只有尝试次数，没有总墙钟预算；
5. 图片续问存在轻微来源时钟逆序，且派生 lease 需要继续携带原 generation cancellation。

这些修复已经由 PR #220 合并到 master，并发布为 `bot-v1.1.1213`。

## 6. 当前回归门槛

`bot-v1.1.1213` 新真实日志必须继续验证：

1. generation 从 buyer action 起超过约 55 秒后不能再进入/停留在可发送生命周期；
2. 超时或取消 generation 后续不得出现迟到 `Ready -> Sending -> Completed`；
3. CDP execute gate 拥塞必须 fail-fast，不再出现 gate 外长时间静默排队；
4. active buyer confirmation 异常必须在约 9 秒总预算内结束；
5. verified submission 后不得因 echo 迟到盲目 resend；
6. 最终成功仍需可信 seller echo / delivery verification；
7. 平台 modal、目标买家变化、stale generation、CDP 失效必须保持不同失败语义；
8. 证据不足时禁止自动重复发送同一内容。

## 7. 当前结论

当前没有证据支持再次重写发送按钮策略。最近几轮的主要风险集中在**发送资格生命周期、会话确认总预算、CDP 串行排队和重复提交防护**，这些已进入 `bot-v1.1.1213`。

下一步必须以 `1.1.1213` 新日志复核；没有新证据时，不应回退到历史坐标点击、盲目 UIA retry 或扩大 timeout。
