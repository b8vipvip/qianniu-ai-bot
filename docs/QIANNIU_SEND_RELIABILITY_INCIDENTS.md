# 千牛真实发送可靠性问题记录

最后更新：2026-08-15

## 1. 记录目的

本文件专门记录“Bot 已经识别买家、生成答案，甚至已经把答案写入千牛输入框，但真实卖家消息没有成功送达”的问题。

这类故障过去多次表现为相同外部症状，但历史提交表明，它们并不是同一个根因简单反复出现，而是发生在同一条出站链路的不同层：会话归属、CDP/IMSDK、UIA 控件定位、WPF 线程、键盘焦点、split-button 语义、物理鼠标输入以及真实发送回显。

因此后续排障时禁止只根据“没有自动回复”判断为旧问题复发，必须先确认失败停在哪个阶段。

---

## 2. 当前生产发送链

截至 `master` 的 PR #124，文本发送采用以下顺序：

1. 严格确认 seller / buyer / 当前会话。
2. 使用 `application.insertText2Inputbox` 写入目标草稿；该接口只负责写入，不代表发送成功。
3. 使用 CDP/UIA 确认输入框中的文本仍属于本次 Bot owned draft。
4. 将 UIA 扫描限定到当前 seller 对应的千牛接待 HWND，并定位精确 `sendMsg` 控件。
5. 正常主路径：点击该 split-button 左侧“发送”主操作区域；坐标由实时 UIA `BoundingRectangle` 推导，不使用固定屏幕坐标。
6. 只有物理坐标输入动作在执行阶段直接抛异常/被 Windows 拒绝时，才允许进入一次性 UIA `Invoke()` 回退。
7. 回退之前必须再次逐字确认本次 owned draft 仍存在；草稿已消失或无法确认时禁止第二次发送动作，只等待真实回显。
8. 卖家消息真实回显优先作为送达证据，输入框清空只作为辅助证据。
9. 文本发送不再使用物理 Enter。

该设计的核心原则是：**任何不确定状态都 fail-closed，宁可漏发，也不允许因为重试造成跨买家、重复或错误发送。**

---

## 3. 历史修复时间线

### PR #95 — seller-bound HWND

外部症状：答案生成后无法真实发送，日志出现 `UIA扫描：未找到 MutilChatView`。

根因：发送 UIA 仍依赖旧千牛顶层 `MutilChatView` 假设，并通过全局 Desk/整个 AliWorkbench 进程扫描；新版千牛和多店场景不能保证该结构。

修复：改为 seller -> Desk -> 真实 HWND，在当前卖家窗口 UIA 树中定位输入框和发送按钮。

结论：解决的是**UIA 扫描根节点/多店窗口归属**问题。

### PR #104 — 重启后重复 CDP session 抢占

外部症状：Bot 重启后已经拿到答案和正确买家，但 CDP 调用超时，真实发送未完成。

根因：同一个 seller 在多个 `recent.html` / WebSocket/CDP session 中反复初始化，`QN.CDP` 被不同 session 抢占。

修复：每个 seller 只保留一个权威 CDP session；重复 session 不再接管 outbound ownership。

结论：解决的是**重启后的 CDP 所有权/竞态**问题。

### PR #111 — 发送运行时硬化与首条/订单回复恢复

外部症状：下单自动回复已经识别订单并进入发送，但卡在 UIA/剪贴板输入；首条咨询也可能因为发送前提前占用资格而永久漏答。

根因包括：QNRpa 初始化依赖全局 Desk、低资源环境下 UIA/剪贴板容易阻塞、首条固定回复的 delivered 语义过早。

修复：seller 专属 Desk 安全绑定；优先 CDP 写入、UIA 校验；首条固定回复改为真实送达后才提交 30 分钟资格。

结论：解决的是**初始化、输入路径和业务送达状态机**问题。

### PR #114 — Enter 主发送

外部症状：`insertText2Inputbox` 成功且 `isInputboxEmpty=false`，但长时间无法进入发送完成。

根因：Enter 之前还依赖可能长时间阻塞的同步 UIA 文本确认，在低内存/RDP/UIA 卡顿时根本到不了发送动作。

修复：将 Enter 提升为主发送动作，并使用 CDP 快速确认草稿存在。

结论：解决的是**发送前 UIA 阻塞**问题。

### PR #115 — WPF STA sync-over-async

外部症状：Bot UI/发送链整体挂死。

根因：WinDbg 证据确认 WPF STA 主线程阻塞在 `TaskAwaiter/GetResult`，4GB 内存压力会放大 CDP/WebView 延迟，但软件层面的确定问题是 sync-over-async。

修复：移除 `.GetAwaiter().GetResult()`；CDP/UIA 采用真正异步和有界超时；当时发送优先级为 Enter -> UIA Invoke -> 坐标。

结论：解决的是**线程模型/阻塞**问题。

### PR #116 — 去掉物理 Enter，UIA 作为主发送

外部症状：新版千牛不在前台/最顶层窗口时，物理 Enter 依赖全局键盘焦点，可能无效或作用于错误窗口。

