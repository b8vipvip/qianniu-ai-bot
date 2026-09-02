# qianniu-ai-bot 项目交接上下文 / Project Handoff Context

> 更新时间 / Updated: 2026-09-02 14:35 +08:00  
> 默认分支 / Default branch: `master`  
> 已验证正式基线 / Verified release baseline: `1.1.1139`  
> `master` baseline SHA: `63b25d59b307a210279119665d3c7c9c85755ecc`  
> 当前修复分支 / Active fix branch: `fix/runtime-starvation-ocr-config-20260902`  
> 当前 PR / Active PR: `#208`

## 1. 当前目标 / Current Goal

继续以真实千牛运行日志为准，保证生产链路稳定：买家消息不能因 Coalescing、线程池饥饿或同步兼容路径永久卡住；AI/本地答案总预算必须真实受 wall-clock deadline 约束；发送必须以正确会话、Bot 自有草稿和真实卖家回显/送达证据为成功标准；OCR 在服务端控制面执行并按店铺令牌隔离；Knowledge V2 保持结构化 FactKey、冲突治理和反馈闭环。

## 2. 2026-09-02 已完成并合并 / Completed and Merged

### PR #203 — 服务端 OCR 迁移

- 新增 `/api/runtime/v1/ocr`，复用 Bearer client token。
- 8MB 图片上限、SHA-256 完整性、并发限制、8s 服务端请求 deadline。
- RapidOCR + ONNXRuntime CPU；服务端不记录原图/完整 OCR 文本。
- Windows 改为上传 + SHA-256 缓存；失败 soft fallback 到视觉模型。
- 正式 Windows 包不再包含 `LocalOcrWorker.exe` / 本地 OCR 模型。

### PR #204 — Embedding / Order / Send 稳定性

- Semantic Embedding 前台全链 async/await，2200ms 子预算贯穿 50s 总预算 cancellation。
- Smart Reply 前台使用 `BuildPlanAsync`；embedding 超时 fail-open。
- `OrderEventHub` 跨进程安全 read-modify-write；订单动作 durable ledger 跨实例幂等。
- 固定订单预设按业务动作执行；相同人工 seller echo 只满足对应分段。
- 千牛“服务态度提醒”成为 terminal cancellation；HWND 安全发送拒绝跨 root/modal 点击。

### PR #205 — 非买家会话硬隔离

- 小二、服务商、1688、群聊、平台系统会话不进入普通买家回复链。
- Guard 前置于订单文本、首问、商品链接预设和 AI。
- URL 本身不是黑名单，真实买家商品链接仍正常处理。

### PR #206 — 语义续问 + Coalescing starvation

- substantive anchor + dependent fragment 语义问题帧。
- `？/可以吗/能用吗/支持吗/多少钱/多久/怎么用` 等可在 180s TTL 内继承主问题。
- `ModelQuestion` 真正进入 Streaming/legacy AI。
- 删除外层 `_preMergeRuleGates`；固定规则只保留单一 1.8s gate，拿不到 fail-open。
- merge 异常显式 Failed，不允许永久停在 Coalescing。

### PR #207 — 人工答案对比学习 JSON 恢复

- 不再使用 first `{` / last `}` 粗暴截取。
- 支持纯 JSON、Markdown fence、wrapper object/array、JSON string、说明文字中的平衡对象。
- 扫描感知 string/escape；只接受学习 schema。
- 最终无法恢复时 `skipped`，不修改知识、不把模型格式漂移误报为 Bot 故障。

## 3. 1.1.1139 真实日志新发现 / New Findings

日志构建身份：`releaseVersion=1.1.1139`、`commit=63b25d59...`。

### P0 — 50 秒 deadline 被运行时饥饿拖延到数分钟

现场不是“模型单纯慢 12 分钟”，而是 deadline continuation 本身没有按时获得调度。多个 generation 的 `文本AI流总预算超时 budgetSeconds=50` 实际延迟数分钟；generation 1 最终约 751 秒后得到本地商品链接答案，仍进入 `Generating -> Ready -> Sending -> Completed`，形成严重迟到发送。

