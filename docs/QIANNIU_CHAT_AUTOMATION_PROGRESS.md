# Qianniu Chat Automation Research Progress

Last updated: 2026-07-15

This document is the handoff source of truth for future AI/agent development. Read it before running new discovery experiments.

## 1. Goal and scope

The project goal is to obtain a reliable Qianniu chat automation path that can:

1. Read the currently selected conversation and its visible/loaded messages.
2. Identify the current contact/session.
3. Write a reply into the chat input.
4. Send exactly once and verify the result.
5. Later integrate the proven path into the existing bot without breaking the existing Simplified Chinese repair.

Discovery work must stay isolated under `tools/qn_discovery_lab` until the probe is verified end to end.

## 2. Confirmed environment

- Qianniu / AliWorkbench version: `9.97.56N`
- Install root: `C:\Program Files\AliWorkbench\9.97.56N`
- Embedded browser: CEF / Chromium 130
- Renderer user agent includes: `Chrome/130.0.0.0 Qianniu/9.97.56N`
- Locale: `--lang=zh-CN`
- Chat renderer command-line marker: `--render_id=bench_im-...`
- Never use a fixed PID. The renderer PID changes frequently.

`Resources/config/render_id.json` maps the following pages to `bench_im`:

- `https://alires-webui/web_msg-center/index.html`
- `https://alires-webui/web_chat-packer/recent.html...`
- `https://alires-webui/Message/message-notify.html`

This confirms that the primary chat surface is a dedicated Web/CEF chat application, not merely a Windmill mini-app.

## 3. Static discovery findings

### 3.1 Windmill bridge chain

The install directory contains:

- `WINDMILLResource/windmill/h5_bridge.js`
- `WINDMILLResource/windmill/af-appx.min.js`
- `WINDMILLResource/windmill/ext-api.js`
- development variants under `WINDMILLResource/windmill/dev`

`h5_bridge.js` defines:

```javascript
const JSAPI = {
  call(func, param, callback) {
    // ...
    windmill.postMessage(...)
  }
};
window.AlipayJSBridge = JSAPI;
```

A candidate text-send call was found in `af-appx.min.js`:

```javascript
ddExec({
  serviceName: "internal.chat",
  actionName: "selectAndSendText",
  args: {
    content: text,
    atList: [],
    bizType: "E-App-" + appId
  }
})
```

### 3.2 Important correction

The call above occurs in a `shareTextMsg` branch. It is likely a mini-app "share text into chat" feature, not the primary chat input's direct send API.

Do not present `internal.chat.selectAndSendText` as the final chat send API unless it is dynamically observed inside the `bench_im` renderer.

## 4. Dynamic reverse-engineering results

### 4.1 Frida attach

- Frida `17.15.5` successfully attaches to the current `bench_im` renderer.
- A Python launcher was built to find the renderer by command line instead of a fixed PID.

### 4.2 Native JavaScript execution hooks that did not fire

The following symbols were found, hooked, and then tested while changing contacts, scrolling, and manually sending messages:

- `WebControl::ExecuteJavaScript`
- `WebControl::ExecuteJavaScriptFunction`
- `webapp::mojom::WebViewProxy::ExecuteJavaScript`

None fired during the tested manual chat operations.

Correct interpretation: the observed manual-send path did not pass through these functions. Their mere presence does not prove they are safe or callable as an injection route.

Do not call these C++ functions directly without a verified object instance, ABI/signature, owning thread, and valid CEF context. Blind calls can crash Qianniu.

### 4.3 CEF/V8 symbol search

Symbol searches returned:

- `*CefV8Context*`: 0
- `*CefFrame*`: 0
- `*ExecuteFunction*`: 0

Release symbols are stripped. Repeating broad `DebugSymbol.findFunctionsMatching` searches is low value unless a new module or symbol source is found.

### 4.4 `AlipayJSBridge` memory hit correction

A memory scan found the ASCII string `AlipayJSBridge` around `0x1d44...`.

