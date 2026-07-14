
// qnbot persistent zh-CN language lock
(function(){
  function lockZhCnLanguage(){
    try{
      localStorage.setItem("locale","zh-CN");
      localStorage.setItem("language","zh-CN");
      localStorage.setItem("lang","zh-CN");
      sessionStorage.setItem("locale","zh-CN");
      sessionStorage.setItem("language","zh-CN");
      sessionStorage.setItem("lang","zh-CN");
      document.cookie="locale=zh-CN; path=/; max-age=31536000";
      document.cookie="language=zh-CN; path=/; max-age=31536000";
      document.cookie="lang=zh-CN; path=/; max-age=31536000";
    }catch(e){}
  }
  function repairIfChanged(){
    try{
      var values = [
        localStorage.getItem("locale"),
        localStorage.getItem("language"),
        localStorage.getItem("lang")
      ].join("|").toLowerCase();
      if (values.indexOf("zh-tw") >= 0 || values.indexOf("zh_hk") >= 0 || values.indexOf("traditional") >= 0 || values.indexOf("繁体") >= 0 || values.indexOf("繁體") >= 0) {
        lockZhCnLanguage();
      }
    }catch(e){
      lockZhCnLanguage();
    }
  }
  lockZhCnLanguage();
  try { window.addEventListener("DOMContentLoaded", lockZhCnLanguage, true); } catch(e) {}
  try { window.addEventListener("load", lockZhCnLanguage, true); } catch(e) {}
  try { window.addEventListener("storage", repairIfChanged, true); } catch(e) {}
  setInterval(repairIfChanged, 1000);
})();
window.__qnbotInjectVersion = "20260714-zh-cn-v10";
window.__qnbotRuntimePatch = "20260707-safe-hooks-v5";
window.__qnbotLanguagePatch = "20260713-hans-all-pages-v3";

