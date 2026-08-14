/*
 * Passive Qianniu IMSDK direct-send discovery v2.
 *
 * Safety rule: this probe NEVER invokes a discovered API. It only enumerates object
 * properties/functions and ranks names that look related to chat/message sending.
 * Paste/run it inside a Qianniu chat renderer that already exposes QN/_vs/imsdk.
 */
(function () {
  'use strict';

  var VERSION = '20260814-imsdk-send-discovery-v2';
  var MAX_DEPTH = 4;
  var MAX_ROWS = 1200;
  var MAX_PREVIEW = 220;
  var KEYWORDS = [
    ['sendmsg', 120], ['sendmessage', 120], ['send_msg', 115], ['sendmessage', 115],
    ['send', 80], ['reply', 65], ['message', 55], ['msg', 45], ['chat', 45],
    ['wangwang', 40], ['singlemsg', 40], ['publish', 35], ['post', 25],
    ['smarttip', 20], ['im', 10]
  ];

  function safeType(value) {
    try { return value === null ? 'null' : typeof value; } catch (_) { return 'unknown'; }
  }

  function safeOwnNames(value) {
    try { return Object.getOwnPropertyNames(value || {}); } catch (_) { return []; }
  }

  function safeGet(value, key) {
    try { return { ok: true, value: value[key] }; }
    catch (error) { return { ok: false, error: String(error && error.message || error) }; }
  }

  function sourcePreview(fn) {
    try {
      var text = Function.prototype.toString.call(fn).replace(/\s+/g, ' ').trim();
      return text.slice(0, MAX_PREVIEW);
    } catch (_) { return ''; }
  }

  function scoreName(path) {
    var lower = String(path || '').toLowerCase();
    var score = 0;
    var hits = [];
    KEYWORDS.forEach(function (entry) {
      if (lower.indexOf(entry[0]) >= 0) {
        score += entry[1];
        hits.push(entry[0]);
      }
    });
    if (/\.send(msg|message)?$/i.test(path)) score += 140;
    if (/wangwang.*send|send.*wangwang/i.test(path)) score += 80;
    if (/singlemsg.*send|send.*singlemsg/i.test(path)) score += 80;
    return { score: score, hits: hits };
  }

  function shouldDescend(path, value, depth) {
    if (depth >= MAX_DEPTH || value === null) return false;
    var type = safeType(value);
    if (type !== 'object' && type !== 'function') return false;
    if (path === 'window' || path === 'document') return false;
    return true;
  }

  function walk(rootName, rootValue, rows, visited) {
    var queue = [{ path: rootName, value: rootValue, depth: 0 }];
    while (queue.length && rows.length < MAX_ROWS) {
      var item = queue.shift();
      var current = item.value;
      if (current === null) continue;

      var type = safeType(current);
      if (type === 'object' || type === 'function') {
        try {
          if (visited.indexOf(current) >= 0) continue;
          visited.push(current);
        } catch (_) {}
      }

      safeOwnNames(current).forEach(function (name) {
        if (rows.length >= MAX_ROWS) return;
        if (name === 'caller' || name === 'callee' || name === 'arguments') return;
        var childPath = item.path + '.' + name;
        var got = safeGet(current, name);
        if (!got.ok) {
          rows.push({ path: childPath, kind: 'getter-error', score: 0, hits: [], error: got.error });
          return;
        }

        var child = got.value;
        var childType = safeType(child);
        var ranked = scoreName(childPath);
        if (childType === 'function' || ranked.score > 0) {
          rows.push({
            path: childPath,
            kind: childType,
            score: ranked.score,
            hits: ranked.hits,
            arity: childType === 'function' ? Number(child.length || 0) : null,
            sourcePreview: childType === 'function' ? sourcePreview(child) : ''
          });
        }

        if (shouldDescend(childPath, child, item.depth)) {
          queue.push({ path: childPath, value: child, depth: item.depth + 1 });
        }
      });
    }
  }

  function resolveRoot(path) {
    var parts = path.split('.');
    var value = window;
    for (var i = 1; i < parts.length; i++) {
      var got = safeGet(value, parts[i]);
      if (!got.ok) return undefined;
      value = got.value;
      if (value === undefined || value === null) break;
    }
    return value;
  }

  function emit(payload) {
    try {
      if (window.chatWebsocket && window.chatWebsocket.readyState === 1) {
        // Reuse the production injection channel that MyWebSocketServer already records with
        // full response payloads. scanKind differentiates v2 from the legacy shallow scan.
        window.chatWebsocket.send(JSON.stringify({
          type: 'imsdkApiScan',
          payload: { scanKind: 'directSendV2', result: payload }
        }));
      }
    } catch (_) {}
    try { console.log('[qnbot][imsdk-send-discovery-v2]', payload); } catch (_) {}
  }

  function run() {
    var rootNames = [
      'window.QN.wangwang',
      'window.QN.intelligentservice',
      'window.QN.component',
      'window.QN.app',
      'window.QN.application',
      'window.QN.gateway',
      'window.QN',
      'window._vs.SDK',
      'window._vs',
      'window.imsdk'
    ];
    var rows = [];
    var visited = [];
    var availableRoots = [];

    rootNames.forEach(function (rootName) {
      var value = resolveRoot(rootName);
      if (value === undefined || value === null) return;
      availableRoots.push(rootName);
      walk(rootName, value, rows, visited);
    });

    rows.sort(function (a, b) {
      if (b.score !== a.score) return b.score - a.score;
      return String(a.path).localeCompare(String(b.path));
    });

    var candidates = rows.filter(function (row) { return row.kind === 'function' && row.score > 0; });
    var payload = {
      version: VERSION,
      scanKind: 'directSendV2',
      passiveOnly: true,
      candidateInvocationDisabled: true,
      href: String(location.href || ''),
      title: String(document.title || ''),
      availableRoots: availableRoots,
      candidateCount: candidates.length,
      candidates: candidates.slice(0, 200),
      rows: rows.slice(0, MAX_ROWS)
    };

    window.__qnbotImsdkSendDiscoveryV2 = payload;
    emit(payload);
    return payload;
  }

  window.__qnbotRunImsdkSendDiscoveryV2 = run;
  window.__qnbotImsdkSendDiscoveryV2Version = VERSION;
  run();
})();
