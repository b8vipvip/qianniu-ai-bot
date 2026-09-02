# 千牛真实发送可靠性问题记录 / Qianniu Send Reliability Incidents

最后更新：2026-09-02 14:45 +08:00

## 1. 判定原则

“Bot 没有回复”不能直接等价为“发送按钮失败”。必须区分：入站漏事件、Coalescing/排队、答案生成、deadline/cancellation、会话稳定性、草稿写入、发送动作、平台 modal、卖家 echo/delivery verification。

成功标准始终是：**正确 seller/buyer + Bot-owned exact draft + 真实送达证据**。输入框清空本身不是充分成功证据。

## 2. 当前生产发送链

截至正式版 `1.1.1139`：

1. 发送前确认 seller / buyer / 当前会话。
2. 写入 Bot-owned draft，并逐字确认编辑器内容。
3. 优先尝试可验证的 CDP DOM 独立发送按钮。
4. 再尝试当前 seller HWND 内、同 root 的安全主发送点。
5. 必要时进入 UIA fallback。
6. 每次 fallback 前后重新确认 exact draft；草稿消失但 echo 未到时只等待确认，不盲目执行第二次动作。
7. 千牛“服务态度提醒”等平台 modal 视为 terminal cancellation，Bot 不点击“继续发送”。
8. 物理输入异常时记录 Windows integrity / target process 诊断。
9. 卖家消息 echo / delivery verification 是最终成功证据。
10. 发送失败会进入 `SendFailureAnomalyService`，保留阶段原因并进行后台诊断。

## 3. 2026-08-13 历史事故状态

旧文档提出的待实现项现已更新：

- **5.1 发送状态机：已完成其核心语义。** 当前代码已经把草稿确认、发送动作、fallback 前再验证、echo confirmation 分阶段执行；不再以“调用成功”直接等价为送达。
- **5.2 UIA Invoke 降级为 fallback：已完成。** 当前优先 CDP DOM / HWND safe point，UIA 是后续兼容路径；且每次 fallback 前必须再次确认 owned draft。
- **5.3 Windows 输入权限诊断：已完成。** `QNRpa.NativeSend` 会读取 Bot/Qianniu integrity level，并记录 `targetHigherIntegrity` 等信息。
- **5.4 backend health：以异常报告/诊断闭环实现，不再作为独立阻塞 TODO。** 当前会记录发送阶段失败、delivery watchdog 结果和异常报告；若未来要做统计型 circuit breaker，应作为性能/运营能力另立需求，而不是当前发送正确性的未完成项。

因此 2026-08-13 文档中的旧“下一步 5.1–5.4”不再是当前开发待办。

## 4. 1.1.1139 新事故：不是发送动作失败，而是迟到 generation 进入发送

### 现场

2026-09-02 真实日志中，generation 1 在约 13:13:35 开始，配置文本总预算 50 秒；但直到约 13:26:06 才出现本地答案完成，`generationMs≈751802`，随后仍进入：

`Generating -> Ready -> Sending -> Completed`

最终该条消息有真实 delivery verification，因此**发送链本身成功**；真正的错误是上游 generation deadline 严重迟到，导致一个 12 分钟前的旧任务仍有资格进入发送链。

同一时间段多个 generation 的 `budgetSeconds=50` timeout 都延迟到数分钟后触发，说明不是单个模型响应慢，而是 runtime timer/cancellation continuation 存在调度饥饿。

### 当前根因判断

高并发时仍存在同步 UIA/CDP/兼容工作占用 CLR ThreadPool worker 的情况。默认最小 worker 数较低时，`Task.Delay` / `CancellationTokenSource.CancelAfter` / delivery watchdog continuation 也可能长时间排不到线程，从而让“50 秒 wall-clock budget”失去真实 wall-clock 含义。

### PR #208 修复

- 启动时提高 ThreadPool worker/I/O 最小容量，给 timeout/cancellation/watchdog 保留调度余量。
- 该改动不是扩大 AI 超时，而是让既有 50 秒 deadline 真正按时执行。

### 回归门槛

1. 连续 8–10 条 buyer 消息压力下，50 秒 timeout 实际触发应约在 50–55 秒，而不是分钟级。
2. 超时 generation 不得稍后进入 Ready/Sending。
3. 发送成功仍必须 `deliveryVerified=True`。
4. 若 ThreadPool 保护后仍出现迟到 generation，下一步必须继续隔离同步 `Task.Run`/UIA/CDP 路径，并在 BuyerSessionAgent/发送屏障增加 generation 绝对年龄硬拒绝；不能简单把 budget 调大。

## 5. 本次日志发送结果

1.1.1139 的多次实际文本发送均出现完整链：草稿写入 -> UIA/CDP/HWND 动作 -> 卖家回显 -> `deliveryVerified=True`。

因此本轮没有证据支持重新改写发送按钮策略；当前优先级是修复**迟到任务仍能进入发送链**和运行时 deadline 饥饿。

## 6. 诊断 JSON 解析补充

继续审计发现发送失败/慢响应诊断模块仍残留旧式 first `{` / last `}` 截取。Provider 若返回 Markdown fence、wrapper、array、JSON string 或正文中包含花括号，诊断结果可能解析失败。PR #208 正在统一 quote/escape-aware 结构化恢复器；这属于诊断可靠性修复，不改变真实发送安全门控。
