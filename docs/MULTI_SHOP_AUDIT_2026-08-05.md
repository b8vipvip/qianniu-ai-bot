# 多店铺隔离代码审计（PR 1 基线）

日期：2026-08-05  
基线分支：`master`  
基线提交：`ffdf41ff01fc8df8c1cff4394a36553cb5047958`（合并 PR #80）

## 1. 审计结论

当前 Windows Bot 已能同时发现多个 `QN` 实例，但配置、持久化、云同步、Web 控制台同步和云备份仍以一个 Windows 用户下的单一全局作用域运行。复制安装目录不能隔离数据，因为持久数据统一落在 `%LocalAppData%\QianniuAiBot`。

PR 1 只建立稳定身份和路径底座，不改变现有单店铺读写行为。现有 `PathEx.DataDir` 被显式暴露为 `LegacyDataRoot`，后续只能由受控迁移流程读取，避免在底座尚未接入全业务链路前破坏已有用户。

## 2. 店铺身份来源

### 已确认的代码来源

- `CDPClient.GetCurrentUser()` 调用 `im.login.GetCurrentLoginID`，结果反序列化为 `LocalUserResponse`。
- `onChatDlgActive`、`onConversationChange` 和后台消息事件会反序列化 `ActiveLocalUser.LoginID`。
- `LocalUser` 同时包含 `Nick`、`Display` 和 `TargetId`。

### PR 1 选择

1. 优先使用 `LocalUser.TargetId` 作为稳定卖家标识候选；
2. `Nick` 只在 `TargetId` 缺失时作为显式兼容回退；
3. 回退身份写成 `nick:<规范化昵称>` 并标记 `HasStableSellerId=false`；
4. `ShopKey = SHA256("qianniu:" + sellerIdentity)` 的前 12 个十六进制字符，目录格式为 `qn_<digest>`；
5. `DisplayName` 只用于展示，不能参与目录、缓存或授权。

### 尚需真实环境验证

`TargetId` 是当前代码中最强的稳定 ID 候选，但仍需在不同千牛版本、店铺改名、子账号和同一店铺多窗口场景下验证其长期稳定性。验证完成前，缺少 `TargetId` 的店铺不能自动与另一个档案合并。

## 3. 全局路径和持久化清单

| 位置 | 当前行为 | 多店铺风险 | 后续阶段 |
|---|---|---|---|
| `BotLib.Extensions.PathEx.UserDataRoot` | 固定 `%LocalAppData%\QianniuAiBot` | 同一 Windows 用户共享根目录 | PR 1 保留为总根目录 |
| `PathEx.DataDir` | 固定 `%LocalAppData%\QianniuAiBot\data` | 所有业务文件共用 | PR 2/PR 5 迁移到 `shops/<ShopKey>` |
| `PersistentParams` | 静态单例，固定打开 `data\params.db` | API、模型、规则、同步状态和令牌共享 | PR 2 引入店铺参数存储/兼容层 |
| `KnowledgeCloudSyncService.Backup` | 硬编码 `data\backups` | 云端应用前备份串店 | PR 4 改为店铺备份目录 |
| `ClientDataCloudBackupService` | 枚举整个 `PathEx.DataDir` | PR #80 会把所有店铺打成一个备份 | PR 4/PR 5 改成单店铺备份和恢复 |
| `ClientDataCloudBackupService` 回滚 | `UserDataRoot\restore-backups` | 恢复边界不含 ShopKey | PR 5 按店铺记录迁移和回滚 |

PR 1 新增目标目录：

```text
%LocalAppData%\QianniuAiBot\global\shops.json
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\profile.json
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\config\
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\knowledge\
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\rules\
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\state\
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\cache\
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\logs\
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\backup\
```

## 4. 全局令牌和云端链路

以下服务均读取同一 `PersistentParams` 作用域 `ai-control-plane` 下的 `ControlPlaneClientToken`：

- `BotWebConsoleSyncService`；
- `KnowledgeCloudSyncService`；
- `ClientDataCloudBackupService`；
- 旧转人工规则一次性迁移链路。

