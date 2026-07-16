# 项目完整交接上下文

最后更新：2026-07-16

## 2026-07-16 消息生命周期 v2 交接补充

`tools/qn_discovery_lab` 已增加隐私安全的语义提取、生命周期比较和证据 CLI。实现保持在发现实验层，尚未接入生产 Bot。

真实证据已确认买家和卖家撤回均为同一稳定消息节点上的原地变化；卖家撤回后的几何方向可能误判为 incoming，因此必须依赖 prior direction、稳定键、节点身份、内容摘要变化和撤回语义。相同正文不能作为身份。

本轮新的空闲只读验证中，三次快照的 24 个 `message_key`、节点身份和 observation 均稳定。由于当前 UIA provider 未暴露消息列表 ScrollPattern，且无法百分之百确认已选会话行，滚动和重新聚焦阶段安全跳过，总体为 PARTIAL。详细规则和未验证范围见 `docs/QIANNIU_MESSAGE_LIFECYCLE_VALIDATION.md`。

本文件用于新聊天、新 AI Agent、Codex 或新开发者直接接续开发。开始任何修改前，必须依次阅读：

1. `AGENTS.md`
2. 本文件 `docs/PROJECT_HANDOFF_CONTEXT.md`
3. `docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`

不要重复已经被验证为低价值或错误方向的实验，除非出现新的直接证据。

---

## 1. 当前项目目标

项目总体目标是维护并继续开发一个面向淘宝千牛的 Windows 桌面 AI 客服机器人，当前有两条并行主线：

### 1.1 业务主线：AI 知识库中心

目标是让用户能够把店铺资料、商品资料、售后规则、聊天记录、图片等内容导入，调用已配置的 AI 接口整理为结构化客服问答知识库，并在桌面端完成搜索、分类、增删改、导入和导出。

### 1.2 自动化主线：千牛聊天结构化读取与发送

目标是获得一个可靠的千牛聊天自动化链路：

1. 识别当前选中的联系人或会话。
2. 读取当前会话中已加载的结构化消息。
3. 将回复写入聊天输入框。
4. 仅发送一次。
5. 从聊天控件树验证发送结果。
6. 验证完成后，再接入现有 AI 回复和知识库逻辑。

近期已确定的可落地路径是 **Windows UI Automation**，不是截图、OCR 或固定坐标点击。

长期仍可并行研究 `MessageSDKBiz.dll`、`MessageSDKModel.dll`、`aim.dll` 等内部消息模块，以获得后台消息流或更稳定的内部接口。

---

## 2. 仓库与分支状态

- GitHub 仓库：`b8vipvip/qianniu-ai-bot`
- 默认分支：`master`
- 本地常用目录：`C:\qianniu-ai-bot`
- 交接生成前已确认的远端 `master` 最新提交：`8416141a0578a9e36ca9b5a09be788edce4d2b9d`
- 该提交是在 UIA 研究文档合并提交 `699c8e2d32c5e0e738fec6467d453a23caf8e9ce` 之后自动更新编译产物的提交。

已经合并进 `master` 的重要改动：

- PR #3：AI 知识库中心与智能导入。
- 后续知识库命名空间修复提交：
  - `126cd744db450297f90296d232021a1721f0f5a9`
  - `c0d5b7dc8bc3c002277509beb69110880f01773a`
- PR #4：千牛 UIA 研究文档、Agent 约束和安全发送探针。
- PR #4 合并提交：`699c8e2d32c5e0e738fec6467d453a23caf8e9ce`

后续开发不要从旧实验分支直接覆盖 `master`。应从最新 `origin/master` 创建新分支，完成验证后再合并。

---

## 3. 技术栈与解决方案架构

### 3.1 解决方案

解决方案文件：

```text
src/Bot.sln
```

包含三个项目：

```text
src/Bot/       主 Windows WPF 客户端
src/BotLib/    公共业务和辅助逻辑
src/DbEntity/  数据实体和持久化相关代码
```

