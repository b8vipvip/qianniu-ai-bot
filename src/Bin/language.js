window.__qnbotLanguageVersion = "20260713-hans-all-pages-v3";
window.__qnbotLanguagePatch = "20260713-hans-all-pages-v3";

(function () {
  if (window.__qnbotHansPatchInstalled) return;
  window.__qnbotHansPatchInstalled = true;

  var REPORT_PREFIX = "__qnbotLanguageReport:";
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
  var observer = null;
  var stats = window.__qnbotHansStats = {
    convertedTextNodes: 0,
    convertedAttributes: 0,
    shadowRoots: 0,
    frameDocuments: 0,
    lastRunAt: 0
  };

  function warn() {
    try { console.warn.apply(console, ["[qnbot-language]"].concat(Array.prototype.slice.call(arguments))); } catch (e) {}
  }

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
      if (frameDocument) observeRoot(frameDocument.documentElement || frameDocument);
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
        acceptNode: function (node) { return shouldSkip(node.parentElement) ? 2 : 1; }
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

  function report() {
    try {
      var reportKey = REPORT_PREFIX + (location.pathname || "/");
      localStorage.setItem(reportKey, JSON.stringify({
        version: window.__qnbotLanguageVersion,
        path: location.pathname || "",
        title: document.title || "",
        convertedTextNodes: stats.convertedTextNodes,
        convertedAttributes: stats.convertedAttributes,
        shadowRoots: stats.shadowRoots,
        frameDocuments: stats.frameDocuments,
        lastSeenAt: Date.now()
      }));
    } catch (e) {}
  }

  function run() {
    forceZhCn();
    stats.lastRunAt = Date.now();
    scanRoot(document.body || document.documentElement);
    report();
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

  forceZhCn();
  observeRoot(document.documentElement || document);
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", run);
  else run();
  setTimeout(run, 500);
  setTimeout(run, 1500);
  setTimeout(run, 3500);
  setInterval(run, 3000);
})();
