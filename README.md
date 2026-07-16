# 千牛 AI 客服机器人

一个面向 Windows 千牛接待台的 AI 客服辅助项目。当前生产代码集成了消息接收、AI 回复、自动发送、多 API、知识库、自动回复规则、日志与运行状态等能力，并保留独立的 UIA / IMSDK 研究分支用于继续验证更稳定的千牛消息链路。

> 当前项目仍处于持续开发和验证阶段。涉及千牛客户端自动化的能力可能随客户端版本变化，请先在测试账号和测试会话中验证。

## 当前主要能力

- 接收千牛聊天消息并识别当前买家/卖家会话
- 调用 OpenAI 兼容接口生成客服回复
- 支持多个 AI 接口和失败切换
- 支持自动回复开关和人工确认规则
- 支持店铺/商品知识库
- 支持 AI 智能导入知识库：粘贴文本、HTML、图片等资料后自动整理为问答
- 支持知识库搜索、编辑、删除、JSON 导入导出
- 支持文本和图片发送
- 支持运行日志、接口状态和连接诊断
- 支持千牛简体中文环境修复相关逻辑

## 当前生产消息链路

`master` 分支当前使用的是一套混合架构，而不是单一技术路径。

### 收消息

主要链路：

```text
千牛聊天页面 / 注入脚本
        ↓
本地 WebSocket 127.0.0.1:41010
        ↓
CDPClient / QN 消息事件
        ↓
识别买家消息
        ↓
MyOpenAI 生成回复
        ↓
人工展示或自动发送
```

相关核心代码：

```text
src/Bot/ChromeNs/MyWebSocketServer.cs
src/Bot/ChromeNs/CDPClient.cs
src/Bot/ChromeNs/QN.cs
src/Bot/ChromeNs/MyOpenAI.cs
```

### 发消息

新版千牛主要通过 `QNRpa` 完成发送：

```text
确认目标买家会话
        ↓
FlaUI / UIA 定位千牛窗口和输入框
        ↓
写入回复文本
        ↓
优先按 Enter 发送
        ↓
失败时尝试发送按钮正文区域
        ↓
通过输入框清空或卖家消息回显确认发送结果
```

相关代码：

```text
src/Bot/ChromeNs/QNRpa.cs
src/Bot/ChromeNs/QN.cs
```

因此，`master` 目前已经使用 UI Automation/FlaUI 参与发送，但生产环境的消息接收主链路仍主要依赖注入脚本、WebSocket 和 CDP 事件。

## AI 知识库

知识库入口位于 Bot 设置菜单中的“知识库”。

当前支持：

- 智能导入文本和复制内容
- 解析 HTML 文本及媒体信息
- 图片多模态分析；模型不支持图片时自动降级到纯文本
- 检测视频并提示跳过或取消
- AI 自动生成分类、问题、答案和关键词
- 自动按问题去重
- 问答列表搜索和分类筛选
- 手工新增、编辑、启用/停用和删除
- JSON 追加或覆盖导入

核心代码：

```text
src/Bot/Knowledge/
src/Bot/Options/CtlRobotOptions.xaml.cs
src/Bot/ChromeNs/MyOpenAI.cs
```

## UIA 消息生命周期研究分支

研究分支：

```text
agent/message-lifecycle-evidence-v2-local
```

这个分支目前没有合并进 `master` 的生产 Bot 主流程。

它主要位于：

```text
tools/qn_discovery_lab/
```

主要研究内容包括：

- 通过 Windows UI Automation 读取当前已加载的千牛消息节点
- 判断 incoming / outgoing 消息方向
- 为消息生成稳定的隐私安全 `message_key`
- 对相同消息进行去重
- 比较消息快照的 added / updated / unchanged / not_observed / reobserved 生命周期
- 识别买家撤回和卖家撤回的候选及高置信度证据
- 通过受保护的发送探针验证输入框写入和单次 Enter 发送

需要注意：

- UIA v2 的消息提取和生命周期层目前仍属于研究/验证代码
- 生命周期模块本身是只读的，不调用 AI，也不会主动发送消息
- 单独的发送探针默认 dry-run，真实发送需要显式确认参数
- 当前还没有把这套 UIA v2 watcher / lifecycle adapter 接入 `src/Bot` 的自动回复主链路

研究文档：

```text
docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md
docs/QIANNIU_MESSAGE_LIFECYCLE_VALIDATION.md
```

## 推荐的后续生产架构

短期建议保留 `master` 现有可工作的生产链路，同时把 UIA v2 以 Shadow Mode 接入：