### 3.2 主程序

主项目文件：

```text
src/Bot/Bot.csproj
```

已确认属性：

- WPF `WinExe`
- .NET Framework 4.8
- C# 7.3 配置
- 启动入口：`Bot.SingleStartUp.StartUp`
- 依赖 `BotLib` 和 `DbEntity`
- 已引用 FlaUI、UIA3、`Interop.UIAutomationClient`、Newtonsoft.Json、OpenAI 客户端等组件

主要目录职责：

```text
src/Bot/AssistWindow/   主辅助窗口、机器人面板和右侧功能区
src/Bot/Automation/     千牛窗口、账号、事件和 UI 自动化相关逻辑
src/Bot/ChromeNs/       WebSocket、AI 接口和浏览器交互逻辑
src/Bot/Common/         通用窗口、数据库帮助、实体帮助等
src/Bot/ControllerNs/   桌面扫描和控制器逻辑
src/Bot/Knowledge/      新增的知识库中心
src/Bot/Options/        设置、AI 接口和机器人选项
src/Bot/StartUp/        启动和生命周期
src/Bin/                编译产物和内嵌脚本来源
```

主项目会把以下脚本作为嵌入资源：

```text
src/Bin/inject.js
src/Bin/language.js
```

现有简体中文和 `zh-CN` 修复属于受保护功能。研究聊天自动化时，不得修改启动、语言、locale 或中文文本修复逻辑。

---

## 4. 已完成：AI 知识库中心

PR #3 修改或新增了以下文件：

```text
src/Bot/AssistWindow/Widget/RightPanel.xaml.cs
src/Bot/Bot.csproj
src/Bot/ChromeNs/MyOpenAI.cs
src/Bot/Knowledge/KnowledgeAiService.cs
src/Bot/Knowledge/KnowledgeCenterWindow.cs
src/Bot/Knowledge/KnowledgeClipboardParser.cs
src/Bot/Knowledge/KnowledgeEditWindow.cs
src/Bot/Knowledge/KnowledgeImportControl.cs
src/Bot/Knowledge/KnowledgeManagerControl.cs
src/Bot/Options/CtlRobotOptions.xaml.cs
```

### 4.1 入口和窗口

`KnowledgeCenterWindow` 是知识库主窗口，标题为“AI客服 - 知识库”，包含两个标签页：

- 智能导入
- 问答管理

`RightPanel.xaml.cs` 已把原有“知识库”菜单切换为打开 `KnowledgeCenterWindow`。

### 4.2 智能导入

`KnowledgeAiService` 当前实现：

- 文本按最多 12000 字符分批。
- 图片按每批最多 5 张分批。
- 调用 `MyOpenAI.CallStructuredChat`。
- 要求 AI 返回固定 JSON：`faqs` 数组。
- 首次 JSON 解析失败后，会进行一次格式修复请求。
- 不支持视觉模型时回退到纯文本分析。
- 自动规范化问题文本并去重。
- 写入现有 `BotFeatureStore` 知识库。
- 不直接处理视频；视频计入跳过统计。

系统提示词明确禁止编造价格、库存、发货时效、物流时效和售后承诺。

### 4.3 知识管理

`KnowledgeManagerControl` 当前支持：

- 按分类筛选。
- 按问题、答案、关键词搜索。
- 新增、编辑和删除问答。
- JSON 导入。
- JSON 导出。
- 追加导入时按规范化问题去重。

### 4.4 已修复的知识库编译问题

知识库合并后出现了模型和存储类型命名空间未导入的问题，已通过补充：

```csharp
using Bot.ChromeNs;
```

修复到：

```text
src/Bot/Knowledge/KnowledgeEditWindow.cs
src/Bot/Knowledge/KnowledgeManagerControl.cs
```

后续 Agent 不要删除这些导入，除非先移动相关类型并完成全量编译验证。

### 4.5 知识库仍需回归验证

代码已合并且后续 CI 已生成编译产物，但仍应进行完整人工回归：

