# 多店铺 PR 3：业务数据、运行状态与云同步隔离

日期：2026-08-05  
基线：PR #82 合并提交 `cddd1544a9d4beb18a75bd3c044c176026bb5ad2`

## 1. 本阶段目标

本阶段在 PR #81 的 ShopKey/目录底座和 PR #82 的令牌/AI 配置隔离之上，把剩余业务数据和运行链路改为明确的店铺边界：

- 知识库、规则、消息策略、通知配置和授权状态；
- 业务策略 JSON 与转人工规则文件；
- 会话时间线、买家别名、知识学习、回复去重、进度与发送回显状态；
- Bot Web 同步队列、远程开关、命令结果和已处理命令；
- 知识云同步 revision/hash/备份；
- 企业微信应用消息通知与人工回复领取；
- 云备份、换机恢复与旧全局数据迁移；
- 店铺日志与仍依赖旧 `PathEx.DataDir` 的兼容文件。

## 2. 店铺目录

```text
%LocalAppData%\QianniuAiBot\
├── global\
│   └── shops.json
├── data\                         # 旧全局数据，只作为迁移源/单店兼容
└── shops\<ShopKey>\
    ├── profile.json
    ├── config\
    │   ├── settings.json         # DPAPI CurrentUser + ShopKey
    │   └── control-plane-token.json
    ├── rules\
    │   ├── business-policy.json
    │   └── handoff-policy.json
    ├── state\
    │   ├── data\                 # 旧 DataDir 调用的店铺兼容根
    │   ├── legacy-data-migration.json
    │   └── handoff-policy-server-migration.json
    ├── logs\runtime.txt
    ├── backup\
    ├── knowledge\
    └── cache\
```

`PathEx.GlobalDataDir` 始终返回旧进程级目录，迁移代码必须显式使用它。`PathEx.DataDir` 在 `ShopSettingsScope` 中通过 `ScopedDataPathRouter` 自动映射到当前店铺的 `state/data`，使尚未逐个改写的旧文件调用也不会继续写全局目录。

## 3. 加密设置作用域

`ShopScopedParamBridge` 只允许以下明确作用域进入店铺加密文件：

```text
ai
feature
shop-cloud
shop-runtime
```

其中：

- `ai`：模型、接口、API Key、提示词、调度策略；
- `feature`：知识库、规则、消息策略、通知渠道凭据、授权与合规状态；
- `shop-cloud`：每店铺知识云/Web 同步开关、revision、hash、命令状态；
- `shop-runtime`：Bot 启用、自动回复等店铺运行开关。

所有值仍整体使用 DPAPI `CurrentUser` 加密，附加熵包含 ShopKey。新增逻辑导出/恢复接口只在内存中处理明文，最终仍由本机 DPAPI 重新加密。

## 4. UI 作用域

`ShopScopedUiBridge` 使用窗口所有者关系与 `WndAssist.Desk.WndTitle` 关联 ShopContext。WPF 的 Loaded、Button/Menu 点击和键盘操作会在实例事件处理前短暂进入该 ShopContext，并在当前 Dispatcher 操作结束后释放。

这样知识库、规则、消息策略、通知、授权和数据管理窗口无需设置进程级“当前店铺”，也不会因为同时打开两个店铺设置窗口而互相覆盖。

## 5. 规则与业务策略

`BusinessPolicyProfileService` 和 `HandoffRuleRemoteConfigService` 已改为：

- 文件位于当前店铺 `rules` 目录；
- 缓存按完整文件路径分组，不再是一份进程级静态策略；
- 修改前备份写入本店 `backup`；
- 只有注册表中恰好一个店铺时，才允许自动继承旧全局规则；
- 多店铺环境必须在当前店铺显式迁移，禁止把同一旧规则静默复制给所有店。

## 6. 会话和运行内存

以下短期内存键均加入 ShopKey：

- 会话时间线和撤回记录；
- 商品链接预设回复；
- 买家内部 nick/display/targetId 别名；
- 知识学习来源、发送阻断和人工回复绕过；
- 已发送答案去重；
- 回复进度卡和发送结果；
- 真实发送回显 Watchdog；
- 转人工通知冷却。

异步历史刷新、知识学习、通知和发送回显检查都会捕获并重新进入原店铺 ShopContext。远程历史、Web 回复、企业微信人工回复和发送回显检查不再退回 `QN.CurQN`。

