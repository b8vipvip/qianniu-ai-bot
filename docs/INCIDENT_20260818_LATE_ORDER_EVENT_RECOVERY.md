# 2026-08-18 延迟订单事件漏识别修复

## 生产现象

买家“旅行走丢的大懒猫”在约 13:34 已进入会话，随后于 2026-08-18 13:35:58 下单、13:36:01 付款；千牛没有向 Bot 提供可被既有订单链路识别的即时订单/付款消息，因此下单与付款自动回复没有触发。

## 根因

旧逻辑只在收到买家后台会话通知后进行较短窗口的消息/订单卡片补抓。订单实际在最后一次相关会话通知约 104 秒后才创建，已经超出补抓窗口。右侧订单面板后来即使刷新，旧版 Bot 也不会继续读取并重新发布到 `OrderEventHub`。

## 修复

- 新增后台订单面板延迟补扫桥接。
- 收到买家后台通知后，在 500ms、1.5s、3.2s、6s、10s、16s、24s、36s、60s、90s、120s、125s、135s、150s、180s 分阶段补扫。
- 36 秒以后仅在目标买家仍是当前会话时被动读取，避免抢占客服当前会话焦点。
- 从右侧订单面板确认的新订单重新构造 `OrderSnapshot` 并进入 `OrderEventHub.Publish()`，继续复用现有 Created/Paid 去重、规则匹配和自动回复链路。
- 新增针对本次 13:35:58 下单、13:36:01 付款场景的静态回归测试。

## 相关修复提交

- `eca5a03dd5ff5f70ee80636975b2fd73e7317597` — recover late active-buyer order panel events
- `c5e49a7fa4b4abe42d2a9094fc56facb3d4a53ca` — cover late passive order panel recovery
- `8218a5603e689ab83df4c2a8d1361321e2204797` — recover late order events from active buyer panel

## 发布要求

正式 Windows x64 包必须从包含以上提交的 `master` 构建，并通过仓库既有的静态测试、Release 构建、完整运行包校验和 GitHub Release 资产校验后发布。
