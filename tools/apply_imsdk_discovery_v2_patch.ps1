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
    'window.__qnbotInjectVersion = "20260714-zh-cn-v9";' `
    'window.__qnbotInjectVersion = "20260714-zh-cn-v10";' `
    "bump inject version"

$startAnchor = '  var IMSDK_DISCOVERY_VERSION = "20260714-imsdk-discovery-v1";'
$endAnchor = '  function collectLanguageReports() {'
$startIndex = $inject.IndexOf($startAnchor, [StringComparison]::Ordinal)
$endIndex = $inject.IndexOf($endAnchor, [StringComparison]::Ordinal)
if ($startIndex -lt 0 -or $endIndex -le $startIndex) {
    if ($inject.Contains('20260714-imsdk-discovery-v2')) {
        Write-Host "IMSDK discovery v2 block already applied."
    } else {
        throw "Unable to locate IMSDK discovery v1 block."
    }
} else {
$newBlock = @'
  var IMSDK_DISCOVERY_VERSION = "20260714-imsdk-discovery-v2";
  var imsdkScanSignature = "";
  var imsdkTraceSequence = 0;
  var imsdkTraceRing = [];
  var imsdkTraceDedup = {};
  var bridgeHookRegistry = {};
  var lastUserIntentAt = 0;
  var lastCaptureNoticeAt = 0;
  window.__qnbotImsdkDiscoveryVersion = IMSDK_DISCOVERY_VERSION;
  window.__qnbotImsdkBridgeHookCount = 0;

  function isSensitiveKey(name) {
    return /token|cookie|authorization|password|passwd|secret|session|ticket|credential|sign|csrf/i.test(name || "");
  }

  function safeValue(value, depth, seen) {
    try {
      if (value === null || value === undefined) return value;
      var valueType = typeof value;
      if (valueType === "string") return value.length > 500 ? value.substring(0, 500) + "..." : value;
      if (valueType === "number" || valueType === "boolean") return value;
      if (valueType === "function") {
        var source = "";
        try { source = Function.prototype.toString.call(value).replace(/\s+/g, " ").substring(0, 240); } catch (e) {}
        return { type: "function", name: value.name || "", source: source };
      }
      if (valueType !== "object") return String(value);
      if (depth <= 0) return Array.isArray(value) ? "[Array]" : "[Object]";
      seen = seen || [];
      if (seen.indexOf(value) >= 0) return "[Circular]";
      seen.push(value);
      var result;
      if (Array.isArray(value)) {
        result = value.slice(0, 20).map(function (item) { return safeValue(item, depth - 1, seen); });
      } else {
        result = {};
        Object.keys(value).slice(0, 50).forEach(function (key) {
          if (isSensitiveKey(key)) result[key] = "[REDACTED]";
          else {
            try { result[key] = safeValue(value[key], depth - 1, seen); } catch (e) { result[key] = "[Unreadable]"; }
          }
        });
      }
      seen.pop();
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
          try { entry.source = Function.prototype.toString.call(value).replace(/\s+/g, " ").substring(0, 240); } catch (e) {}
          if (value.__qnbotOriginal) entry.wrapped = true;
        }
      } catch (e) {
        entry.error = "unreadable";
      }
      report.properties.push(entry);
    });
    report.properties = report.properties.slice(0, 180);
    return report;
  }

  function taskCacheReports() {
    var reports = [];
    try {
      if (!window.TASK_CACHE) return reports;
      Object.keys(window.TASK_CACHE).slice(0, 16).forEach(function (key) {
        var task = window.TASK_CACHE[key];
        reports.push({
          path: "window.TASK_CACHE." + key,
          type: typeof task,
          preview: safeValue(task, 3, [])
        });
      });
    } catch (e) {}
    return reports;
  }

  function isPageActive() {
    try {
      if (Date.now() - lastUserIntentAt < 15000) return true;
      if (document.visibilityState === "hidden") return false;
      return !!getConversationID();
    } catch (e) { return false; }
  }

  function isCaptureActive() {
    return Date.now() < (window.__qnbotImsdkCaptureUntil || 0);
  }

  function isTraceNoise(method) {
    return /GetNewMsg|GetRemoteHisMsg|GetCurrentLoginID|GetCurrentConversationID|SetConversationRead|SetFlagsPeerMsgReaded|GetSmartAssistantStatus|IsOverseaUser|GetSmartTipSwitch|GetHotKeyList|item\.record\.query|application\.getVersion/i.test(method || "");
  }

  function isSendCandidate(method) {
    method = method || "";
    if (isTraceNoise(method)) return false;
    return /send|submit|dispatch|publish|post|create|insert|inputbox|message|singlemsg|chat|reply|wangwang|broadcast/i.test(method);
  }

  function previewKey(value) {
    try { return JSON.stringify(safeValue(value, 2, [])).substring(0, 700); } catch (e) { return ""; }
  }

  function shouldEmitTrace(bridge, phase, method, param, result) {
    var now = Date.now();
    var key = [bridge || "", phase || "", method || "", previewKey(param), previewKey(result)].join("|");
    var last = imsdkTraceDedup[key] || 0;
    if (now - last < 500) return false;
    imsdkTraceDedup[key] = now;
    if (Object.keys(imsdkTraceDedup).length > 300) {
      Object.keys(imsdkTraceDedup).forEach(function (item) {
        if (now - imsdkTraceDedup[item] > 10000) delete imsdkTraceDedup[item];
      });
    }
    return true;
  }

  function emitImsdkTrace(bridge, phase, method, param, result, error, elapsedMs, force) {
    try {
      if (!force && !shouldEmitTrace(bridge, phase, method, param, result)) return;
      var trace = {
        version: IMSDK_DISCOVERY_VERSION,
        sequence: ++imsdkTraceSequence,
        bridge: bridge || "imsdk",
        phase: phase,
        method: method || "",
        elapsedMs: elapsedMs || 0,
        at: new Date().toISOString(),
        href: location.href,
        pageActive: isPageActive(),
        captureActive: isCaptureActive(),
        captureReason: window.__qnbotImsdkCaptureReason || "",
        conversation: safeValue(getConversationID(), 2, []),
        param: safeValue(param, 3, []),
        result: safeValue(result, 2, []),
        error: error ? String(error && error.message ? error.message : error).substring(0, 500) : ""
      };
      imsdkTraceRing.push(trace);
      if (imsdkTraceRing.length > 160) imsdkTraceRing.shift();
      send("imsdkInvokeTrace", JSON.stringify(trace));
    } catch (e) {
      warn("emitImsdkTrace failed", e);
    }
  }

  function startCapture(reason, seconds) {
    var duration = Math.max(3, Math.min(60, Number(seconds) || 8));
    var now = Date.now();
    lastUserIntentAt = now;
    window.__qnbotImsdkCaptureReason = reason || "manual";
    window.__qnbotImsdkCaptureUntil = Math.max(window.__qnbotImsdkCaptureUntil || 0, now + duration * 1000);
    if (now - lastCaptureNoticeAt > 500) {
      lastCaptureNoticeAt = now;
      emitImsdkTrace("dom", "capture-start", reason || "manual", { seconds: duration }, null, null, 0, true);
    }
    return { ok: true, seconds: duration, until: window.__qnbotImsdkCaptureUntil, reason: window.__qnbotImsdkCaptureReason };
  }

  function likelyComposerElement(element) {
    try {
      if (!element || element.nodeType !== 1) return false;
      var tag = (element.tagName || "").toLowerCase();
      if (tag === "textarea" || tag === "input") return true;
      if (element.isContentEditable) return true;
      var role = element.getAttribute && element.getAttribute("role");
      return role === "textbox";
    } catch (e) { return false; }
  }

  function closestClickable(element) {
    var current = element;
    for (var i = 0; current && i < 8; i++, current = current.parentElement) {
      var tag = (current.tagName || "").toLowerCase();
      var role = current.getAttribute && current.getAttribute("role");
      if (tag === "button" || role === "button" || typeof current.onclick === "function") return current;
    }
    return element;
  }

  function installUserIntentCapture() {
    if (window.__qnbotUserIntentCaptureInstalled) return true;
    try {
      document.addEventListener("keydown", function (event) {
        try {
          if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return;
          if (likelyComposerElement(event.target)) startCapture("composer-enter", 8);
        } catch (e) {}
      }, true);
      document.addEventListener("click", function (event) {
        try {
          var target = closestClickable(event.target);
          var text = ((target && (target.innerText || target.textContent)) || "") + " " + ((target && target.getAttribute && (target.getAttribute("aria-label") || target.getAttribute("title"))) || "");
          var cls = target && target.className ? String(target.className) : "";
          if (/发送|發送|send/i.test(text) || /send|submit/i.test(cls)) startCapture("send-button-click", 8);
        } catch (e) {}
      }, true);
      document.addEventListener("submit", function () { startCapture("form-submit", 8); }, true);
      window.__qnbotUserIntentCaptureInstalled = true;
      return true;
    } catch (e) {
      warn("installUserIntentCapture failed", e);
      return false;
    }
  }

  function methodFromConfig(config, fallback) {
    try {
      if (!config) return fallback;
      if (typeof config === "string") return config;
      var api = config.api || {};
      return api.alias || api.uri || api.name || config.url || config.method || fallback;
    } catch (e) { return fallback; }
  }

  function installFunctionHook(owner, property, bridge, resolver) {
    try {
      if (!owner || typeof owner[property] !== "function") return false;
      var key = bridge + "." + property;
      var current = owner[property];
      if (current.__qnbotBridgeWrapped && current.__qnbotBridgeKey === key) {
        bridgeHookRegistry[key] = true;
        return true;
      }
      var original = current.__qnbotOriginal || current;
      var wrapped = function () {
        var args = Array.prototype.slice.call(arguments);
        var method = key;
        try { if (resolver) method = resolver(args) || key; } catch (e) {}
        var trace = !isTraceNoise(method) && (isSendCandidate(method) || (isCaptureActive() && isPageActive()));
        var started = Date.now();
        if (trace) emitImsdkTrace(bridge, "call", method, args, null, null, 0, false);
        var result;
        try {
          result = original.apply(this, arguments);
        } catch (error) {
          if (trace) emitImsdkTrace(bridge, "throw", method, null, null, error, Date.now() - started, false);
          throw error;
        }
        if (trace && result && typeof result.then === "function") {
          result.then(function (value) {
            emitImsdkTrace(bridge, "resolve", method, null, value, null, Date.now() - started, false);
          }, function (error) {
            emitImsdkTrace(bridge, "reject", method, null, null, error, Date.now() - started, false);
          });
        } else if (trace) {
          emitImsdkTrace(bridge, "return", method, null, result, null, Date.now() - started, false);
        }
        return result;
      };
      wrapped.__qnbotBridgeWrapped = true;
      wrapped.__qnbotBridgeKey = key;
      wrapped.__qnbotOriginal = original;
      try {
        Object.keys(current).forEach(function (name) {
          if (name.indexOf("__qnbot") === 0) return;
          try { wrapped[name] = current[name]; } catch (e) {}
        });
      } catch (e) {}
      owner[property] = wrapped;
      if (owner[property] !== wrapped) return false;
      bridgeHookRegistry[key] = true;
      window.__qnbotImsdkBridgeHookCount = Object.keys(bridgeHookRegistry).length;
      return true;
    } catch (e) {
      return false;
    }
  }

  function installContainerHooks(container, bridge, limit) {
    var installed = 0;
    if (!container) return installed;
    var names = [];
    try { names = Object.getOwnPropertyNames(container); } catch (e) { return installed; }
    names.slice(0, limit || 60).forEach(function (name) {
      if (!/send|msg|message|chat|invoke|call|post|emit|notify|event|dispatch|publish|broadcast|reply/i.test(name)) return;
      if (installFunctionHook(container, name, bridge, function (args) {
        var suffix = args && typeof args[0] === "string" ? ":" + args[0] : "";
        return bridge + "." + name + suffix;
      })) installed++;
    });
    return installed;
  }

  function installKnownBridgeHooks() {
    var installed = 0;
    if (window.imsdk) {
      if (installFunctionHook(window.imsdk, "invoke", "imsdk", function (args) { return args[0] || "imsdk.invoke"; })) installed++;
      if (installFunctionHook(window.imsdk, "broadcast", "imsdk", function (args) { return "imsdk.broadcast:" + (args[0] || ""); })) installed++;
    }
    if (window.QN) {
      if (installFunctionHook(window.QN, "invoke", "QN", function (args) { return "QN.invoke:" + methodFromConfig(args[0], "unknown"); })) installed++;
      if (installFunctionHook(window.QN, "ajax", "QN", function (args) { return "QN.ajax:" + methodFromConfig(args[0], "unknown"); })) installed++;
      if (installFunctionHook(window.QN, "emit", "QN", function (args) { return "QN.emit:" + (args[0] || ""); })) installed++;
      if (installFunctionHook(window.QN, "postEvent", "QN", function (args) { return "QN.postEvent:" + (args[0] || ""); })) installed++;
      installed += installContainerHooks(window.QN.wangwang, "QN.wangwang", 80);
      installed += installContainerHooks(window.QN.component, "QN.component", 50);
      installed += installContainerHooks(window.QN.app, "QN.app", 50);
    }
    if (window.workbench) {
      installed += installContainerHooks(window.workbench, "workbench", 80);
      installed += installContainerHooks(window.workbench.im, "workbench.im", 120);
    }
    if (window._vs && window._vs.SDK) installed += installContainerHooks(window._vs.SDK, "_vs.SDK", 80);
    window.__qnbotImsdkBridgeHooksInstalled = Object.keys(bridgeHookRegistry).length > 0;
    window.__qnbotImsdkInvokeTraceInstalled = window.__qnbotImsdkBridgeHooksInstalled;
    window.__qnbotImsdkBridgeHookCount = Object.keys(bridgeHookRegistry).length;
    return installed;
  }

  function runImsdkApiScan(force) {
    try {
      var objects = [
        describeRuntimeObject("window.imsdk", window.imsdk),
        describeRuntimeObject("window.__qnbotOriginalImsdkInvoke", { invoke: window.__qnbotOriginalImsdkInvoke }),
        describeRuntimeObject("window.QN", window.QN),
        describeRuntimeObject("window.QN.wangwang", window.QN && window.QN.wangwang),
        describeRuntimeObject("window.QN.component", window.QN && window.QN.component),
        describeRuntimeObject("window.QN.app", window.QN && window.QN.app),
        describeRuntimeObject("window._vs", window._vs),
        describeRuntimeObject("window._vs.SDK", window._vs && window._vs.SDK),
        describeRuntimeObject("window.workbench", window.workbench),
        describeRuntimeObject("window.workbench.im", window.workbench && window.workbench.im),
        describeRuntimeObject("window.TASK_CACHE", window.TASK_CACHE)
      ].concat(taskCacheReports());
      var signature = [
        !!window.imsdk,
        !!window.QN,
        !!window.workbench,
        !!(window.workbench && window.workbench.im),
        window.__qnbotImsdkBridgeHookCount || 0,
        window.TASK_CACHE ? Object.keys(window.TASK_CACHE).length : 0
      ].join("|");
      if (!force && signature === imsdkScanSignature) return true;
      imsdkScanSignature = signature;
      var report = {
        version: IMSDK_DISCOVERY_VERSION,
        at: new Date().toISOString(),
        href: location.href,
        title: document.title || "",
        pageActive: isPageActive(),
        hookCount: window.__qnbotImsdkBridgeHookCount || 0,
        hookKeys: Object.keys(bridgeHookRegistry).slice(0, 180),
        objects: objects
      };
      send("imsdkApiScan", JSON.stringify(report));
      log("IMSDK API scan v2 sent", report);
      return true;
    } catch (e) {
      warn("runImsdkApiScan failed", e);
      return false;
    }
  }

  function installImsdkInvokeTraceHook() {
    try {
      installUserIntentCapture();
      installKnownBridgeHooks();
      if (window.__qnbotImsdkBridgeHooksInstalled) {
        if (!window.__qnbotImsdkCaptureUntil) startCapture("startup", 8);
        return true;
      }
      return false;
    } catch (e) {
      warn("installImsdkInvokeTraceHook failed", e);
      return false;
    }
  }

  window.__qnbotRunImsdkScan = function () { return runImsdkApiScan(true); };
  window.__qnbotStartImsdkCapture = function (seconds) { return startCapture("manual", seconds || 30); };
  window.__qnbotGetImsdkTrace = function () { return imsdkTraceRing.slice(); };
  window.__qnbotGetBridgeHooks = function () { return Object.keys(bridgeHookRegistry).slice(); };

