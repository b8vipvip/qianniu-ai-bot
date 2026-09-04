(() => {
  const IDS = {
    enabled: "autoReplyRulesEnabled",
    manual: "manualHandoffKeywords",
    confirm: "manualConfirmKeywords",
    workEnabled: "workHoursEnabled",
    start: "workStartTime",
    end: "workEndTime",
    mode: "offHoursReplyMode",
    fixed: "offHoursFixedText",
    save: "saveAutoReplyRulesBtn",
    hint: "autoReplyRulesHint"
  };
  let rulesState = null;
  let rulesTimer = null;
  let loading = false;

  function addStyles() {
    if (document.getElementById("botWebAutoReplyRulesStyle")) return;
    const style = document.createElement("style");
    style.id = "botWebAutoReplyRulesStyle";
    style.textContent = `
      .rule-field{display:block;margin:14px 0}.rule-field>span{display:block;font-weight:600;margin-bottom:6px}.rule-field small{display:block;color:#667085;margin-top:5px;line-height:1.5}.rule-field textarea,.rule-field input[type=time],.rule-field select{width:100%;box-sizing:border-box;border:1px solid #d0d5dd;border-radius:9px;background:#fff;padding:10px;font:inherit}.rule-field textarea{min-height:76px;resize:vertical}.rule-times{display:grid;grid-template-columns:1fr 1fr;gap:10px}.rule-subtitle{margin:18px 0 4px;font-size:14px;color:#344054}.rule-sync-meta{margin-top:10px;color:#667085;font-size:13px;line-height:1.5}
      @media(max-width:520px){.rule-times{grid-template-columns:1fr}}
    `;
    document.head.appendChild(style);
  }

  function ensurePanel() {
    if ($("autoReplyRulesPanel")) return;
    const page = $("page-settings");
    if (!page) return;
    addStyles();
    const panel = document.createElement("section");
    panel.id = "autoReplyRulesPanel";
    panel.className = "panel settings-panel";
    panel.innerHTML = `
      <h3>自动回复规则</h3>
      <p class="rule-sync-meta">仅同步非敏感规则字段。Webhook、邮箱密码、下单接口 Token 等仍只保存在 Windows 本机。</p>
      <label class="switch-row"><span><strong>启用转人工规则</strong><small>命中下方关键词时按 Windows 现有规则阻止自动答复或转人工。</small></span><input id="${IDS.enabled}" type="checkbox"></label>
      <label class="rule-field"><span>强制转人工关键词</span><textarea id="${IDS.manual}" maxlength="2000" placeholder="退款,投诉,差评"></textarea><small>逗号或现有 Windows 支持的分隔方式保持不变。</small></label>
      <label class="rule-field"><span>仅人工确认关键词</span><textarea id="${IDS.confirm}" maxlength="2000" placeholder="手机号,地址,验证码"></textarea><small>涉及隐私、支付或需要人工确认的关键词。</small></label>
      <h4 class="rule-subtitle">人工客服工作时间与下班回复</h4>
      <label class="switch-row"><span><strong>启用工作时间判断</strong><small>支持跨夜，例如 18:00–09:00。</small></span><input id="${IDS.workEnabled}" type="checkbox"></label>
      <div class="rule-times">
        <label class="rule-field"><span>上班时间</span><input id="${IDS.start}" type="time" step="60"></label>
        <label class="rule-field"><span>下班时间</span><input id="${IDS.end}" type="time" step="60"></label>
      </div>
      <label class="rule-field"><span>下班回复方式</span><select id="${IDS.mode}"><option value="AI告知下班时间">AI 告知下班时间</option><option value="固定预设答案">固定预设答案</option></select></label>
      <label id="offHoursFixedTextRow" class="rule-field"><span>下班固定回复</span><textarea id="${IDS.fixed}" maxlength="3000" placeholder="请输入下班后的固定回复"></textarea><small>仅在选择“固定预设答案”时使用。</small></label>
      <button id="${IDS.save}" class="primary wide" type="button">保存并下发自动回复规则</button>
      <div id="${IDS.hint}" class="hint"></div>
    `;
    const info = $("clientInfo");
    const infoPanel = info && info.closest ? info.closest("section.panel") : null;
    if (infoPanel && infoPanel.parentNode === page) page.insertBefore(panel, infoPanel);
    else page.appendChild(panel);
    $(IDS.mode).addEventListener("change", renderMode);
    $(IDS.save).addEventListener("click", saveRules);
  }

  function renderMode() {
    const row = $("offHoursFixedTextRow");
    if (!row || !$(IDS.mode)) return;
    row.classList.toggle("hidden", $(IDS.mode).value !== "固定预设答案");
  }

  function setForm(desired) {
    if (!desired) return;
    $(IDS.enabled).checked = desired.auto_reply_rules_enabled !== false;
    $(IDS.manual).value = desired.manual_handoff_keywords || "";
    $(IDS.confirm).value = desired.manual_confirm_keywords || "";
    $(IDS.workEnabled).checked = desired.work_hours_enabled !== false;
    $(IDS.start).value = desired.work_start_time || "09:00";
    $(IDS.end).value = desired.work_end_time || "18:00";
    $(IDS.mode).value = desired.off_hours_reply_mode === "固定预设答案" ? "固定预设答案" : "AI告知下班时间";
    $(IDS.fixed).value = desired.off_hours_fixed_text || "";
    renderMode();
  }

  function renderState() {
    ensurePanel();
    if (!rulesState || !$(IDS.hint)) return;
    const save = $(IDS.save);
    if (!rulesState.initialized) {
      save.disabled = true;
      $(IDS.hint).innerHTML = `${badge("等待首次同步","warn")}<span style="margin-left:8px">先等待 Windows Bot 上报当前本地规则；升级不会用服务端默认值覆盖现有规则。</span>`;
      return;
    }
    save.disabled = false;
    const revision = Number(rulesState.revision || 0);
    const applied = Number(rulesState.applied_revision || 0);
    const error = String(rulesState.last_error || "").trim();
    let label = "应用成功", kind = "good", detail = `Windows Bot 已确认规则版本 ${applied}`;
    if (error && applied < revision) {
      label = "应用失败"; kind = "bad"; detail = `${error} · 目标版本 ${revision} · 已应用 ${applied}`;
    } else if (!rulesState.online && applied < revision) {
      label = "已保存 · 等待上线"; kind = "warn"; detail = `规则版本 ${revision} 已保存在服务端，Windows Bot 上线后自动下发`;
    } else if (applied < revision) {
      label = "等待 Windows 确认"; kind = "warn"; detail = `规则版本 ${revision} 已下发，Windows 当前已确认 ${applied}`;
    }
    $(IDS.hint).innerHTML = `${badge(label,kind)}<span style="margin-left:8px">${esc(detail)}</span>`;
  }

  async function refreshRules(showError = false) {
    if (loading) return;
    loading = true;
    try {
      const next = await api("/api/bot-web/auto-reply-rules");
      const first = !rulesState || Number(next.revision || 0) !== Number(rulesState.revision || 0);
      rulesState = next;
      ensurePanel();
      if (first || document.activeElement === document.body) setForm(next.desired || {});
      renderState();
    } catch (err) {
      if (showError && err.message !== "登录已失效") toast(err.message);
    } finally {
      loading = false;
    }
  }

  async function saveRules() {
    if (!rulesState || !rulesState.initialized) {
      toast("请先等待 Windows Bot 完成首次规则同步");
      return;
    }
    const data = {
      auto_reply_rules_enabled: $(IDS.enabled).checked,
      manual_handoff_keywords: $(IDS.manual).value.trim(),
      manual_confirm_keywords: $(IDS.confirm).value.trim(),
      work_hours_enabled: $(IDS.workEnabled).checked,
      work_start_time: $(IDS.start).value || "09:00",
      work_end_time: $(IDS.end).value || "18:00",
      off_hours_reply_mode: $(IDS.mode).value,
      off_hours_fixed_text: $(IDS.fixed).value.trim()
    };
    if (data.off_hours_reply_mode === "固定预设答案" && !data.off_hours_fixed_text) {
      toast("固定预设答案不能为空");
      return;
    }
    const button = $(IDS.save);
    button.disabled = true;
    try {
      rulesState = await api("/api/bot-web/auto-reply-rules", { method: "PUT", body: JSON.stringify(data) });
      setForm(rulesState.desired || data);
      renderState();
      toast("自动回复规则已保存，等待 Windows Bot 确认应用");
    } catch (err) {
      toast(err.message);
    } finally {
      button.disabled = false;
    }
  }

  function stopTimer() {
    if (rulesTimer) clearInterval(rulesTimer);
    rulesTimer = null;
  }

  function startTimer() {
    stopTimer();
    ensurePanel();
    refreshRules(false);
    rulesTimer = setInterval(() => {
      if (!$("appView").classList.contains("hidden")) refreshRules(false);
    }, 2500);
  }

  const baseShowLogin = showLogin;
  showLogin = function () {
    stopTimer();
    rulesState = null;
    baseShowLogin();
  };

  const baseShowApp = showApp;
  showApp = function (name) {
    baseShowApp(name);
    startTimer();
  };

  const baseRenderSettings = renderSettings;
  renderSettings = function () {
    baseRenderSettings();
    ensurePanel();
    renderState();
  };

  ensurePanel();
  if (!$("appView").classList.contains("hidden")) startTimer();
})();