(function () {
  if (window.__qnbotMainInstalled) return;
  window.__qnbotMainInstalled = true;

  var WS_URL = "ws://127.0.0.1:41010";
  var OFFICIAL_SCRIPT = "https://iseiya.taobao.com/imsupport?locale=zh-CN&lang=zh-CN";
  var pending = [];
  var reconnectTimer = null;
  var heartbeatTimer = null;
  var lastStatusText = "";
  var lastStatusAt = 0;
  var lastActiveText = "";

  window._buyerCache = window._buyerCache || new Map();

  function log() { try { console.log.apply(console, ["[qnbot-safe]"].concat(Array.prototype.slice.call(arguments))); } catch (e) {} }
  function warn() { try { console.warn.apply(console, ["[qnbot-safe]"].concat(Array.prototype.slice.call(arguments))); } catch (e) {} }

  function forceZhCn() {
    try {
      if (document.documentElement) document.documentElement.setAttribute("lang", "zh-CN");
      ["locale", "lang", "language", "i18nextLng", "umi_locale", "appLocale", "localeCode", "ALI_LANG", "qn_lang"].forEach(function (key) {
        try { localStorage.setItem(key, "zh-CN"); } catch (e) {}
        try { sessionStorage.setItem(key, "zh-CN"); } catch (e) {}
      });
      try { document.cookie = "locale=zh-CN; path=/; max-age=31536000"; } catch (e) {}
      try { document.cookie = "lang=zh-CN; path=/; max-age=31536000"; } catch (e) {}
      try { Object.defineProperty(navigator, "language", { get: function () { return "zh-CN"; }, configurable: true }); } catch (e) {}
      try { Object.defineProperty(navigator, "languages", { get: function () { return ["zh-CN", "zh"]; }, configurable: true }); } catch (e) {}
    } catch (e) { warn("forceZhCn failed", e); }
  }

  function installSimplifiedChineseGuard() {
    if (window.__qnbotHansPatchInstalled) return;
    window.__qnbotHansPatchInstalled = true;

    // Qianniu may restore a zh-TW/zh-HK profile after the page has started. Keep
    // the locale fixed and convert already-rendered UI labels as a fallback.
    var map = {
      "發":"发","關":"关","閉":"闭","聯":"联","繫":"系","絡":"络","連":"连","線":"线","狀":"状","態":"态",
      "訂":"订","單":"单","號":"号","記":"记","錄":"录","買":"买","賣":"卖","貨":"货","總":"总","價":"价",
      "後":"后","詳":"详","歷":"历","諮":"咨","詢":"询","寶":"宝","貝":"贝","設":"设","薦":"荐","優":"优",
      "計":"计","儲":"储","資":"资","評":"评","備":"备","註":"注","會":"会","員":"员","產":"产","編":"编",
      "標":"标","題":"题","輸":"输","轉":"转","機":"机","個":"个","嗎":"吗","這":"这","無":"无","處":"处",
      "購":"购","點":"点","擊":"击","啟":"启","確":"确","認":"认","當":"当","來":"来","頁":"页","網":"网",
      "開":"开","請":"请","訊":"讯","裡":"里","滿":"满","僅":"仅","條":"条","數":"数","補":"补","遲":"迟",
      "幫":"帮","該":"该","讓":"让","與":"与","為":"为","將":"将","顯":"显","傳":"传","圖":"图","覽":"览",
      "應":"应","實":"实","樣":"样","類":"类","別":"别","選":"选","擇":"择","導":"导","戶":"户","務":"务",
      "獲":"获","時":"时","間":"间","從":"从","進":"进","還":"还","據":"据","權":"权","檔":"档","庫":"库",
      "並":"并","於":"于","對":"对","話":"话","軟":"软","體":"体","帳":"账","載":"载","讀":"读","寫":"写",
      "錯":"错","誤":"误","刪":"删","舊":"旧","復":"复","離":"离","區":"区","遠":"远","幣":"币","繼":"继"
    };

    var observedRoots = [];
    var observedFrameDocuments = [];
    var observedFrames = [];
    var stats = window.__qnbotHansStats = {
      convertedTextNodes: 0,
      convertedAttributes: 0,
      shadowRoots: 0,
      frameDocuments: 0,
      lastRunAt: 0
    };
    var observer = null;

    function convertText(value) {
      if (!value || typeof value !== "string") return value;
      return value.replace(/[\u4e00-\u9fff]/g, function (ch) { return map[ch] || ch; });
    }

    function shouldSkip(element) {
      if (!element || !element.tagName) return false;
      var tag = element.tagName.toUpperCase();
      return tag === "SCRIPT" || tag === "STYLE" || tag === "TEXTAREA" || tag === "INPUT" ||
        tag === "PRE" || tag === "CODE" || element.isContentEditable;
    }

    function convertAttributes(element) {
      if (!element || element.nodeType !== 1 || shouldSkip(element)) return;
      ["title", "aria-label", "placeholder"].forEach(function (name) {
        try {
          if (!element.hasAttribute(name)) return;
          var oldValue = element.getAttribute(name);
          var newValue = convertText(oldValue);
          if (newValue !== oldValue) {
            element.setAttribute(name, newValue);
            stats.convertedAttributes++;
          }
        } catch (e) {}
      });
    }

    function convertTextNode(node) {
      if (!node || node.nodeType !== 3 || shouldSkip(node.parentElement)) return;
      var converted = convertText(node.nodeValue);
      if (converted !== node.nodeValue) {
        node.nodeValue = converted;
        stats.convertedTextNodes++;
      }
    }

    function observeRoot(root) {
      if (!root || observedRoots.indexOf(root) >= 0) return;
      observedRoots.push(root);
      if (root.nodeType === 11) stats.shadowRoots++;
      try {
        var ownerDocument = root.nodeType === 9 ? root : root.ownerDocument;
        if (ownerDocument && ownerDocument !== document && observedFrameDocuments.indexOf(ownerDocument) < 0) {
          observedFrameDocuments.push(ownerDocument);
          stats.frameDocuments = observedFrameDocuments.length;
        }
      } catch (e) {}
      try {
        if (observer) observer.observe(root, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ["title", "aria-label", "placeholder"] });
      } catch (e) { warn("observeRoot failed", e); }
      scanRoot(root);
    }

    function scanFrame(frame) {
      if (!frame) return;
      if (observedFrames.indexOf(frame) < 0) {
        observedFrames.push(frame);
        try { frame.addEventListener("load", function () { scanFrame(frame); }); } catch (e) {}
      }
      try {
        var frameDocument = frame.contentDocument;
        if (!frameDocument) return;
        observeRoot(frameDocument.documentElement || frameDocument);
      } catch (e) {}
    }

    function scanRoot(root) {
      try {
        if (!root) return;
        if (root.nodeType === 3) {
          convertTextNode(root);
          return;
        }
        if (root.nodeType !== 1 && root.nodeType !== 9 && root.nodeType !== 11) return;
        if (shouldSkip(root)) return;
        convertAttributes(root);
        var ownerDocument = root.nodeType === 9 ? root : (root.ownerDocument || document);
        var walker = ownerDocument.createTreeWalker(root, 4, {
          acceptNode: function (node) {
            return shouldSkip(node.parentElement) ? 2 : 1;
          }
        });
        var nodes = [];
        while (walker.nextNode()) nodes.push(walker.currentNode);
        nodes.forEach(convertTextNode);

        if (root.querySelectorAll) {
          Array.prototype.forEach.call(root.querySelectorAll("*"), function (element) {
            convertAttributes(element);
            try { if (element.shadowRoot) observeRoot(element.shadowRoot); } catch (e) {}
            try { if ((element.tagName || "").toUpperCase() === "IFRAME") scanFrame(element); } catch (e) {}
          });
        }
      } catch (e) { warn("scanRoot failed", e); }
    }

    function run() {
      stats.lastRunAt = Date.now();
      scanRoot(document.body || document.documentElement);
    }
    window.__qnbotForceHansText = run;
    try {
      observer = new MutationObserver(function (mutations) {
        mutations.forEach(function (mutation) {
          if (mutation.type === "characterData") convertTextNode(mutation.target);
          if (mutation.addedNodes) Array.prototype.forEach.call(mutation.addedNodes, scanRoot);
          if (mutation.type === "attributes") convertAttributes(mutation.target);
        });
      });
    } catch (e) { warn("language observer failed", e); }

    // Capture open and closed Shadow DOM roots created after this early script.
    try {
      if (window.Element && Element.prototype.attachShadow && !Element.prototype.__qnbotOriginalAttachShadow) {
        var originalAttachShadow = Element.prototype.attachShadow;
        Element.prototype.__qnbotOriginalAttachShadow = originalAttachShadow;
        Element.prototype.attachShadow = function () {
          var shadowRoot = originalAttachShadow.apply(this, arguments);
          observeRoot(shadowRoot);
          return shadowRoot;
        };
      }
    } catch (e) { warn("attachShadow hook failed", e); }

    observeRoot(document.documentElement || document);
    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", run);
    else run();
    setTimeout(run, 500);
    setTimeout(run, 1500);
    setTimeout(run, 3500);
    setInterval(run, 3000);
  }

  function appendOfficialWhenReady() {
    if (window.__qnbotOfficialSupportLoaded) return;
    window.__qnbotOfficialSupportLoaded = true;
    var append = function () {
      try {
        if (!document.body) { setTimeout(append, 300); return; }
        var existed = Array.prototype.slice.call(document.getElementsByTagName("script")).some(function (s) { return (s.src || "").indexOf("iseiya.taobao.com/imsupport") >= 0; });
        if (existed) return;
        var script = document.createElement("script");
        script.type = "text/javascript";
        script.src = OFFICIAL_SCRIPT;
        script.onload = function () { log("official imsupport loaded"); };
        script.onerror = function (e) { warn("official imsupport load failed", e); };
        document.body.appendChild(script);
        log("official imsupport appended");
      } catch (e) { warn("appendOfficialWhenReady failed", e); }
    };
    append();
  }

  function socketOpen() { return window.chatWebsocket && window.chatWebsocket.readyState === WebSocket.OPEN; }

  function send(type, response) {
    var payload = JSON.stringify({ type: type, response: response || "" });
    if (socketOpen()) {
      try { window.chatWebsocket.send(payload); return; } catch (e) { warn("send failed", e); }
    }
    if (pending.length < 200) pending.push(payload);
    setupWebSocket();
  }

  function flushPending() {
    if (!socketOpen()) return;
    while (pending.length > 0) {
      try { window.chatWebsocket.send(pending.shift()); } catch (e) { break; }
    }
  }

  function getLoginID() {
    try { return window._vs && window._vs.loginID ? window._vs.loginID : null; } catch (e) { return null; }
  }

  function getConversationID() {
    try { return window._vs && window._vs.conversationID ? window._vs.conversationID : window._conversationId || null; } catch (e) { return null; }
  }

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
          if (/å‘é€|ç™¼é€|send/i.test(text) || /send|submit/i.test(cls)) startCapture("send-button-click", 8);
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
  function collectLanguageReports() {
    var reports = [];
    var prefix = "__qnbotLanguageReport:";
    var now = Date.now();
    try {
      for (var i = 0; i < localStorage.length; i++) {
        var key = localStorage.key(i) || "";
        if (key.indexOf(prefix) !== 0) continue;
        try {
          var report = JSON.parse(localStorage.getItem(key) || "{}");
          if (!report.lastSeenAt || now - report.lastSeenAt > 15000) continue;
          reports.push({
            version: report.version || "",
            path: report.path || key.substring(prefix.length),
            title: report.title || "",
            convertedTextNodes: report.convertedTextNodes || 0,
            convertedAttributes: report.convertedAttributes || 0,
            shadowRoots: report.shadowRoots || 0,
            frameDocuments: report.frameDocuments || 0
          });
        } catch (e) {}
      }
    } catch (e) {}
    reports.sort(function (a, b) {
      var aCount = a.convertedTextNodes + a.convertedAttributes;
      var bCount = b.convertedTextNodes + b.convertedAttributes;
      if (aCount !== bCount) return bCount - aCount;
      return (a.path || "").localeCompare(b.path || "");
    });
    return reports.slice(0, 20);
  }

  function statusObject(extra) {
    var login = getLoginID();
    var conv = getConversationID();
    var obj = {
      patch: "__qnbotStatusPatch",
      injectVersion: window.__qnbotInjectVersion || "",
      runtime: window.__qnbotRuntimePatch,
      languagePatch: window.__qnbotLanguagePatch || "",
      languageInstalled: !!window.__qnbotHansPatchInstalled,
      documentLang: document.documentElement ? document.documentElement.getAttribute("lang") || "" : "",
      navigatorLanguage: navigator.language || "",
      convertedTextNodes: window.__qnbotHansStats ? window.__qnbotHansStats.convertedTextNodes : 0,
      convertedAttributes: window.__qnbotHansStats ? window.__qnbotHansStats.convertedAttributes : 0,
      shadowRoots: window.__qnbotHansStats ? window.__qnbotHansStats.shadowRoots : 0,
      frameDocuments: window.__qnbotHansStats ? window.__qnbotHansStats.frameDocuments : 0,
      languagePages: collectLanguageReports(),
      href: location.href,
      title: document.title || "",
      hasImsdk: !!(window.imsdk && typeof window.imsdk.invoke === "function"),
      hasImsdkOn: !!(window.imsdk && typeof window.imsdk.on === "function"),
      hasQN: !!(window.QN && typeof window.QN.regEvent === "function"),
      hasVs: !!window._vs,
      hasLoginID: !!login,
      hasConversationID: !!conv,
      onEventNotify: typeof window.onEventNotify,
      onInvokeNotify: typeof window.onInvokeNotify,
      imsdkDiscoveryVersion: window.__qnbotImsdkDiscoveryVersion || "",
      imsdkDiscoveryInstalled: !!window.__qnbotImsdkBridgeHooksInstalled,
      imsdkBridgeHookCount: window.__qnbotImsdkBridgeHookCount || 0,
      imsdkCaptureActive: Date.now() < (window.__qnbotImsdkCaptureUntil || 0),
      imsdkCaptureReason: window.__qnbotImsdkCaptureReason || "",
      userIntentCaptureInstalled: !!window.__qnbotUserIntentCaptureInstalled,
      extra: extra || ""
    };
    try { if (login) obj.loginNick = login.nick || login.uid || ""; } catch (e) {}
    try { if (conv) obj.conversationNick = conv.nick || conv.ccode || ""; } catch (e) {}
    return obj;
  }

  function sendStatus(extra, force) {
    var obj = statusObject(extra);
    var text = JSON.stringify(obj);
    var t = Date.now();
    if (!force && text === lastStatusText && t - lastStatusAt < 10000) return;
    lastStatusText = text;
    lastStatusAt = t;
    send("qnbotStatus", text);
  }

  function scheduleReconnect() {
    if (reconnectTimer) return;
    reconnectTimer = setTimeout(function () {
      reconnectTimer = null;
      setupWebSocket();
    }, 3000);
  }

  function setupWebSocket() {
    var old = window.chatWebsocket;
    if (old && (old.readyState === WebSocket.OPEN || old.readyState === WebSocket.CONNECTING)) return;
    try {
      var socket = new WebSocket(WS_URL);
      socket.onopen = function () {
        window.chatWebsocket = socket;
        log("websocket connected");
        sendStatus("websocket-open", true);
        flushPending();
        clearInterval(heartbeatTimer);
        heartbeatTimer = setInterval(function () {
          try {
            if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: "hi" }));
            else clearInterval(heartbeatTimer);
          } catch (e) { clearInterval(heartbeatTimer); }
        }, 3000);
      };
      socket.onmessage = async function (event) {
        try {
          var param = JSON.parse(event.data);
          if (param.method === "execute") {
            try {
              var res = await eval(param.expression);
              socket.send(JSON.stringify({ type: "execute", response: JSON.stringify(res) }));
            } catch (err) {
              warn("execute eval failed", err);
              socket.send(JSON.stringify({ type: "execute", response: "" }));
            }
          }
        } catch (e) { warn("socket message failed", e); }
      };
      socket.onclose = function () {
        if (window.chatWebsocket === socket) window.chatWebsocket = null;
        clearInterval(heartbeatTimer);
        scheduleReconnect();
      };
      socket.onerror = function (e) { warn("websocket error", e); };
    } catch (e) { warn("setupWebSocket failed", e); scheduleReconnect(); }
  }

  window.___setupWebSocket = setupWebSocket;

  function updateFromConversation(conv) {
    try {
      if (!conv || !conv.ccode) return;
      if (!window._buyerCache.has(conv.ccode)) window._buyerCache.set(conv.ccode, conv);
    } catch (e) { warn("updateFromConversation failed", e); }
  }

  function updateBuyerCacheFromLocal() {
    try {
      if (!window._db || !window._db.msgDataMap) return;
      var login = getLoginID();
      if (!login || !login.nick) return;
      Array.from(window._db.msgDataMap).forEach(function (entry) {
        var ccode = entry[0];
        var messages = entry[1] || [];
        if (window._buyerCache.has(ccode)) return;
        for (var i = 0; i < messages.length; i++) {
          var message = messages[i] || {};
          var ext = message.ext || {};
          var origin = message.originBanamaMessage || {};
          var senderNick = ext.sender_nick || "";
          var receiverNick = ext.receiver_nick || "";
          if (!senderNick || !receiverNick) continue;
          if (senderNick.indexOf(login.nick) >= 0) { window._buyerCache.set(ccode, origin.toid); break; }
          if (receiverNick.indexOf(login.nick) >= 0) { window._buyerCache.set(ccode, origin.fromid); break; }
        }
      });
    } catch (e) { warn("updateBuyerCacheFromLocal failed", e); }
  }

  function getCacheConv(ccode) {
    try {
      if (!window._buyerCache.has(ccode)) updateBuyerCacheFromLocal();
      return window._buyerCache.get(ccode);
    } catch (e) { return undefined; }
  }

  async function getRemoteMsg(ccode) {
    try {
      if (!window.imsdk || typeof window.imsdk.invoke !== "function") return { ccode: ccode };
      var remoteMsg = await window.imsdk.invoke("im.singlemsg.GetRemoteHisMsg", {
        cid: { ccode: ccode, type: 1 },
        count: 3,
        gohistory: 1,
        msgid: "-1",
        msgtime: "-1"
      });
      var buyer = { ccode: ccode };
      var msgs = remoteMsg && remoteMsg.result ? (remoteMsg.result.msgs || []) : [];
      for (var i = 0; i < msgs.length; i++) {
        if (msgs[i].loginid && msgs[i].fromid && msgs[i].loginid.nick !== msgs[i].fromid.nick) { buyer = msgs[i].fromid; break; }
      }
      return buyer;
    } catch (e) { warn("getRemoteMsg failed", e); return { ccode: ccode }; }
  }

  async function handleNewMsgCid(cid, source) {
    try {
      if (!cid || !cid.ccode) return;
      var conv = getCacheConv(cid.ccode);
      if (conv === undefined) conv = await getRemoteMsg(cid.ccode);
      send("onShopRobotReceriveNewMsgs", JSON.stringify({ loginID: getLoginID(), conversation: conv, source: source || "" }));

      // __qnbotGetNewMsgPatch: actively fetch unread messages and send them to Bot.
      if (window.imsdk && typeof window.imsdk.invoke === "function") {
        try {
          var response = await window.imsdk.invoke("im.singlemsg.GetNewMsg", { ccode: cid.ccode });
          if (response) send("receiveNewMsg", JSON.stringify(response));
        } catch (e) { warn("GetNewMsg failed", e); }
      }
    } catch (e) { warn("handleNewMsgCid failed", e); }
  }

  function installOnEventNotifyHook() {
    if (window.__qnbotOnEventNotifyInstalled) return true;
    if (typeof window.onEventNotify !== "function") return false;
    try {
      var original = window.onEventNotify.bind(window);
      window.___qnww = original;
      window.onEventNotify = function (sid, name, a, data) {
        try { original(sid, name, a, data); } catch (e) { warn("original onEventNotify failed", e); }
        var conv = null;
        try { conv = typeof name === "string" ? JSON.parse(name) : name; } catch (e) {}
        try {
          if (!sid || !conv) return;
          if (sid.indexOf("onConversationChange") >= 0) { updateFromConversation(conv); send("onConversationChange", JSON.stringify({ loginID: getLoginID(), conversation: conv })); }
          else if (sid.indexOf("onConversationAdd") >= 0) { updateFromConversation(conv); send("onConversationAdd", JSON.stringify({ loginID: getLoginID(), conversation: conv })); }
          else if (sid.indexOf("onConversationClose") >= 0) { updateFromConversation(conv); send("onConversationClose", JSON.stringify({ loginID: getLoginID(), conversation: conv })); }
          else if (sid.indexOf("OnChatDlgActive") >= 0) { send("onChatDlgActive", JSON.stringify({ loginID: getLoginID(), conversation: getConversationID() })); }
        } catch (e) { warn("onEventNotify wrapper failed", e); }
      };
      window.__qnbotOnEventNotifyInstalled = true;
      log("onEventNotify hook installed");
      sendStatus("onEventNotify-installed", true);
      return true;
    } catch (e) { warn("installOnEventNotifyHook failed", e); return false; }
  }

  function installQnNotifyHook() {
    if (window.__qnbotQnNotifyInstalled) return true;
    if (!window.QN || typeof window.QN.regEvent !== "function") return false;
    try {
      window.QN.regEvent("bench.msgcenter.newmsgnotify", function (res) { send("messageCenterNotify", res); });
      window.__qnbotQnNotifyInstalled = true;
      log("QN notify hook installed");
      sendStatus("QN-installed", true);
      return true;
    } catch (e) { warn("installQnNotifyHook failed", e); return false; }
  }

  function installImsdkHook() {
    if (window.__qnbotImsdkHookInstalled) return true;
    if (!window.imsdk || typeof window.imsdk.on !== "function") return false;
    try {
      window.imsdk.on(["im.singlemsg.onReceiveNewMsg"], function (cids) {
        try { (cids || []).forEach(function (cid) { handleNewMsgCid(cid, "imsdk.onReceiveNewMsg"); }); } catch (e) { warn("onReceiveNewMsg handler failed", e); }
      });

      if (typeof window.onInvokeNotify === "function") {
        var oldInvokeNotify = window.onInvokeNotify.bind(window);
        window.onInvokeNotifyDelegate = oldInvokeNotify;
        window.onInvokeNotify = function (sid, status, response) {
          try { oldInvokeNotify(sid, status, response); } catch (e) { warn("old onInvokeNotify failed", e); }
          try {
            var task = window.TASK_CACHE && window.TASK_CACHE[sid];
            var cur = getConversationID();
            if (task && task.config && task.config.fn === "im.singlemsg.GetNewMsg") {
              if (!cur || !task.config.param || task.config.param.ccode === cur.ccode) {
                send("receiveNewMsg", typeof response === "string" ? response : JSON.stringify(response));
              }
            }
          } catch (e) { warn("onInvokeNotify wrapper failed", e); }
        };
      }

      window.__qnbotImsdkHookInstalled = true;
      log("imsdk hook installed");
      sendStatus("imsdk-installed", true);
      return true;
    } catch (e) { warn("installImsdkHook failed", e); return false; }
  }

  function sendActiveConversationIfReady() {
    try {
      var login = getLoginID();
      var conv = getConversationID();
      if (!login || !conv) return;
      var text = JSON.stringify({ loginID: login, conversation: conv });
      if (text === lastActiveText) return;
      lastActiveText = text;
      updateFromConversation(conv);
      send("onChatDlgActive", text);
    } catch (e) { warn("sendActiveConversationIfReady failed", e); }
  }

  function loop() {
    forceZhCn();
    installSimplifiedChineseGuard();
    appendOfficialWhenReady();
    setupWebSocket();
    installOnEventNotifyHook();
    installQnNotifyHook();
    installImsdkHook();
    installImsdkInvokeTraceHook();
    runImsdkApiScan(false);
    sendActiveConversationIfReady();
    sendStatus("loop", false);
  }

  forceZhCn();
  installSimplifiedChineseGuard();
  appendOfficialWhenReady();
  setupWebSocket();
  loop();
  setInterval(loop, 1000);
})();

