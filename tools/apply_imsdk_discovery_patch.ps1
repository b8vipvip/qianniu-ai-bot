$ErrorActionPreference = "Stop"

function Read-Utf8File([string]$Path) {
    return [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8)
}

function Write-Utf8File([string]$Path, [string]$Content) {
    $utf8Bom = New-Object System.Text.UTF8Encoding($true)
    [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, $utf8Bom)
}

function Replace-Required([string]$Content, [string]$OldValue, [string]$NewValue, [string]$Description) {
    if ($Content.Contains($NewValue)) {
        Write-Host "$Description already applied."
        return $Content
    }
    if (-not $Content.Contains($OldValue)) {
        throw "Patch anchor not found: $Description"
    }
    Write-Host "Apply: $Description"
    return $Content.Replace($OldValue, $NewValue)
}

$injectPath = "src/Bin/inject.js"
$inject = Read-Utf8File $injectPath

$inject = Replace-Required $inject `
    'window.__qnbotInjectVersion = "20260713-zh-cn-v8";' `
    'window.__qnbotInjectVersion = "20260714-zh-cn-v9";' `
    "bump inject version"

$discoveryBlock = @'
  var IMSDK_DISCOVERY_VERSION = "20260714-imsdk-discovery-v1";
  var imsdkScanSent = false;
  var imsdkTraceSequence = 0;
  var imsdkTraceRing = [];
  window.__qnbotImsdkDiscoveryVersion = IMSDK_DISCOVERY_VERSION;

  function isSensitiveKey(name) {
    return /token|cookie|authorization|password|passwd|secret|session|ticket|credential/i.test(name || "");
  }

  function safeValue(value, depth, seen) {
    try {
      if (value === null || value === undefined) return value;
      var valueType = typeof value;
      if (valueType === "string") return value.length > 500 ? value.substring(0, 500) + "..." : value;
      if (valueType === "number" || valueType === "boolean") return value;
      if (valueType === "function") {
        var source = "";
        try { source = Function.prototype.toString.call(value).replace(/\s+/g, " ").substring(0, 180); } catch (e) {}
        return { type: "function", name: value.name || "", source: source };
      }
      if (valueType !== "object") return String(value);
      if (depth <= 0) return Array.isArray(value) ? "[Array]" : "[Object]";
      seen = seen || [];
      if (seen.indexOf(value) >= 0) return "[Circular]";
      seen.push(value);
      if (Array.isArray(value)) {
        return value.slice(0, 20).map(function (item) { return safeValue(item, depth - 1, seen); });
      }
      var result = {};
      Object.keys(value).slice(0, 40).forEach(function (key) {
        if (isSensitiveKey(key)) result[key] = "[REDACTED]";
        else {
          try { result[key] = safeValue(value[key], depth - 1, seen); } catch (e) { result[key] = "[Unreadable]"; }
        }
      });
      return result;
    } catch (e) {
      return "[PreviewError]";
    }
  }

  function describeRuntimeObject(path, obj) {
    var report = { path: path, type: typeof obj, properties: [] };
    if (!obj) return report;
    var names = [];
    try { names = names.concat(Object.getOwnPropertyNames(obj)); } catch (e) {}
    try {
      var proto = Object.getPrototypeOf(obj);
      if (proto && proto !== Object.prototype) names = names.concat(Object.getOwnPropertyNames(proto));
    } catch (e) {}
    var unique = {};
    names.forEach(function (name) {
      if (!name || unique[name]) return;
      unique[name] = true;
      var entry = { name: name, type: "unknown" };
      try {
        var value = obj[name];
        entry.type = typeof value;
        if (typeof value === "function") {
          try { entry.source = Function.prototype.toString.call(value).replace(/\s+/g, " ").substring(0, 180); } catch (e) {}
        }
      } catch (e) {
        entry.error = "unreadable";
      }
      report.properties.push(entry);
    });
    report.properties = report.properties.slice(0, 160);
    return report;
  }

  function runImsdkApiScan(force) {
    try {
      if (!window.imsdk || typeof window.imsdk.invoke !== "function") return false;
      if (imsdkScanSent && !force) return true;
      var report = {
        version: IMSDK_DISCOVERY_VERSION,
        at: new Date().toISOString(),
        href: location.href,
        title: document.title || "",
        objects: [
          describeRuntimeObject("window.imsdk", window.imsdk),
          describeRuntimeObject("window.QN", window.QN),
          describeRuntimeObject("window._vs", window._vs),
          describeRuntimeObject("window.TASK_CACHE", window.TASK_CACHE)
        ]
      };
      imsdkScanSent = true;
      send("imsdkApiScan", JSON.stringify(report));
      log("IMSDK API scan sent", report);
      return true;
    } catch (e) {
      warn("runImsdkApiScan failed", e);
      return false;
    }
  }

  function isTraceNoise(method) {
    return /GetNewMsg|GetRemoteHisMsg|GetCurrentLoginID|GetCurrentConversationID|SetConversationRead|SetFlagsPeerMsgReaded|item\.record\.query/i.test(method || "");
  }

  function isSendCandidate(method) {
    method = method || "";
    if (isTraceNoise(method)) return false;
    return /send|submit|dispatch|publish|post|create|insertText|inputbox/i.test(method);
  }

  function emitImsdkTrace(phase, method, param, result, error, elapsedMs) {
    try {
      var trace = {
        version: IMSDK_DISCOVERY_VERSION,
        sequence: ++imsdkTraceSequence,
        phase: phase,
        method: method || "",
        elapsedMs: elapsedMs || 0,
        at: new Date().toISOString(),
        href: location.href,
        conversation: safeValue(getConversationID(), 2, []),
        param: safeValue(param, 3, []),
        result: safeValue(result, 2, []),
        error: error ? String(error && error.message ? error.message : error).substring(0, 500) : ""
      };
      imsdkTraceRing.push(trace);
      if (imsdkTraceRing.length > 80) imsdkTraceRing.shift();
      send("imsdkInvokeTrace", JSON.stringify(trace));
    } catch (e) {
      warn("emitImsdkTrace failed", e);
    }
  }

  function installImsdkInvokeTraceHook() {
    try {
      if (window.__qnbotImsdkInvokeTraceInstalled) return true;
      if (!window.imsdk || typeof window.imsdk.invoke !== "function") return false;
      var original = window.imsdk.invoke;
      if (original.__qnbotImsdkWrapped) {
        window.__qnbotImsdkInvokeTraceInstalled = true;
        return true;
      }
      var wrapped = function (method, param) {
        var started = Date.now();
        var captureActive = Date.now() < (window.__qnbotImsdkCaptureUntil || 0);
        var shouldTrace = isSendCandidate(method) || (captureActive && !isTraceNoise(method));
        if (shouldTrace) emitImsdkTrace("call", method, param, null, null, 0);
        var result;
        try {
          result = original.apply(this, arguments);
        } catch (err) {
          if (shouldTrace) emitImsdkTrace("throw", method, param, null, err, Date.now() - started);
          throw err;
        }
        if (shouldTrace && result && typeof result.then === "function") {
          result.then(function (value) {
            emitImsdkTrace("resolve", method, null, value, null, Date.now() - started);
          }, function (err) {
            emitImsdkTrace("reject", method, null, null, err, Date.now() - started);
          });
        } else if (shouldTrace) {
          emitImsdkTrace("return", method, null, result, null, Date.now() - started);
        }
        return result;
      };
      wrapped.__qnbotImsdkWrapped = true;
      wrapped.__qnbotOriginal = original;
      window.__qnbotOriginalImsdkInvoke = original;
      window.imsdk.invoke = wrapped;
      window.__qnbotImsdkInvokeTraceInstalled = true;
      if (!window.__qnbotImsdkCaptureUntil) window.__qnbotImsdkCaptureUntil = Date.now() + 120000;
      log("IMSDK invoke trace installed; passive capture window=120s");
      sendStatus("imsdk-discovery-installed", true);
      return true;
    } catch (e) {
      warn("installImsdkInvokeTraceHook failed", e);
      return false;
    }
  }

  window.__qnbotRunImsdkScan = function () { return runImsdkApiScan(true); };
  window.__qnbotStartImsdkCapture = function (seconds) {
    var duration = Math.max(5, Math.min(600, Number(seconds) || 60));
    window.__qnbotImsdkCaptureUntil = Date.now() + duration * 1000;
    send("imsdkInvokeTrace", JSON.stringify({
      version: IMSDK_DISCOVERY_VERSION,
      phase: "capture-start",
      seconds: duration,
      at: new Date().toISOString(),
      href: location.href
    }));
    return { ok: true, seconds: duration, until: window.__qnbotImsdkCaptureUntil };
  };
  window.__qnbotGetImsdkTrace = function () { return imsdkTraceRing.slice(); };

'@

if (-not $inject.Contains('var IMSDK_DISCOVERY_VERSION = "20260714-imsdk-discovery-v1";')) {
    $anchor = '  function collectLanguageReports() {'
    if (-not $inject.Contains($anchor)) { throw "inject.js discovery insertion anchor not found" }
    $inject = $inject.Replace($anchor, $discoveryBlock + $anchor)
}

$statusOld = @'
      onEventNotify: typeof window.onEventNotify,
      onInvokeNotify: typeof window.onInvokeNotify,
      extra: extra || ""
'@
$statusNew = @'
      onEventNotify: typeof window.onEventNotify,
      onInvokeNotify: typeof window.onInvokeNotify,
      imsdkDiscoveryVersion: window.__qnbotImsdkDiscoveryVersion || "",
      imsdkDiscoveryInstalled: !!window.__qnbotImsdkInvokeTraceInstalled,
      imsdkCaptureActive: Date.now() < (window.__qnbotImsdkCaptureUntil || 0),
      extra: extra || ""
'@
$inject = Replace-Required $inject $statusOld $statusNew "add discovery status fields"

$loopOld = @'
    installQnNotifyHook();
    installImsdkHook();
    sendActiveConversationIfReady();
'@
$loopNew = @'
    installQnNotifyHook();
    installImsdkHook();
    installImsdkInvokeTraceHook();
    runImsdkApiScan(false);
    sendActiveConversationIfReady();
'@
$inject = Replace-Required $inject $loopOld $loopNew "install passive IMSDK scanner in loop"
Write-Utf8File $injectPath $inject

$markerFiles = @(
    "src/Bot/Common/LanguageRepairService.cs",
    "src/Bot/Common/QNInject.cs",
    "tools/check_qianniu_injection.ps1",
    "tools/repair_qianniu_language.ps1",
    "tools/patch_language_persist.ps1"
)
foreach ($file in $markerFiles) {
    if (-not (Test-Path $file)) { continue }
    $text = Read-Utf8File $file
    if ($text.Contains("20260713-zh-cn-v8")) {
        $text = $text.Replace("20260713-zh-cn-v8", "20260714-zh-cn-v9")
        Write-Utf8File $file $text
        Write-Host "Updated inject marker: $file"
    }
}

$serverPath = "src/Bot/ChromeNs/MyWebSocketServer.cs"
$server = Read-Utf8File $serverPath
$serverOld = @'
                        else if (wMsg.Type == "receiveNewMsg" || wMsg.Type == "onShopRobotReceriveNewMsgs" || wMsg.Type == "onChatDlgActive")
                        {
                            Task.Run(() => TryInitSession(session, "event:" + wMsg.Type));
                        }
'@
$serverNew = @'
                        else if (wMsg.Type == "imsdkApiScan")
                        {
                            Log.Info("IMSDK API扫描结果: " + wMsg.Response);
                        }
                        else if (wMsg.Type == "imsdkInvokeTrace")
                        {
                            Log.Info("IMSDK调用跟踪: " + wMsg.Response);
                        }
                        else if (wMsg.Type == "receiveNewMsg" || wMsg.Type == "onShopRobotReceriveNewMsgs" || wMsg.Type == "onChatDlgActive")
                        {
                            Task.Run(() => TryInitSession(session, "event:" + wMsg.Type));
                        }
'@
$server = Replace-Required $server $serverOld $serverNew "persist IMSDK discovery events in Bot log"
Write-Utf8File $serverPath $server

Write-Host "IMSDK discovery patch applied successfully."
