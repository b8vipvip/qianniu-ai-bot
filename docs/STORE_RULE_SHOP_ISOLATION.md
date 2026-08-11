# 店铺规则中心：ShopKey 隔离、云备份与同步

日期：2026-08-11

## 目标

“设置 → 知识库 → 问答管理 → 店铺规则中心”中的原始店铺资料、核心规则和场景规则卡必须属于当前 ShopKey，不能继续使用进程级全局文件或全局静态缓存。

## 本地目录

每个店铺独立保存：

```text
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\rules\store-prompt-profile.json
```

`StorePromptProfileService` 只允许在 `ShopSettingsScope` 中读取或写入，并且缓存按完整店铺文件路径分组。因此同一 Bot.exe 同时打开多个千牛店铺时，店铺 A 的规则不会被店铺 B 读取或覆盖。

店铺规则中心窗口会捕获并绑定打开它的 `ShopContext`，加载、AI 生成、手工保存均重新进入该 ShopKey 的作用域。

## 旧全局数据迁移

历史版本文件：

```text
%LocalAppData%\QianniuAiBot\data\store-prompt-profile.json
```

迁移规则：

- 只有一个已注册店铺时，可以自动继承旧全局文件；迁移前在本店 `backup` 目录保存副本。
- 多店铺时禁止猜测旧文件归属。
- 多店铺用户通过现有“将旧全局数据迁移到本店”流程确认归属后，旧文件会先进入当前店铺兼容目录；店铺规则服务只消费当前 ShopKey 的迁移结果，再移动到本店 `rules` 目录。

## 云备份 / 换机恢复

`ClientDataCloudBackupService` 枚举当前 `shops\<ShopKey>` 根目录中的业务文件，并只排除日志、缓存、临时文件、密钥原文件等。因此：

```text
rules\store-prompt-profile.json
```

会自动进入当前店铺的 QABK2 云备份，并在恢复时受 manifest ShopKey 校验保护；跨店恢复继续直接拒绝。

## 云同步

新增独立店铺规则同步链路：

```text
POST /api/runtime/v1/bot-web/store-rule-sync
```

行为：

- 复用本店“知识库云同步”开关；
- 每个 ShopKey 使用自己的 Bot 客户端令牌；
- 请求携带 Bearer Token 和 `X-Shop-Key`；
- Windows 端独立维护 `StoreRuleCloudRevision` / `StoreRuleCloudLastHash`；
- 本地规则保存后立即排队同步，同时有周期同步兜底；
- 云端规则覆盖本地前，先在本店 `backup` 目录创建 `store-rule-cloud-before-apply-*.json`；
- 服务端状态按 `client_id` 存储，ShopKey 由现有 runtime token-binding 中间件验证，不接受 payload 自行声明店铺身份；
- 强制把一个客户端令牌重新绑定到另一店铺时，服务端同时清除该令牌旧店铺的 `bot_store_rule_state`，避免跨店残留。

## 托盘退出异常

托盘“退出”不再尝试把已经 `Close()` 的 WPF `WndNotifyIcon` 再次设置为 Visible。退出处理改为幂等的 `Application.Shutdown()`，并停止触碰 `Visibility` / `Show` / `EnsureHandle`，对应修复 Windows Server 上的 `InvalidOperationException`。