1. 打开“知识库”入口。
2. 纯文本智能导入。
3. 图片智能导入以及不支持视觉模型时的回退。
4. AI 返回错误 JSON 时的修复流程。
5. 重复问题去重。
6. 新增、编辑、删除。
7. JSON 导入和导出。
8. 无 AI 接口配置时的错误提示。
9. 大文本和多图分批。
10. 确认原有机器人设置和主窗口功能没有回归。

不要在未手动验证的情况下宣称知识库全部功能已通过端到端测试。

---

## 5. 已完成：千牛聊天自动化研究

完整实验记录见：

```text
docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md
```

### 5.1 已确认运行环境

- 千牛 / AliWorkbench：`9.97.56N`
- 安装目录：`C:\Program Files\AliWorkbench\9.97.56N`
- CEF / Chromium 130
- 语言参数：`--lang=zh-CN`
- 主聊天 Renderer 标记：`--render_id=bench_im-...`
- Renderer PID 会变化，严禁固定 PID

`Resources/config/render_id.json` 将以下页面映射到 `bench_im`：

```text
https://alires-webui/web_msg-center/index.html
https://alires-webui/web_chat-packer/recent.html...
https://alires-webui/Message/message-notify.html
```

### 5.2 UI Automation 已取得的实质成果

使用：

```text
uiautomation==2.0.29
```

已确认千牛无障碍树暴露主聊天结构：

```text
窗口名称：千牛接待台
窗口类名：MutilChatView
聊天文档：千牛消息聊天
消息列表 AutomationId：J_msg_list
```

已能读取当前已加载的：

- 发送者或账号名称
- 时间戳
- 文本消息正文
- 商品标题、价格和 URL
- 消息节点 AutomationId，例如 `*.PNM`

因此，读取聊天信息不需要 OCR 或截图。

### 5.3 已确认输入框

稳定 AutomationId：

```text
UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.chatInputArea.plainTextEdit
```

已成功完成：

1. 读取原值。
2. 写入唯一测试文本。
3. 通过 `ValuePattern` 读回相同文本。
4. 恢复原值。
5. 不发送。

### 5.4 已确认发送按钮

稳定 AutomationId：

```text
UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.enterAreaKeyWidget.sendMsg
```

按钮名称：

```text
发送
```

已定位，但截至本交接文件生成时，还没有完成“真实调用一次并从消息树验证”的最终人工测试。

### 5.5 已提交的安全发送探针

文件：

```text
tools/qn_discovery_lab/qn_uia_send_probe.py
```

设计约束：

- 默认 dry-run。
- 真实发送必须同时提供：

```text
--send --confirm SEND_TO_CURRENT_CHAT
```

- 明确提示发送到当前选中的聊天。
- 倒计时后调用。
- 只调用一次发送按钮。
- 绝不自动重试。
- 发送后检查输入框状态和 UIA 消息树。
- 预发送失败时恢复原输入。
- 默认不输出完整私人聊天记录。

---

## 6. 已确认的错误方向和低价值实验

以下内容已经测试过，不应作为下一步默认方向重复执行。

### 6.1 Windmill Bridge 不是已确认的主聊天发送 API

静态资源中发现：

```text
internal.chat.selectAndSendText
```

但它位于 `shareTextMsg` 分支，更像 Windmill 小程序“分享文字到聊天”的功能。

在主聊天 `bench_im` Renderer 中没有动态观察到：

```text
selectAndSendText
internal.chat
ddExec
```

不得把它写成已确认的主聊天发送 API。

### 6.2 `AlipayJSBridge` 内存字符串不是对象句柄

Frida 扫描发现的 `AlipayJSBridge` 地址只是原始字符串命中，不是已验证的 V8 对象或函数指针。

禁止继续把该地址当作 `window.AlipayJSBridge` 对象解引用或调用。

### 6.3 三个 ExecuteJavaScript Hook 没有命中手工聊天操作

已 Hook：