```text
现有 CDP/WebSocket 收消息 ───────→ 生产自动回复
             │
             └──────────────→ UIA v2 旁路观察和比对

UIA v2 watcher
    ↓
稳定 message_key + 生命周期去重
    ↓
只记录差异，不触发自动回复
    ↓
验证稳定后逐步切换为备用接收源
```

经过足够真实环境验证后，再考虑：

1. 将 UIA v2 封装成 C# `QianniuUiaMessageAdapter`
2. 先作为生产 Bot 的 Shadow Mode 旁路
3. 验证当前会话识别、历史消息初始化和重观察行为
4. 只把新的 buyer `user_text` 事件标记为可处理
5. 与现有 CDP 消息按稳定键进行交叉去重
6. 最后再决定是否将 UIA 提升为主接收链路或故障备用链路

## 环境要求

- Windows 10/11
- .NET Framework 4.8
- 千牛 Windows 客户端
- 千牛无障碍/辅助功能可被 Windows UI Automation 访问
- 至少一个可用的 OpenAI 兼容 AI 接口

项目主要面向当前已验证的千牛新版环境。千牛升级后，AutomationId、控件结构或注入环境可能发生变化。

## 获取最新构建

推荐从 GitHub Actions 下载完整构建产物，而不是只下载单个 `Bot.exe`。

操作路径：

```text
GitHub → Actions → Windows CI → 最新成功运行 → Artifacts
```

下载：

```text
qianniu-bot-bin
```

解压整个目录后运行 `Bot.exe`。

构建日志产物：

```text
qianniu-bot-build-logs
```

> 当前 CI 不再自动把编译结果和日志提交回 `master`，避免 CI 自己修改分支、产生重复构建和并发冲突。`src/Bin` 中已提交的文件不一定代表最新一次 CI 构建，请优先使用 Actions Artifact。

## GitHub Actions 工作流

主工作流：

```text
.github/workflows/build-windows.yml
```

触发条件：

- Pull Request → `master`
- Push → `master`
- 手动 `workflow_dispatch`

工作流执行：

```text
Checkout
  ↓
准备旧项目兼容项
  ↓
NuGet Restore
  ↓
MSBuild Debug / x64
  ↓
验证 src/Bin/Bot.exe
  ↓
上传 qianniu-bot-bin
  ↓
上传 qianniu-bot-build-logs
```

同一分支的新构建会取消旧的未完成构建，避免并发 Actions 相互覆盖。

## 本地编译

在安装了 Visual Studio Build Tools / MSBuild 和 NuGet 的 Windows 环境中：

```powershell
nuget restore src\Bot.sln -NonInteractive
msbuild src\Bot.sln /m /t:Rebuild /p:Configuration=Debug /p:Platform=x64
```

主要输出目录：

```text
src/Bin/
```

## 推荐开发流程

```text
从 master 创建功能分支
        ↓
开发并提交代码
        ↓
创建 Pull Request 到 master
        ↓
Windows CI 自动编译
        ↓
下载 qianniu-bot-bin 实机验证
        ↓
确认功能和千牛兼容性
        ↓
合并到 master
```

对于消息链路、UIA、IMSDK、Frida 等探索性工作，建议继续放在独立 `agent/*` 分支和 `tools/qn_discovery_lab`，在端到端验证完成之前不要直接接入生产自动回复链路。

## 目录结构

```text
src/Bot/                         WPF 主程序
src/Bot/ChromeNs/                千牛连接、CDP、AI 和发送链路
src/Bot/Knowledge/               AI 知识库
src/Bot/Automation/              Windows 自动化相关代码
src/BotLib/                      通用基础库
src/DbEntity/                    数据实体
src/Bin/                         本地/历史构建输出
tools/qn_discovery_lab/          千牛 UIA / IMSDK / Frida 研究工具
docs/                            研究进度与交接文档
.github/workflows/               GitHub Actions
```

## 使用提醒

1. 建议先使用测试店铺和测试会话验证自动回复。
2. 自动发送前确认当前会话识别准确。
3. 退款、投诉、赔偿、订单隐私等高风险问题建议保持人工确认。
4. 不要把研究探针直接当作生产发送链路使用。
5. 千牛版本变化后，应重新验证消息接收、输入框定位和发送确认逻辑。

## 免责声明

本项目仅供学习、研究和技术交流。使用者应自行确认符合淘宝/千牛平台规则、当地法律法规、用户隐私要求以及第三方 AI 服务条款。

项目不保证在所有千牛版本、账号环境或系统配置下持续可用。