This was a raw string occurrence, not a verified V8 object pointer. Reading nearby pointers produced no usable object structure. Do not dereference that address as `window.AlipayJSBridge`, and do not treat it as a function/object handle.

### 4.5 Runtime marker scan

The `bench_im` process contained `h5_bridge.js` and `WINDMILLResource` strings in mapped module regions, but scans did not find:

- `selectAndSendText`
- `internal.chat`
- `ddExec`

Some V8 ranges changed protection/unmapped during scanning, so this is not a mathematical proof of absence. However, there is still no positive evidence that the Windmill candidate is the primary chat send path.

### 4.6 CEF cache/resource scans

Direct `findstr` and `Select-String` scans of CEF cache files were low value because Chromium cache, Code Cache, Service Worker CacheStorage, LevelDB, and GPU cache data are binary/structured.

The install resource scan confirmed Windmill JS files and standard CEF `.pak/.bin/.dat` resources. Do not repeat broad text scans over every cache binary unless a parser for the specific cache format is introduced.

## 5. Native modules loaded in `bench_im`

The chat renderer loads several highly relevant native modules:

- `MessageSDKBiz.dll`
- `MessageSDKModel.dll`
- `message_support.dll`
- `aim.dll`
- `AppBiz.dll`
- `AssistIPC.dll`
- `AssistIPC_shared.dll`
- `syncsdkbiz.dll`
- `ipc.dll`
- `ipc_mojom.dll`
- `WebApp.dll`
- `WebView.dll`
- `windmill.dll`
- `prgdb.dll`
- `FTSEngine.dll`
- `libaccs.dll`
- `wtnet.dll`

This is positive evidence that an internal structured message pipeline exists. It does not imply a public or stable third-party API.

Long-term reverse-engineering should focus on `MessageSDKBiz.dll`, `MessageSDKModel.dll`, `aim.dll`, and IPC boundaries rather than continuing to assume Windmill is the main chat path.

## 6. Major verified result: Windows UI Automation works

This is the first production-relevant success.

Using `uiautomation==2.0.29`, the Qianniu accessibility tree exposes the chat UI in a structured way.

### 6.1 Confirmed top-level controls

- Window name: `千牛接待台`
- Window class: `MutilChatView`
- Chat document name: `千牛消息聊天`
- CEF document class: `Chrome_RenderWidgetHostHWND`

### 6.2 Confirmed message tree

The chat document exposes:

- `AutomationId = app`
- `AutomationId = msgOutBody`
- `AutomationId = J_msgContainer`
- `AutomationId = J_msg_list`

Loaded message nodes expose structured names containing:

- sender/account names
- timestamps
- text message bodies
- product titles/prices/URLs
- UI message-node IDs such as `4210903391593.PNM`

Therefore current/loaded chat information can be read without OCR or screenshots.

### 6.3 Confirmed input control

Stable input `AutomationId`:

```text
UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.chatInputArea.plainTextEdit
```

The control supports `ValuePattern`.

Verified test:

1. Read the original input value.
2. Set `UIA_PROBE_20260715_153738`.
3. Read it back successfully.
4. Restore the original empty value.
5. Do not send.

Observed output:

```text
[FOUND] input: (502,779,1006,935)[504x156]
[FOUND] send : (920,938,986,962)[66x24] name= 发送
[INFO] old input: ''
[INFO] readback : 'UIA_PROBE_20260715_153738'
[PASS] input write/read
[INFO] restored : ''
[SAFE] send button was located but NOT clicked
```

### 6.4 Confirmed send button

Stable send-button `AutomationId`:

```text
UIWindow.mutilcentralwidget.stackedWidget.SingleChatView.centralwidget.stackedWidget.SubChatView.ChatDisplayWidget.ChatContentView.splitter.sendMsgWidget.enterAreaKeyWidget.sendMsg
```

Name: `发送`

The button was located but has not yet been invoked in the verified record.

## 7. Chosen architecture

### Near-term, practical path

