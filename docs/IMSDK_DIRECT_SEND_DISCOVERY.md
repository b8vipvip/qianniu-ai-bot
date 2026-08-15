# 新版千牛 IMSDK 直接发送能力确认

更新时间：2026-08-14

## 当前结论

在把任意 IMSDK 方法提升为生产发送主路径之前，必须先证明它是“普通买家聊天”的稳定直接发送接口，而不是智能提示、服务号、营销卡片或旧版已下线接口。

目前代码和现场日志能确认：

- `application.insertText2Inputbox` 可以把文本写入当前买家输入框，但它本身不负责发送。
- 现场 9.97.59N 日志中出现过 `intelligentservice.SendSmartTipMsg`，并随后观察到卖家侧真实消息回显，因此它具备文本发送能力；但它属于 `intelligentservice` / `SmartTip` 命名空间，暂不等同于普通聊天的规范发送接口。
- 历史 `wangwang.chat.sendMsg` 方向需要特别谨慎：现代千牛隐私改造文档已经把旧 `module_wangwang.chat` / `sendMsg` 列为下线/禁止继续使用的接口，不能因为名称看起来最合适就重新接入生产链路。
- 在没有确认一个稳定、普通聊天语义、当前版本继续支持的 IMSDK 直接发送方法之前，新版千牛的生产文本发送保持 UIA。

## 生产发送策略

新版千牛文本发送当前固定为：

1. 用 CDP/IMSDK 打开并严格确认目标买家会话。
2. 用 `application.insertText2Inputbox` 写入草稿。
3. 用 CDP/UIA 双重确认草稿仍属于本次 Bot 发送。
4. 刷新并限定到当前卖家对应的千牛接待窗口 HWND。
5. 对该卖家窗口内已经识别的“发送”按钮，只点击 UIA `BoundingRectangle` 推导出的左侧主操作区域；不使用固定屏幕坐标。
6. 如果坐标输入动作被 Windows 在执行阶段直接拒绝/抛异常（例如 Win32 Access Denied），并且目标草稿仍逐字存在，才单次回退 UIA `Invoke()`；如果坐标动作已经发出但只是送达确认延迟/不明确，则禁止再做第二次发送动作。
7. 用卖家消息真实回显优先确认送达；CDP 输入框清空只作为辅助确认。

**文本发送不再使用物理 Enter。** 这样即使当前最顶层/前台窗口不是千牛接待窗口，也不会把发送动作依赖到全局键盘焦点。

未知 IMSDK 候选方法不会自动调用。宁可本轮发送失败，也不允许在未确认参数和语义时把未知接口用于真实买家。

## 被动逆向工具 v2

新增：

- `tools/qn_discovery_lab/imsdk_send_discovery_v2.js`
- `tools/qn_discovery_lab/analyze_imsdk_send_trace.py`

`imsdk_send_discovery_v2.js` 只做对象/函数枚举，不调用候选接口。它会递归检查：

- `QN.wangwang`
- `QN.intelligentservice`
- `QN.component`
- `QN.app`
- `QN.application`
- `QN.gateway`
- `QN`
- `_vs.SDK`
- `_vs`
- `imsdk`

并按 `sendMsg`、`sendMessage`、`send`、`reply`、`message`、`singlemsg`、`wangwang` 等关键词排序，把结果通过现有 `imsdkApiScan` 日志通道输出，`scanKind=directSendV2`。

分析器可以读取 Bot 日志，把发送相关 method/path 汇总出来。它不会连接千牛，更不会执行候选接口。

## 何时允许切到 IMSDK 直发

只有同时满足以下条件才允许修改生产路由：

- 在当前新版千牛真实接待 WebView 中稳定存在；
- 被动跟踪能证明它与人工点击“发送”时的普通文字消息链路一致；
- 参数能明确绑定当前卖家、目标买家和消息正文，不依赖模糊全局状态；
- 受控小流量测试能获得真实卖家回显，并证明不会产生卡片/智能提示等不同消息类型；
- 重启、多店铺、多买家切换后仍能严格隔离；
- 失败/超时可 fail-closed，不产生重复发送；
- 不属于官方已下线/明确禁止继续使用的旧 API。

满足这些条件后，再把它提升为：`IMSDK 直发 -> 卖家窗口内主按钮左侧坐标 -> 坐标输入被系统拒绝时 UIA Invoke`。在此之前保持同样的 UIA/坐标安全边界，并继续以真实卖家回显确认送达。
