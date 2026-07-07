window.__qnbotInjectVersion = "20260707-zh-cn-v2";
window.__qnbotRuntimePatch = "20260707-safe-hooks-v5";

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

  function statusObject(extra) {
    var login = getLoginID();
    var conv = getConversationID();
    var obj = {
      patch: "__qnbotStatusPatch",
      runtime: window.__qnbotRuntimePatch,
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
    appendOfficialWhenReady();
    setupWebSocket();
    installOnEventNotifyHook();
    installQnNotifyHook();
    installImsdkHook();
    sendActiveConversationIfReady();
    sendStatus("loop", false);
  }

  forceZhCn();
  appendOfficialWhenReady();
  setupWebSocket();
  loop();
  setInterval(loop, 1000);
})();