'@
    $inject = $inject.Substring(0, $startIndex) + $newBlock + $inject.Substring($endIndex)
}

$statusOld = @'
      imsdkDiscoveryVersion: window.__qnbotImsdkDiscoveryVersion || "",
      imsdkDiscoveryInstalled: !!window.__qnbotImsdkInvokeTraceInstalled,
      imsdkCaptureActive: Date.now() < (window.__qnbotImsdkCaptureUntil || 0),
      extra: extra || ""
'@
$statusNew = @'
      imsdkDiscoveryVersion: window.__qnbotImsdkDiscoveryVersion || "",
      imsdkDiscoveryInstalled: !!window.__qnbotImsdkBridgeHooksInstalled,
      imsdkBridgeHookCount: window.__qnbotImsdkBridgeHookCount || 0,
      imsdkCaptureActive: Date.now() < (window.__qnbotImsdkCaptureUntil || 0),
      imsdkCaptureReason: window.__qnbotImsdkCaptureReason || "",
      userIntentCaptureInstalled: !!window.__qnbotUserIntentCaptureInstalled,
      extra: extra || ""
'@
$inject = Replace-Required $inject $statusOld $statusNew "extend IMSDK v2 status fields"
Write-Utf8File $injectPath $inject

$markerFiles = @(
    "src/Bot/Common/LanguageRepairService.cs",
    "src/Bot/Common/QNInject.cs",
    "tools/check_qianniu_injection.ps1",
    "tools/repair_qianniu_language.ps1"
)
foreach ($file in $markerFiles) {
    if (-not (Test-Path $file)) { continue }
    $text = Read-Utf8File $file
    if ($text.Contains("20260714-zh-cn-v9")) {
        $text = $text.Replace("20260714-zh-cn-v9", "20260714-zh-cn-v10")
        Write-Utf8File $file $text
        Write-Host "Updated inject marker: $file"
    }
}

Write-Host "IMSDK discovery v2 patch applied successfully."
Write-Host "The v2 tracer remains passive and never invokes an unknown send API."
