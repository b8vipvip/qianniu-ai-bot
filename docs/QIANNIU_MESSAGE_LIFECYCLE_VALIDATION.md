# 千牛消息生命周期验证

最后更新：2026-07-16

## 范围

本轮在 `tools/qn_discovery_lab` 中实现隐私安全的 UIA 消息语义元数据、快照比较和生命周期证据 CLI。代码保持 `qn_uia_messages.v2`，不接入生产 Bot，不调用 AI，不发送消息。

## 已由真实本地证据确认

- 买家撤回是同一 `message_key`、同一节点身份上的原地内容变化；旧方向为 `incoming`，撤回后规范化为 `direction=unknown`、`type=system`。
- 卖家撤回同样是原地变化；撤回后的旧几何算法可能误判为 `incoming/text`，所以确认必须使用 prior direction、相同键、相同节点身份、内容摘要变化和 `withdrawal_notice`。
- 两条相同正文可以拥有不同节点身份。正文内容和匹配数量不能用于撤回关联。
- 历史 offscreen 撤回提示不能按相邻、时间、几何或正文关联到其他消息。

旧的买家/卖家快照属于 legacy 证据：它们缺少本轮新增的 `semantic_flags`、`content_hash` 和 `node_identity_hash`，因此不能单独作为新版 capture 的端到端通过证据。旧证据仍支持“键集合无新增/删除、目标节点原地变化、卖家撤回后方向可误判”的结论。

## 本轮真实只读验证

证据保存在 Git 仓库外。三次空闲快照均观察到 24 个消息节点：

- `message_key` 集合和顺序稳定。
- `node_identity_hash` 和 `observation_key` 稳定。
- 未观察到 visible-only 或其他业务更新。
- 当前 UIA provider 未暴露 `J_msg_list` 的 ScrollPattern，因此滚动阶段安全跳过。
- 无法百分之百确认当前已选会话行身份，因此重新聚焦/最小化恢复阶段安全跳过。

总体结果为 `PARTIAL`，不能据此声称滚动、重新观察或历史首次加载已经端到端验证。

## 规则

- `message_key` 优先来自原始 PNM 节点 ID 的内存内 SHA-256 摘要。
- `observation_key` 包含内容摘要、规范化方向、原类型、时间、语义标志和生命周期；明确排除 visible、offscreen、bounds 和屏幕坐标。
- 同键同 observation 为 `unchanged`；同键不同 observation 为 `updated`。
- 消失为 `not_observed`，再次出现为 `reobserved`；两者都不能推断撤回或新消息。
- 单独出现的撤回提示只能是 candidate。只有同键、同节点、内容变化、旧消息为普通文本且新语义为撤回时，才能确认买家或卖家撤回。
- 所有撤回、历史初始化和重新观察事件均 `actionable=false`。
- `message_key` 保持既有 `qn_uia_messages.v2` 算法和 `key_source`；新增的 `node_identity_hash` 只作为附加关联证据，不重写历史键。
- 未经真实验证的风险、发送失败、订单和一般系统提示只作为低置信度 semantic candidate；它们不会单独覆盖普通文本的方向、类型或 `user_text` 生命周期。

推荐的证据比较命令使用显式路径参数：

```text
qn_uia_lifecycle_probe.py compare --before before.json --after after.json --scenario history_reload
```

仍兼容 `compare before.json after.json --scenario history_reload`。

## 尚未验证

- 风控屏蔽和发送失败的真实 UIA 结构。
- 一般系统提示和订单提示的完整规则覆盖。
- ScrollPattern 下的 visible/offscreen、not_observed/reobserved 和历史首次加载行为。
- 当前会话重新聚焦及最小化/恢复行为。
- 主 Bot shadow mode 和生产接入。