Use Windows UI Automation for:

- reading the currently selected conversation
- extracting loaded messages
- locating the input field
- writing reply text
- invoking the structured `发送` button
- verifying the message appears in `J_msg_list`

This is not fragile pixel-coordinate RPA. It uses stable accessibility controls and AutomationIds.

### Medium-term path

Continue native discovery in parallel for a background/non-visible structured stream:

- hook `MessageSDKBiz.dll` / `MessageSDKModel.dll`
- identify incoming-message callbacks and outgoing-message serialization
- capture sender, receiver, conversation ID, message ID, type, text, timestamp, and status

### Fallback/secondary paths

- CEF DOM/accessibility extraction
- local database/index analysis (`prgdb.dll`, `FTSEngine.dll`)
- network-layer plaintext hooks after serialization/decryption

## 8. Safety rules for sending

Every independent send probe must:

1. Default to dry-run.
2. Require both `--send` and the exact confirmation token `SEND_TO_CURRENT_CHAT`.
3. Display that it sends to the currently selected chat.
4. Use a countdown before invoking send.
5. Invoke the send button only once.
6. Never automatically retry after an ambiguous result.
7. Restore the original input on any pre-send validation failure.
8. Verify through the UIA message tree and/or input clearing.
9. Avoid modifying the main bot until the probe is verified.

## 9. Development environment notes

### Python

A dedicated environment is used:

```text
tools/qn_discovery_lab/.venv-uia
```

`pywinauto` was abandoned because `win32ui` failed to load even in a clean venv. The working package is:

```text
uiautomation==2.0.29
```

### PowerShell / PSReadLine

Long pasted arrays and here-strings repeatedly crash PSReadLine with `System.ArgumentOutOfRangeException` in `PSConsoleReadLine.ReallyRender`.

Do not give the user long multi-line paste blocks. Prefer:

- committing scripts to this repository
- `git checkout <remote-branch> -- <path>`
- short `Set-Content` / `-replace` commands
- single valid Base64 payload only when generated and verified

## 10. Experiments not to repeat without new evidence

Do not repeat these as the next default step:

- treating the `AlipayJSBridge` string address as a V8 object
- scanning a tiny neighborhood around that string for object methods
- broad searches for stripped `CefV8Context` symbols
- hooking the three tested ExecuteJavaScript symbols and assuming silence means they can be actively called
- broad `findstr` scans over active Chromium cache binaries
- modifying `h5_bridge.js` before proving `bench_im` loads and calls it
- claiming `internal.chat.selectAndSendText` is the final main-chat API
- using a fixed renderer PID
- using pixel coordinates or OCR when UIA controls are available

## 11. Immediate next tasks

1. Run the safe real-send probe in `tools/qn_discovery_lab/qn_uia_send_probe.py` against a dedicated test conversation.
2. Require a unique timestamped message and verify it appears in the UIA tree.
3. Build `qn_uia_extract_messages.py` that outputs normalized JSON:
   - node AutomationId
   - sender
   - timestamp
   - message type
   - body/text
   - visible/off-screen state
4. Add deduplication keyed by message-node AutomationId plus content/time fallback.
5. Build a watcher that reports newly added messages without sending.
6. Add current-contact/session detection before any bot integration.
7. Only after those pass, connect the UIA adapter to the existing response-generation code.
8. Continue MessageSDK/AIM reverse engineering as a parallel, longer-term track.

## 12. Current milestone status

| Capability | Status |
|---|---|
| Locate current chat window | Verified |
| Read loaded chat text | Verified |
| Read sender/time/product metadata | Verified |
| Locate input by AutomationId | Verified |
| Write and read back input text | Verified |
| Restore original input | Verified |
| Locate send button by AutomationId | Verified |
| Invoke send exactly once | Pending next probe |
| Verify sent message in UIA tree | Pending next probe |
| Background message stream | Not yet available |
| Stable public/internal API | Not found |
| Main bot integration | Intentionally deferred |