当前后果：多个店铺会使用同一令牌、同一服务端 `client_id`、同一知识同步修订号和同一整机云备份。PR 2 必须先实现每店铺 DPAPI 令牌存储；PR 4 再把每个 `ShopRuntime` 的 Web、知识和备份请求绑定到对应令牌。

## 5. static / singleton / current 状态清单

### 已确认高风险状态

- `QN.QNSet`：全局集合，包含多个千牛实例；
- `QN.CurQN`：全局当前实例，异步任务不能依赖它判断消息所属店铺；
- `PersistentParams`：全局静态数据库和缓存；
- `BotWebConsoleSyncService`：全局静态计时器、消息队列、命令结果、暂停状态和同步游标；
- `KnowledgeCloudSyncService`：全局静态计时器、同步锁、修订号和 UI 状态；
- `ClientDataCloudBackupService`：全局静态 UI 和整机数据边界；
- `MyOpenAI.ChatClient`、系统提示和配置指纹：当前按进程共享；
- `BotFeatureStore` / `AiEndpointStore`：调用点未携带店铺上下文，当前等价于全局配置。

### 约束

后续不得在异步任务中重新读取 `QN.CurQN` 或 UI 当前选中项。队列项、缓存键、命令、日志和云同步游标必须显式包含不可变 `ShopContext` / `ShopKey`。

## 6. 消息到回复和 Web 同步调用链

当前主要链路：

```text
CDPClient WebSocket 事件
→ QN.Cdp_EvRecieveNewMessage / 后台恢复
→ QN.ProcessIncomingMessageAsync
→ IncomingMessageSafety / OrderPlacedAutoReplyService / VisionMessageDecision
→ BuyerMessageBurstCoordinator.Enqueue
→ QN.ProcessBuyerBurstAsync
→ ConversationContextStore / 店铺规则 / 知识库 / 订单 / 充值 / MyOpenAI
→ QN.SendTextWithRetryAsync
→ 千牛当前目标买家校验并发送
```

Web 同步链路：

```text
BotWebConsoleSyncService.PatchExisting
→ 反射包装每个 QN 的 BuyerMessageBurstCoordinator handler
→ HandleBurstAsync / CaptureConversation
→ 全局 PendingMessages
→ SyncOnceAsync 使用全局 ControlPlaneClientToken
→ /api/runtime/v1/bot-web/sync
```

风险点：消息对象只携带 seller/buyer 昵称，Web 队列没有 `ShopKey`；服务根据全局令牌同步所有 `QN` 的消息。PR 3 必须从消息入口创建 `ShopContext`，PR 4 再拆分为每店铺独立同步运行时。

## 7. PR 1 实现边界

本 PR 新增：

- `ShopContext`；
- `ShopProfile`；
- `ShopKeyGenerator`；
- `ShopIdentityResolver`；
- `ShopProfileStore`；
- `IShopScopedPathProvider` / `ShopScopedPathProvider`；
- `global/shops.json` 与 `shops/<ShopKey>/profile.json` 的原子写入；
- 重复 ShopKey、重复卖家身份和路径穿越防护；
- 仓库静态回归测试和 Windows 编译接线。

本 PR 明确不做：

- 不读取或保存每店铺令牌；
- 不切换 `PersistentParams`；
- 不迁移旧 `data`；
- 不改变消息处理、回复、Web 同步或云备份；
- 不自动把昵称回退档案升级/合并到稳定 ID 档案。

这样可保持当前单店铺用户行为不变，并为 PR 2 至 PR 5 提供统一底座。

## 8. 下一阶段直接入口

PR 2 应按以下顺序实施：

1. `ShopTokenStore`（Windows DPAPI CurrentUser）；
2. 店铺绑定 UI 和令牌指纹；
3. 店铺参数存储/兼容层；
4. 规则、知识、知识策略、模型/API 和转人工策略切换到 `ShopScopedPathProvider`；
5. 仍未完成迁移的旧接口只能在明确单店铺兼容模式中使用并记录弃用诊断。
