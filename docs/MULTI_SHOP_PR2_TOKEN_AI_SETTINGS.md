# 多店铺 PR 2：令牌与 AI 配置隔离

日期：2026-08-05  
基线：PR #81 合并提交 `0adddbfbbb0cfe45a3b6295dde9840b23e9f249a`

## 1. 本阶段完成范围

本阶段实现以下能力：

1. 每个 `ShopKey` 独立保存 Bot 客户端令牌；
2. 令牌使用 Windows DPAPI `CurrentUser` 加密；
3. 令牌文件与 DPAPI 附加熵同时绑定 `ShopKey`；
4. 设置窗口新增“店铺绑定”页面；
5. 旧全局令牌只能由用户点击“导入旧全局令牌”后显式保存到当前店铺；
6. AI 接口列表、调度策略、模型、API Key、系统提示词等 `ai` 作用域配置写入当前店铺的 `config/settings.json`；
7. 主买家回复链和图片视觉模型判断按消息所属卖家进入对应店铺 AI 配置作用域；
8. 店铺配置不存在某项值时，只读回退旧全局配置，保证旧单店铺用户升级后继续工作；
9. 店铺作用域写入不会在失败时回退全局数据库，避免一个店铺的保存操作污染其他店铺。

## 2. 文件布局

```text
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\config\settings.json
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\config\control-plane-token.json
```

`settings.json` 使用 schema：

```text
qianniu-ai-bot.shop-settings / version 1
```

令牌文件使用 schema：

```text
qianniu-ai-bot.shop-token / version 1
```

令牌文件不保存令牌明文，只保存：

- DPAPI 加密密文；
- 令牌 SHA-256 前 12 位指纹；
- ShopKey；
- 算法和更新时间。

## 3. 设置作用域

`ScopedParamRouter` 位于 `BotLib`，只提供通用的读写代理接口，不依赖 Windows Bot 的店铺类型。

Windows Bot 启动后由 `ShopScopedParamBridge` 注册代理。本 PR 白名单只包含：

```text
subKey = ai
```

没有把 `feature`、`ai-control-plane` 或任意未知作用域自动路由到店铺文件，避免在没有完整运行时接线时提前宣称隔离完成。

设置窗口在创建和保存 `CtlRobotOptions` 时显式进入 `ShopSettingsScope`。该作用域使用 `AsyncLocal<ShopContext>`，只随当前异步调用链传播，不是进程级“当前店铺”变量。

## 4. 买家回复运行时

主回复运行时在 `BuyerMessageBurstCoordinator` 唯一的 `_handler` 调用点执行：

```text
BuyerMessageBurst.SellerNick
→ ShopContextLocator.ResolveRuntimeBySellerNick
→ ShopSettingsScope.Enter(shop)
→ 原有 Bot Web 观察器及 ProcessBuyerBurstAsync
```

这样不依赖异步执行时的 `QN.CurQN`，并且不会再增加第二个反射处理器包装器。

现有 `MyOpenAI` 仍保留进程级静态提示词和配置指纹。为避免两个店铺并发生成答案时静态状态互相覆盖，本阶段在 burst handler 外使用临时 `SemaphoreSlim` 串行门。该门只是一项兼容措施；后续应把 `MyOpenAI` 配置改为显式的不可变调用上下文，再移除串行限制。

图片消息在进入 burst 前会先判断是否存在视觉模型。`VisionMessageDecision` 现已根据消息 `toid.nick` 解析店铺，并在店铺作用域内重新读取视觉接口配置。

## 5. 兼容与失败行为

### 旧全局 AI 配置

当本店 `settings.json` 尚无对应 key 时，读取旧 `params.db` 中的全局值。用户在店铺设置页面保存后，新值只写本店文件。

### 旧全局 Bot 令牌

不会自动复制。用户必须在“店铺绑定”页面点击导入，并点击窗口底部保存。这样避免在多店铺环境中把同一个历史令牌静默绑定给所有店铺。

### 不稳定昵称回退

若千牛没有提供 `TargetId`，页面会显示不稳定身份警告。用户必须主动勾选确认，才可保存本店令牌。

### 文件损坏或写入失败

- schema 或 ShopKey 不匹配会直接报错；
- 店铺配置写入失败不会写回全局数据库；
- 保存异常时设置窗口重新显示，不会关闭并假装成功；
- 清除令牌会同时删除当前文件、`.bak` 和临时副本；任一删除失败都会显示错误。

## 6. 本阶段明确未完成

以下仍使用旧全局数据或令牌，留给后续 PR：

- 知识库内容；
- 知识策略和可靠度统计；
- 店铺资料、业务策略 JSON、场景规则；
- 转人工策略；
- 消息策略和通知配置；
- 知识库智能导入任务本身的知识写入边界；
- Bot Web 同步令牌和同步游标；
- 知识云同步令牌、revision 和 hash；
- PR #80 整机云备份和恢复边界。

统一 API 地址仍保存在全局配置中，因为同一程序连接同一控制面服务是当前部署模型；服务端身份由每店铺令牌区分。

## 7. 下一阶段

建议 PR 3 优先完成：

1. 为消息、会话缓存、订单/充值任务和日志显式携带 `ShopContext`；
2. 将知识库、知识策略、业务规则和转人工策略迁移到店铺路径；
3. 对旧全局数据实现可重入迁移、迁移日志和回滚；
4. 在两店铺并发测试中验证检索、生成、发送及日志均不串店。

随后 PR 4 再拆分 Web 同步、知识云同步和客户端云备份的每店铺运行时与令牌。