## 7. Bot Web 同步

每个 ShopKey 拥有独立的：

- DPAPI 客户端令牌；
- 远程暂停状态；
- 消息同步开关；
- Web 人工回复开关；
- 同步间隔；
- 待同步消息队列；
- 命令结果队列；
- 已处理命令集合。

状态上报只包含属于该 ShopKey 的在线客服。远程 `send_text` 只能在同一 ShopKey 的千牛实例中匹配卖家，不能回退当前全局窗口。请求同时携带 Bearer 令牌和 `X-Shop-Key`。

## 8. 知识云同步

知识云同步按 ShopKey 独立维护：

- 启用状态；
- revision；
- content hash；
- 并发锁；
- 页面状态控件；
- 云端应用前本店备份。

定时器枚举店铺档案，每个店铺使用自己的客户端令牌。云端知识只会写入该店铺的 `feature` 加密设置。

## 9. 企业微信应用消息

通知和人工回复轮询按在线 ShopKey 分组：

- 每店使用自己的客户端令牌；
- 请求携带 `X-Shop-Key`；
- 回复任务只在该 ShopKey 的千牛实例中查找卖家；
- 发送和知识学习在对应 ShopContext 内执行；
- 完成结果回报也绑定同一 ShopKey。

## 10. 本店云备份与换机

云备份 schema：

```text
qianniu-ai-bot.shop-data-backup / version 2
```

备份包只包含当前 ShopKey：

- 逻辑设置（不复制 DPAPI 密文）；
- 本店知识、规则和业务文件。

明确排除：

- Bot 客户端令牌；
- 原始 `config/settings.json`；
- `profile.json` 和全局 `shops.json`；
- 日志、缓存、临时文件；
- revision/hash、远程暂停和已处理命令等瞬时云状态；
- 其他店铺目录。

包使用本店令牌 + ShopKey 派生密钥，格式升级为 `QABK2`。恢复前校验 manifest ShopKey；跨店恢复直接拒绝。新电脑恢复逻辑设置时使用新电脑当前 Windows 用户的 DPAPI 重新加密。

## 11. 旧数据迁移

### 自动迁移

仅当 `shops.json` 中恰好一个店铺时，允许自动继承：

- 旧全局 `ai` / `feature` 配置；
- 旧自动回复开关；
- 旧业务文件；
- 旧业务策略和转人工规则。

### 多店铺显式迁移

若检测到多个店铺，自动迁移关闭。“店铺绑定”页提供“将旧全局数据迁移到本店”按钮，由用户确认归属后执行。

永不迁移：

- 旧全局 Bot 客户端令牌；
- 云 revision/hash/已处理命令；
- 远程暂停；
- 设备身份；
- 日志、缓存、临时文件。

迁移前在本店 `backup` 写入源数据清单；完成后写入 `state/legacy-data-migration.json`，重复运行默认不覆盖现有本店数据。

旧服务端转人工规则迁移也改为每 ShopKey 独立令牌和独立 marker。

## 12. 日志边界

进程级主日志继续保留，用于启动、崩溃和跨店基础设施诊断。在 ShopContext 内产生的日志同时镜像到：

```text
shops\<ShopKey>\logs\runtime.txt
```

店铺日志不会包含令牌、API Key 或完整加密载荷。

## 13. 有意保留的全局对象

以下属于程序基础设施，不是店铺业务数据，继续保持进程级：

- 统一 API 服务地址；
- 程序版本、进程 ID、启动时间；
- 崩溃诊断主日志；
- QN 在线实例注册集合；
- Windows 全局安装/更新信息。

它们不会作为某店铺知识、规则、消息、令牌或云状态使用。

## 14. 验证重点

静态和 Windows CI 必须覆盖：

- DataDir 环境路由与 GlobalDataDir 明确迁移源；
- DPAPI 设置作用域白名单；
- 策略文件路径和缓存键；
- 会话/学习/去重/进度/Watchdog 的 ShopKey；
- Web、知识云、企业微信的独立令牌和 `X-Shop-Key`；
- Web/通知/历史/Watchdog 无 `QN.CurQN` 回退；
- 云备份不包含令牌和 DPAPI 原文件，且拒绝跨店恢复；
- 单店自动迁移与多店显式迁移；
- Debug/x64 和 Release/x64 完整运行包构建。