```text
WebControl::ExecuteJavaScript
WebControl::ExecuteJavaScriptFunction
webapp::mojom::WebViewProxy::ExecuteJavaScript
```

切换联系人、滚动和手动发送时没有触发。

正确结论仅是：所测试的手工聊天路径没有经过这些函数。不能因此直接调用它们，也不能在缺少对象、ABI、线程和上下文时盲调 C++ 方法。

### 6.4 CEF/V8 发布符号已裁剪

以下符号搜索结果为 0：

```text
*CefV8Context*
*CefFrame*
*ExecuteFunction*
```

不要重复进行同类宽泛符号搜索，除非获得新的模块、符号文件或调用路径证据。

### 6.5 宽泛扫描 CEF 缓存价值低

Chromium Cache、Code Cache、Service Worker CacheStorage、LevelDB 和 GPU Cache 是二进制或结构化格式。直接对所有缓存文件运行 `findstr` / `Select-String` 会产生错误和噪音。

只有在引入对应格式解析器时才继续。

### 6.6 不要优先使用像素级 RPA

千牛已经暴露稳定 UIA 控件和 AutomationId，因此不要退化为：

- 固定坐标点击
- 截图模板匹配
- OCR 读取正文

UIA 失败时可以作为最终兜底，但不是当前主方案。

---

## 7. 内部消息模块研究结论

`bench_im` Renderer 已确认加载：

```text
MessageSDKBiz.dll
MessageSDKModel.dll
message_support.dll
aim.dll
AppBiz.dll
AssistIPC.dll
AssistIPC_shared.dll
syncsdkbiz.dll
ipc.dll
ipc_mojom.dll
WebApp.dll
WebView.dll
prgdb.dll
FTSEngine.dll
libaccs.dll
wtnet.dll
```

这证明客户端内部存在结构化消息管线，但不证明存在官方开放或第三方稳定 API。

长期研究优先级：

1. `MessageSDKBiz.dll`
2. `MessageSDKModel.dll`
3. `aim.dll`
4. IPC / Mojo 边界
5. 本地数据库和索引
6. 序列化前或解密后的网络明文

在 UIA 端到端链路完成前，不要让原生逆向阻塞可用版本落地。

---

## 8. 已确认的开发环境问题

### 8.1 `pywinauto` / `pywin32` 不可用

本机 Python 3.10 中，即使创建全新虚拟环境，导入 `win32ui` 仍失败：

```text
ImportError: DLL load failed while importing win32ui
```

因此当前实验不再使用 `pywinauto`。

已验证工作方案：

```text
uiautomation==2.0.29
comtypes
```

虚拟环境：

```text
tools/qn_discovery_lab/.venv-uia
```

虚拟环境不得提交到 Git。

### 8.2 PowerShell / PSReadLine 长粘贴崩溃

本机 PowerShell 在粘贴较长数组或 here-string 时会发生：

```text
System.ArgumentOutOfRangeException
PSConsoleReadLine.ReallyRender
```

后续 Agent 必须：

- 优先直接把脚本提交到仓库。
- 让用户用 `git pull` 或 `git restore` 获取文件。
- 终端命令保持短小。
- 不再给出数百行 PowerShell 数组。
- 不使用未经实际验证的分段 Base64。

### 8.3 权限一致性

UI Automation 脚本与千牛应运行在相同权限级别。千牛如果以管理员运行，探针也应以管理员运行。

---

## 9. 安全和隐私约束

1. 真实发送测试只允许在专门测试会话中进行。
2. 发送前必须由用户手工确认当前选中联系人。
3. 探针只能发送一次，结果不明确时也不得自动重试。
4. 不得把真实客户姓名、账号、聊天正文和商品隐私数据提交到 GitHub。
5. 日志和样本应脱敏。
6. 不要在主 Bot 中自动启用新发送路径，必须先通过独立探针。
7. 保留现有简体中文修复和启动逻辑。
8. 发现内部 API 时，先做只读或 dry-run 验证，再考虑发送。
9. 不要修改 AliWorkbench 安装文件，除非有独立备份、明确恢复方案和用户批准。

