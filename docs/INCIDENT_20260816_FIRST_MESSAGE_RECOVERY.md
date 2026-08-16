# 2026-08-16 千牛首条买家消息补偿提前结束

## 现象

后台通知已识别新买家消息并自动切换到目标会话，但首条消息没有进入权威回复队列。现场日志只出现 `attempt=1/8`，随后没有 AI 生成或发送动作。

## 根因

`QN.MessageRecovery.cs` 在会话切换后首次读取远端历史为空时返回 `true`。调度器把该返回值解释为“补偿完成”，因此提前结束 2..8 次重试。千牛会话 ID 已切换不代表消息历史已经完成 hydration。

## 修复

- 会话切换确认后等待 450ms 再读取远端历史。
- 本轮历史为空/没有候选消息时返回 `false`，继续最多 8 次补偿重试。
- 保留详细 `receiveNewMsg` 到达后的版本取消和 observed 去重，避免历史补偿与实时事件双重回复。
- 日志明确记录每次重试、hydration 等待和重试耗尽。

## 回归验证

`tests/test_background_message_recovery_auto_switch_static.py` 覆盖：

1. 导航/恢复锁等待有界且可重试；
2. 自动切换后存在 hydration 等待；
3. 空历史不能结束补偿，必须继续重试；
4. 实时详细事件到达仍能取消历史重放；
5. 处理 AI/规则答案前释放导航相关锁。

## 发布要求

必须通过 PR CI 与 `Windows x64 release build`；合并到 `master` 后由 `Publish Bot auto-update release` 使用已验证构建自动发布 stable 版本，禁止用本地重打包文件替换正式发布资产。
