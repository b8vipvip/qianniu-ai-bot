# Qianniu Chat Automation Progress / 千牛聊天自动化进度

Last updated / 最后更新：2026-09-02 16:30 +08:00 之后

本文件只记录**当前生产状态和仍需验证的事项**。早期 discovery / message lifecycle 研究证据保留在独立文档中，不再把历史实验 TODO 当作当前生产 TODO。

## 1. Current production baseline / 当前生产基线

- Default branch / 默认分支：`master`
- Verified master commit / 已验证 master：`d5a72fd3fac83ced6780ac1213fbacecd784f64f`
- Current formal release / 当前正式版本：`bot-v1.1.1150`
- Windows x64 Release build #1150：成功
- Auto-update release workflow：成功
- Rescue updater asset workflow：成功

## 2. Completed / 已完成

### 2.1 Chat event ingestion / 聊天事件接入
- WebSocket / CDP business session discovery 已完成；
- 买家文本、订单卡片、图片消息已进入统一处理链；
- 非买家会话隔离已完成。

### 2.2 Coalescing / Generation
- 买家 burst coalescing 已实现；
- 前置规则等待有上限；
- generation cancellation / fail-open 已实现；
- 总 AI 预算仍为 50 秒；
- 1.1.1139 真实日志曾确认 **50 秒 deadline 实际延迟数分钟**，根因方向为 ThreadPool continuation starvation；
- PR #208 增加 ThreadPool starvation guard，保护 cancellation/timer/watchdog continuation。

### 2.3 Smart Reply / Knowledge
- Semantic Embedding 前台已 true async；
- embedding 子 deadline 约 2.2 秒；
- Knowledge V2 FactKey 已细化；
- 人工答案学习与异常诊断 JSON 解析已切换为结构化恢复器。

### 2.4 Order events / 订单事件
- OrderEventHub 跨进程原子持久化完成；
- order action durable ledger 完成；
- 固定回复按 segment 幂等；
- 人工完全相同 segment 可满足该 segment，但不会取消后续不同 segment。

### 2.5 Reliable send / 可靠发送
- Bot-owned exact draft 校验；
- CDP / 安全 HWND / UIA 分层发送；
- fallback 前重新验证；
- seller echo / delivery verification；
- 平台“服务态度提醒”检测；
- 不自动点击“继续发送”，阻断同文本盲重试。

### 2.6 OCR
- 本地 OCR worker/model 已从 Windows release 移除；
- 服务端 `/api/runtime/v1/ocr` 已实现；
- 客户端使用本店 `ShopControlPlaneConnectionStore` URL/token 自动解析 OCR endpoint；
- 不再要求人工配置伪造的“服务端控制面 AI 接口”。

## 3. Remaining runtime work / 当前剩余运行时工作

### P0 — Absolute generation freshness guard / generation 绝对年龄屏障

PR #209 已实现独立最后防线，当前等待 CI 与真实运行验证：
- 使用 dedicated background `Thread`，不依赖 ThreadPool timer continuation；
- 每 250ms 扫描 generation；
- generation 首次进入 `Generating` 后开始独立 wall-clock 计时；
- 超过 55 秒仍活跃则 `BuyerSessionAgent.Cancel(..., absolute_generation_age_exceeded)`；
- generation 从 `ActiveGenerations` 移除后，即使 provider 忽略 cancellation 迟到返回，现有 `lease.IsCurrent` 也会失败，因此不能继续 Ready/Sending；
- 人工客服回复不触发取消；terminal generation watch 自动清理。

这是一道最后防线，不替代正常 50 秒 cancellation。

### P1 — 1.1.1150+ runtime verification / 新版真实日志验证

下一份完整日志重点检查：
- 50 秒 deadline 是否稳定；
- 是否仍出现数分钟迟到 generation；
- 是否出现 `absolute_generation_age_exceeded`，若出现则迟到结果后续不得再进入 Ready/Sending；
- OCR 是否使用 `shop-control-plane`；
- CDP 页面通道是否持续单调增长；
- 重复消息平台弹窗是否被安全阻断。

### P2 — CDP lifecycle observation / 生命周期观察

旧日志没有足够证据证明页面通道持续泄漏。保持观察；只有新版长时日志出现持续增长时才继续改生命周期代码。

## 4. Validation rule / 验证规则

任何“已修复”都至少满足其一：

1. 生产代码 + CI/静态契约证明；或
2. 新版本真实运行日志证明。

对于运行时竞态、真实发送、平台 UI、CDP 生命周期等问题，CI 通过不等于线上已验证，必须继续用新版本日志复核。

## 5. Next execution / 下一步

1. PR #209 Windows CI、API CI、x64 Release 全绿；
2. 合并并自动发布下一版；
3. 新版真实测试并导出完整日志；
4. 按新日志继续挖掘，不凭旧文档重复修复已完成项。