PR #208 已加入第一层运行时防线：启动时提高 CLR ThreadPool worker/I/O 最小容量，确保 `CancelAfter`、`Task.Delay`、发送确认 watchdog 等关键 continuation 不被同步 UIA/CDP 兼容任务长期饿死。

真实回归必须验证：50 秒 timeout 实际触发误差不再达到分钟级；若仍存在，继续隔离剩余同步 `Task.Run`/UIA/CDP 路径，并增加 generation 绝对年龄发送屏障。

### P1 — OCR 已连接控制面但客户端仍提示“未配置OCR接口”

1.1.1139 已成功连接 `https://aboter.mv3.cn` SSE 控制面，但图片链仍提示 `服务端OCR跳过/失败: 未配置可用的服务端控制面OCR接口`。根因是 OCR 客户端错误要求 `AiEndpointStore` 额外存在 `Type == 服务端控制面` 的 AI endpoint，而正式凭据实际保存在 `ShopControlPlaneConnectionStore`。

PR #208 已修复：OCR 按 seller/shop 直接复用现有控制面 URL + 本店 token；旧 AI endpoint 只保留兼容回退。

### P1 — 诊断模块仍残留旧式 JSON 截取

PR #207 修复了人工学习，但继续审计发现 `SendFailureAnomalyService`、`SlowResponseAnomalyService` 仍有 first/last brace JSON 恢复逻辑。PR #208 正在统一到 quote/escape-aware 的结构化 JSON 恢复器，避免诊断 AI 输出 wrapper/fence 时再次出现同类解析漂移。

### 观察项 — 页面通道数量

1.1.1139 心跳持续显示 `业务CDP=1｜页面通道=27`。当前日志中业务权威 CDP 始终为 1，重复页面事件有短窗去重和权威会话转交，未观察到页面通道继续单调增长，因此当前证据不足以判定仍有 CDP 生命周期泄漏。只有后续 `页面通道` 随运行时间持续增长且不回落时才重新升为缺陷。

### 观察项 — Knowledge V2 冲突

启动时 `records=829, conflicts=93`。当前 `FactKey` 已包含 Subject + Predicate + Intent + ProductIds + Entities + Conditions + RequiredContext + Exclusions，不再是早期过粗键。93 个冲突目前视为真实知识治理数据，不在没有逐条证据时通过放宽 FactKey 隐藏。

## 4. 发送可靠性当前结论 / Send Reliability Status

旧事故文档中 2026-08-13 的主要发送改造已由当前实现覆盖：exact Bot-owned draft 校验；CDP DOM / HWND safe point / UIA 分层发送；每次 fallback 前后重新验证草稿；平台 modal/root 安全拒绝；卖家 echo / delivery verification 才算真实成功；Windows integrity 诊断；发送失败异常报告。

1.1.1139 本次日志中的实际发送均出现 `deliveryVerified=True`，未发现“仅清空输入框即误判成功”的新证据。

## 5. 当前未完成 / Remaining Work

PR #208 合并前：三条正式 CI 必须全绿；完成诊断 JSON 恢复统一；更新三份进度/事故文档。

合并后真实回归重点：

1. 连续 8–10 条买家消息下，50s deadline 是否在约 50–55s 内真实触发。
2. 旧 generation 是否还会在数分钟后进入 Ready/Sending。
3. 图片 OCR 是否出现 `endpointSource=shop-control-plane`，且不再提示未配置 OCR。
4. OCR 服务端不可用时是否正常 soft fallback 到视觉模型。
5. `页面通道` 是否长期稳定而非单调增长。
6. 订单固定分段是否继续保持 durable idempotency + exact seller echo satisfied。

## 6. 发布规则 / Release Rule

始终执行：`feature/fix branch -> regression/static tests -> PR -> Windows CI + API CI + Windows x64 release build -> merge master -> master release build -> auto-update release`。

禁止为了测试绿而削弱生产安全门控；禁止 PR 分支构建成功就宣称正式版已发布；禁止未确认 seller echo/delivery 就宣称发送成功；禁止重新引入本地 OCR worker/model；禁止用同步 `.Result/.Wait/GetAwaiter().GetResult()` 恢复前台 Embedding 网络调用。