修复：删除文本发送的物理 Enter；改成 seller-scoped UIA `Invoke()` 为主，按钮坐标为最后回退。

结论：解决的是**全局键盘焦点依赖**问题。

### PR #118 — 千牛发送按钮是 split-button

外部症状：UIA `Invoke()` 在实机超时；坐标回退却点击了右侧下拉区，打开“按 Enter 发送 / 按 Ctrl+Enter 发送”菜单，没有真正发送；失败重试还可能重复追加草稿。

修复：不再把 split-button 的 UIA `Invoke()` 作为正常主路径；UIA 只定位 `sendMsg` 和 BoundingRectangle，真实动作改为点击左侧主区域，并给右侧 18~30px/约三分之一留出 arrow guard；owned draft 重试不再二次插入。

结论：解决的是**split-button 语义 + 重复草稿**问题。

### PR #124 — 坐标输入被 Windows 拒绝

外部症状：首条咨询、下单自动回复均已进入真实发送链；目标 buyer 正确、草稿已写入、`sendMsg` 控件与坐标正确，但 `FlaUI.Core.Input.Mouse.Click` 连续抛出“拒绝访问”。

修复：

- 坐标点击仍为正常主路径；
- 单独记录“物理坐标动作是否在执行阶段抛异常”；
- 只有该动作被系统直接拒绝/抛异常时，才允许一次性 UIA `Invoke()` 回退；
- 回退前再次验证 owned draft；
- 如果草稿消失/无法确认，只观察送达，不执行第二次动作；
- 增加异常类型和 HResult 日志。

结论：解决的是**Windows 物理输入注入失败时缺少安全替代路径**问题。

---

## 4. 为什么“改好一段时间后又复现”

### 4.1 外部症状相同，但故障点并不相同

历史上“没有自动回复”至少曾分别由以下位置造成：

- UIA 找错窗口；
- 重启后 CDP session 抢占；
- WPF 主线程阻塞；
- UIA 文本读取卡住；
- Enter 焦点不可靠；
- UIA Invoke 对 split-button 行为不稳定；
- 坐标点击命中 split-button 下拉区；
- 重试重复写入草稿；
- 当前最新现场：物理鼠标输入被 Windows 拒绝。

因此不能把这些记录归类为“同一个 bug 修了很多次”。更准确的说法是：**没有一个经过当前千牛版本验证的普通聊天直接发送 API，所以生产链长期跨越多个自动化层；不同环境状态会暴露下一层之前没有出现的故障。**

### 4.2 每次现场修复都可能改变主路径和 fallback，暴露新的边界

发送策略曾经历：

`旧 UIA/Clipboard -> Enter 主发送 -> Enter + UIA fallback -> seller-scoped UIA Invoke -> split-button 左侧坐标 -> 坐标失败时受控 UIA Invoke`

这些变化不是无理由反复切换，而是现场证据逐步否定了前一阶段的假设：

- Enter 在非前台窗口不可靠；
- UIA Invoke 对当前 split-button 可能超时/语义异常；
- split-button 左侧坐标虽然能精确避开箭头，但物理输入 API 又可能被 Windows 拒绝。

因此真正需要稳定的不是“永远只用某一个动作”，而是**明确、可观测、有边界的发送状态机和受控降级策略**。

### 4.3 CI 能验证代码和顺序，但不能模拟真实千牛桌面语义

当前 Windows CI 可以验证：

- 静态回归；
- Python 生命周期测试；
- NuGet restore；
- x64 MSBuild；
- 完整运行包组装。

但 GitHub Hosted Runner 没有真实登录的千牛接待窗口，也无法覆盖：

- 当前千牛 UIA provider 对 split-button 的真实行为；
- RDP/本地桌面切换；
- 前后台窗口和焦点；
- Bot 与 AliWorkbench 的进程完整性/权限关系；
- Windows 对物理输入注入的拒绝；
- 同时存在多个 seller/recent.html session 时的完整运行态。

所以“CI 全绿”只能证明代码可编译且仓库断言成立，不能证明每一种真实 Windows/千牛状态下都能完成物理发送。

### 4.4 `Access Denied` 的精确 Windows 根因尚需新日志确认

PR #124 已开始记录 exception type 与 HResult。当前已有证据只足够确认：

- UIA 控件可以被找到；
- 草稿可以被读取/写入；
- 物理坐标点击 API 在执行阶段返回“拒绝访问”。

这与 Windows 输入注入的权限/完整性级别、桌面/session 状态等因素相符，但在拿到新的 exception type/HResult 与进程权限诊断之前，不应把 UIPI 或某一个具体安全边界写成已确认唯一根因。

---

## 5. 后续必须执行的长期修复方向

### 5.1 将发送链固定成显式状态机

日志和实现应统一阶段名：

`ConversationVerified -> DraftWritten -> DraftVerified -> SendControlResolved -> PrimaryActionAttempted -> FallbackActionAttempted -> EchoConfirmed/Failed`

任何“自动回复没发出”必须可以从日志直接定位到唯一阶段。

### 5.2 记录发送 backend 与失败分类

每次发送至少记录：