---

## 10. 当前未完成任务

按优先级排列：

### P0：同步和基线确认

- 将本地工作区安全同步到最新 `origin/master`。
- 保留本地实验文件和未提交修改的备份。
- 确认新聊天先阅读三份交接文档。

### P1：真实发送探针

在专门测试会话执行：

```powershell
$py="$PWD\.venv-uia\Scripts\python.exe"
$msg="UIA_SEND_PROBE_"+(Get-Date -Format "yyyyMMdd_HHmmss")
& $py .\qn_uia_send_probe.py --message $msg --send --confirm SEND_TO_CURRENT_CHAT
```

成功标准：

- 脚本仅调用一次发送按钮。
- 输入框清空。
- 唯一测试文本出现在 UIA 消息树。
- 没有自动重试。

失败时记录：

- 控件是否存在。
- `InvokePattern` 是否存在。
- 输入框发送后状态。
- 消息树是否刷新。
- 千牛窗口和当前会话是否保持不变。

### P2：结构化消息提取器

新增：

```text
tools/qn_discovery_lab/qn_uia_extract_messages.py
```

输出规范化 JSON，至少包含：

```json
{
  "conversation": {},
  "messages": [
    {
      "node_id": "",
      "sender": "",
      "timestamp": "",
      "direction": "incoming|outgoing|unknown",
      "type": "text|product|image|system|unknown",
      "text": "",
      "visible": true
    }
  ]
}
```

不得把真实样本提交仓库。

### P3：去重和新消息 watcher

- 首选按消息节点 AutomationId 去重。
- 回退键：发送者 + 时间 + 类型 + 内容哈希。
- 仅报告新增消息。
- 第一版只读，不自动发送。
- 对窗口切换、历史消息加载和列表重绘做测试。

### P4：当前联系人和会话识别

发送前必须得到可验证的当前会话标识。需要从主窗口、会话列表、标题或聊天文档中提取：

- 显示名称
- 账号标识（如 UIA 暴露）
- 会话节点 AutomationId
- 当前选中状态

在不能确认当前会话时，禁止自动发送。

### P5：接入现有机器人

只有 P1-P4 全部通过后，才新增正式 UIA Adapter，并接入：

```text
读取新消息
→ 知识库检索/AI 生成
→ 安全策略
→ 写入输入框
→ 发送一次
→ 验证结果
```

正式 Adapter 不应直接散落在窗口代码中，应放入明确的自动化层，并通过接口与现有机器人逻辑解耦。

### P6：知识库回归和增强

- 完成第 4.5 节人工回归。
- 为 JSON 解析、问题规范化和去重增加单元测试或最小可执行测试。
- 检查 AI 接口失败和超时提示。
- 检查图片批处理和不支持视觉模型回退。
- 后续再考虑知识库检索质量和向量检索，不要在基础导入未验证前扩大范围。

### P7：长期原生消息流

- 对 `MessageSDKBiz.dll`、`MessageSDKModel.dll`、`aim.dll` 做字符串、导出、RTTI 和调用边界分析。
- 优先寻找 incoming callback、send serialization 和 status callback。
- 所有 Hook 先只记录元数据，不主动修改消息。
- 不得使用固定偏移作为唯一方案。

---

## 11. 推荐下一步开发顺序

新聊天开始后按以下顺序执行：

1. 同步本地 `master`。
2. 阅读三份交接文档。
3. 检查 `git status`，不要覆盖本地实验资料。
4. 运行 `qn_uia_send_probe.py` dry-run。
5. 在测试会话运行一次真实发送。
6. 将结果写回 `docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`。
7. 开发只读消息提取器。
8. 开发新消息 watcher 和去重。
9. 开发当前会话识别。
10. 完成知识库人工回归。
11. 再设计主程序 UIA Adapter 接口。
12. 原生 MessageSDK 研究作为并行长期任务。

