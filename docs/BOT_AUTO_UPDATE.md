# Bot 自动更新

## 用户入口

更新后进入：

`设置 -> 关于与更新`

页面显示：

- 当前版本；
- GitHub 最新稳定版本；
- 已跳过的版本；
- 自动检查开关；
- 弹窗通知开关；
- 自动下载安装包开关；
- 检查间隔；
- 检查更新、下载并安装、发布页面等操作。

默认策略：

- 自动检查：开启；
- 新版本弹窗：开启；
- 自动下载：关闭；
- 检查间隔：6 小时；
- 自动安装：始终关闭，必须由用户确认。

Bot 启动约 20 秒后执行第一次后台检查，之后每 30 分钟检查一次是否到达用户配置的检查间隔。GitHub 暂时不可访问时只记录日志，不影响聊天和自动回复。

## 版本来源

客户端只识别仓库：

`b8vipvip/qianniu-ai-bot`

中的正式 GitHub Release，并且只接受：

- 标签以 `bot-v` 开头；
- 非草稿；
- 非预发布；
- 包含稳定资产 `qianniu-bot-x64.zip`；
- 包含校验清单 `update.json`。

正式版本格式：

`1.1.<Windows x64 release build run number>`

例如：

`bot-v1.1.375`

安装包根目录包含 `release-info.json`，Bot 运行时优先使用该文件确定当前正式版本。旧包没有此文件时，使用程序集版本 `1.1.0` 作为兼容基线。

## GitHub 发布流水线

`.github/workflows/publish-bot-auto-update-release.yml` 监听：

`Windows x64 release build`

只有以下条件同时成立才自动发布：

1. 原 x64 Release 工作流成功；
2. 构建分支为 `master`；
3. 完整运行包 Artifact 存在；
4. Artifact 中能找到 `Bin/Bot.exe`；
5. Artifact 中能找到 `Bin/BotAutoUpdater.ps1`。

发布流程：

1. 下载已经通过构建与测试的完整 x64 Artifact；
2. 写入 `release-info.json`；
3. 重新压缩为稳定文件名 `qianniu-bot-x64.zip`；
4. 计算最终 ZIP 的 SHA-256；
5. 生成 `update.json`；
6. 创建 `bot-v*` GitHub Release；
7. 验证两个公开资产均已发布。

也可以在 GitHub Actions 中手动运行发布工作流，它会选择最近一次成功的 `master` x64 Release 构建。

## 下载和安装

安装包下载到：

`%LocalAppData%\QianniuAiBot\updates\<version>\qianniu-bot-x64.zip`

下载完成后必须与 `update.json` 中的 SHA-256 完全一致，否则自动删除并拒绝安装。

用户点击“立即更新”后：

1. 再次提示更新会关闭 Bot；
2. 将 `BotAutoUpdater.ps1` 复制到临时目录；
3. 关闭当前 Bot；
4. 备份现有程序；
5. 备份 `%LocalAppData%\QianniuAiBot\data`；
6. 解压并验证 `Bin/Bot.exe` 和 `release-info.json`；
7. 替换程序文件；
8. 启动新 Bot；
9. 确认新进程持续运行；
10. 失败时恢复程序和永久数据并重新启动旧版本。

默认保留最近 8 个更新备份。

更新日志位置：

`%LocalAppData%\QianniuAiBotUpdater\logs`

备份位置：

`%LocalAppData%\QianniuAiBotUpdater\backups`

## 安全边界

- 客户端不保存 GitHub Token；公开仓库的 Release 和资产可直接读取。
- 不从普通 Actions 临时 Artifact 地址直接更新。
- 不接受其他仓库或任意 URL 的安装包。
- 不接受缺少 SHA-256 清单的自动安装。
- 不自动静默安装；更新前始终需要用户确认。
- 不覆盖 `%LocalAppData%\QianniuAiBot\data` 中的用户配置、知识库和运行数据。
- 安装目录中存在 `.git` 时，更新器拒绝覆盖，防止误删源代码仓库。