- seller / buyer（生产日志遵守隐私规则）；
- seller HWND / AliWorkbench PID；
- send control AutomationId 与 BoundingRectangle；
- backend：`coordinate-main-area` / `uia-invoke-fallback`；
- action 是否真正开始；
- exception type / HResult；
- 输入框清空状态；
- 卖家回显状态；
- 是否发生 retry、是否复用 owned draft。

不要再只输出一个笼统的 `result=False`。

### 5.3 增加 session-scoped backend 健康状态

如果同一 AliWorkbench PID/HWND 在短时间内连续出现同一“坐标输入被系统拒绝”，可以考虑在本次千牛进程生命周期内把 coordinate backend 标记为 degraded，后续发送在完成 buyer + owned draft 双重确认后直接使用已验证的 UIA fallback，避免每条消息先重复撞一次相同 AccessDenied。

该优化只能在有足够实机日志证明 UIA fallback 稳定后启用，并且 HWND/PID/会话变化后必须重新探测，不能永久记忆。

### 5.4 增加本机权限/桌面诊断

在不改变用户权限的前提下，诊断日志应记录 Bot 与 AliWorkbench 的：

- PID；
- 是否管理员/提升运行；
- Windows session ID；
- 当前 active desktop/交互状态（能安全获得时）；
- 发生 AccessDenied 时的 exception type/HResult。

目的是确认“拒绝访问”到底来自完整性级别、RDP/session、输入 API 本身还是其他 Win32 限制，而不是继续猜测。

### 5.5 继续被动研究普通聊天 IMSDK 直发

长期最理想方案仍然是找到当前新版千牛中真实、稳定、普通文本聊天语义的直接发送接口，减少对桌面自动化的依赖。

在满足 `docs/IMSDK_DIRECT_SEND_DISCOVERY.md` 中的验证门槛前，未知 IMSDK 方法只能被动枚举/观察，禁止自动调用。

---

## 6. 回归与验收标准

修改任何发送实现后，至少必须同时满足：

1. 不恢复物理 Enter。
2. 不使用固定屏幕坐标。
3. 所有 UIA/坐标均限定到当前 seller HWND。
4. 发送前确认当前 buyer。
5. 非空输入框只有完全确认是 owned draft 时才能接管。
6. 一个发送动作已经可能成功时，不允许为了“确认不及时”盲目执行第二种发送动作。
7. 首条咨询与下单自动回复必须复用同一可靠发送链，不单独复制一套点击逻辑。
8. 卖家真实消息回显优先作为送达确认。
9. Windows CI、静态回归和完整 x64 Release 打包必须通过。
10. 正式宣称“发送问题彻底解决”之前，至少还需要真实千牛环境覆盖：前台、后台/RDP、重启后、多会话切换、首条咨询、下单自动回复以及一次故障 fallback 场景。

---

## 7. Bot 是否会自动修改自己的源代码

### 当前结论：不会

当前正式 Windows Bot 客户端没有发现“运行时自动修改 GitHub 源码、自动创建提交/分支/PR、自动调用 git 或 GitHub API 修代码”的生产逻辑。

需要区分三个完全不同的概念：

1. **Bot 自动更新**：客户端可以检查正式版本、下载经过 SHA-256 校验的 `qianniu-bot-x64.zip`，备份当前程序/用户数据，然后替换已安装的二进制并在启动失败时回滚。它是在更新程序文件，不是在运行时编辑 C# 源码。
2. **GitHub Actions 编译/发布**：当前 `windows-build.yml` 只有 `contents: read`，负责测试、编译和上传 artifact；`publish-bot-auto-update-release.yml` 有 `contents: write`，但用途是创建 tag/Release 和上传 `qianniu-bot-x64.zip`、`update.json`，不是修改生产源代码。
3. **开发期间的一次性 Agent/Actions 补丁**：历史上为了执行受控修复，可能临时创建过能提交补丁的 helper/workflow；例如 PR #124 开发过程中出现过 `[agent-send-fix-applied]` 提交。但该一次性 helper/workflow 随后已删除，它属于开发/仓库自动化，不属于 Bot.exe 的运行时自修改能力。

因此，如果未来要增加“Bot 发现故障后自动改代码并发布”的能力，必须作为一个新的、高风险控制面功能单独设计，不能误认为当前客户端已经具备。

推荐保持当前边界：**Bot 可以自动诊断、自动降级、自动上报故障证据、自动更新已审核的正式包；不要让生产客服进程直接持有 GitHub 写权限并自行修改源码。**

---

## 8. 相关 PR / 文档

- PR #95：seller-bound Qianniu HWND UIA send
- PR #104：restart/CDP authoritative session recovery
- PR #111：runtime send hardening + first/order auto replies
- PR #114：Enter primary send
- PR #115：nonblocking CDP/UIA send
- PR #116：seller-scoped UIA primary send + IMSDK discovery
- PR #118：split-button left main-area send
- PR #124：coordinate AccessDenied -> guarded UIA fallback
- `docs/IMSDK_DIRECT_SEND_DISCOVERY.md`
- `docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`
- `docs/PROJECT_HANDOFF_CONTEXT.md`