---

## 12. 本地安全同步方案

由于当前聊天无法直接操作用户电脑，本地同步必须在用户 PowerShell 中执行。

先保存本地实验目录副本，避免未跟踪文件与远端文件冲突：

```powershell
cd C:\qianniu-ai-bot
$stamp=Get-Date -Format "yyyyMMdd_HHmmss"
$backup="C:\qianniu-ai-bot-backup-$stamp"
New-Item -ItemType Directory $backup | Out-Null
Copy-Item .\tools\qn_discovery_lab "$backup\qn_discovery_lab" -Recurse -Force
```

查看状态：

```powershell
git branch --show-current
git status --short
```

保存已跟踪修改：

```powershell
git stash push -m "before-master-sync-$stamp"
```

同步：

```powershell
git fetch origin --prune
git switch master
git pull --ff-only origin master
```

确认：

```powershell
git status
git log -5 --oneline
```

说明：

- 上述 `git stash` 默认不包含未跟踪文件，因此不会尝试打包 `.venv-uia`。
- `qn_discovery_lab` 已先复制到仓库外备份目录。
- 不要执行 `git clean -fd`。
- 不要执行 `git reset --hard`，除非已经检查备份且明确要丢弃本地改动。
- 如果 `git switch master` 因未跟踪同名文件冲突，先把冲突文件移动到备份目录，再重试；不要直接删除。

同步完成后，虚拟环境仍可继续使用；若丢失，可重新创建：

```powershell
cd C:\qianniu-ai-bot\tools\qn_discovery_lab
python -m venv .venv-uia
$py="$PWD\.venv-uia\Scripts\python.exe"
& $py -m pip install uiautomation==2.0.29
```

---

## 13. 新聊天启动指令

在新聊天中发送下面这段即可直接继续：

```text
请继续开发 GitHub 仓库 b8vipvip/qianniu-ai-bot。
先读取仓库 master 分支的 AGENTS.md、docs/PROJECT_HANDOFF_CONTEXT.md 和 docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md，严格遵守其中的约束，不要重复已标记为无效的实验。

当前第一优先级：
1. 确认本地 master 与 origin/master 同步；
2. 运行 tools/qn_discovery_lab/qn_uia_send_probe.py 的 dry-run；
3. 在专门测试会话完成一次真实发送并从 UIA 树验证；
4. 成功后开发 qn_uia_extract_messages.py，输出脱敏的结构化 JSON；
5. 暂不修改主 Bot 的启动、语言和 zh-CN 修复逻辑。

用户偏好：所有文件创建和修改优先通过 GitHub 提交或短终端命令完成，不要给长 PowerShell here-string 或大段数组，因为 PSReadLine 会崩溃。
```

---

## 14. 交接时能力状态

| 能力 | 状态 |
|---|---|
| 知识库中心代码合并 | 已完成 |
| 知识库命名空间编译修复 | 已完成 |
| 知识库完整人工回归 | 待完成 |
| 定位千牛主聊天窗口 | 已验证 |
| 读取已加载聊天文本 | 已验证 |
| 读取发送者、时间和商品元数据 | 已验证 |
| 定位输入框 | 已验证 |
| 程序写入并读回输入框 | 已验证 |
| 恢复输入框原内容 | 已验证 |
| 定位发送按钮 | 已验证 |
| 安全发送探针已提交 | 已完成 |
| 真正调用发送一次 | 待用户在测试会话验证 |
| 从 UIA 树确认发送结果 | 待验证 |
| 结构化消息 JSON 提取器 | 未完成 |
| 新消息 watcher | 未完成 |
| 当前联系人/会话识别 | 未完成 |
| 主 Bot UIA Adapter 集成 | 有意延后 |
| 稳定公开或内部聊天 API | 未找到 |
| MessageSDK 后台消息流 | 未完成 |

本文件是项目级交接；千牛逆向的逐项证据和实验细节以 `docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md` 为准。
