const state={providers:[],tests:[],clients:[],dashboard:null,currentPage:"dashboard",pollTimer:null};
const $=id=>document.getElementById(id);
const esc=value=>String(value??"").replace(/[&<>"']/g,c=>({"&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"}[c]));
const splitModels=value=>String(value||"").split(/[,，;\n]/).map(x=>x.trim()).filter(Boolean);
const badge=(text,kind="gray")=>`<span class="badge ${kind}">${esc(text)}</span>`;
function toast(message){const el=$("toast");el.textContent=message;el.classList.remove("hidden");setTimeout(()=>el.classList.add("hidden"),3500)}
async function api(path,options={}){
  const init={credentials:"same-origin",headers:{"Content-Type":"application/json",...(options.headers||{})},...options};
  const response=await fetch(path,init);
  if(response.status===401){showLogin();throw new Error("登录已失效");}
  const type=response.headers.get("content-type")||"";
  const data=type.includes("json")?await response.json():await response.text();
  if(!response.ok)throw new Error(data?.detail||data?.error?.message||data||`HTTP ${response.status}`);
  return data;
}
function showLogin(){clearInterval(state.pollTimer);$("appView").classList.add("hidden");$("loginView").classList.remove("hidden")}
function showApp(user){$("loginView").classList.add("hidden");$("appView").classList.remove("hidden");$("adminName").textContent=user;loadAll();state.pollTimer=setInterval(refreshCurrent,8000)}
$("loginForm").addEventListener("submit",async e=>{e.preventDefault();$("loginError").textContent="";try{const r=await api("/api/admin/login",{method:"POST",body:JSON.stringify({username:$("loginUser").value,password:$("loginPassword").value})});showApp(r.username)}catch(err){$("loginError").textContent=err.message}});
$("logoutBtn").onclick=async()=>{try{await api("/api/admin/logout",{method:"POST"})}finally{showLogin()}};
document.querySelectorAll(".nav").forEach(btn=>btn.onclick=()=>switchPage(btn.dataset.page));
const titles={
 dashboard:["运行总览","查看上游状态、模型池和最近调用情况。"],
 providers:["供应商与模型","统一管理上游密钥、主备模型、协议能力和定时深测。"],
 tests:["测试中心","复用 test_api_models_v4 的普通测试与深度测试能力。"],
 clients:["Bot 客户端","为每台 Windows Bot 签发独立访问令牌。"],
 deploy:["部署与安全","查看 Bot 连接地址、HTTPS 和备份建议。"]
};
function switchPage(page){state.currentPage=page;document.querySelectorAll(".page").forEach(x=>x.classList.toggle("active",x.id===`page-${page}`));document.querySelectorAll(".nav").forEach(x=>x.classList.toggle("active",x.dataset.page===page));$("pageTitle").textContent=titles[page][0];$("pageSubtitle").textContent=titles[page][1];refreshCurrent()}
async function loadAll(){await Promise.all([loadDashboard(),loadProviders(),loadTests(),loadClients()]);$("runtimeUrl").textContent=location.origin+"/v1"}
async function refreshCurrent(){try{if(state.currentPage==="dashboard")await loadDashboard();if(state.currentPage==="providers")await loadProviders();if(state.currentPage==="tests")await loadTests();if(state.currentPage==="clients")await loadClients()}catch(err){console.warn(err)}}
function statusKind(text){text=String(text||"");if(text.startsWith("可用"))return"good";if(text.includes("失败")||text.includes("不可用"))return"bad";if(text.includes("测试")||text.includes("运行"))return"warn";return"gray"}
async function loadDashboard(){
  const d=await api("/api/admin/dashboard");state.dashboard=d;
  $("metricCards").innerHTML=[
    ["启用供应商",d.enabled_provider_count,"统一调度的上游站点"],
    ["健康供应商",d.healthy_provider_count,"最近深测确认可用"],
    ["Bot 客户端",d.client_count,"当前启用的令牌"],
    ["最近测试",d.recent_tests.length,"普通、深度和定时任务"]
  ].map(x=>`<div class="metric"><span>${x[0]}</span><strong>${x[1]}</strong><small>${x[2]}</small></div>`).join("");
  $("dashboardProviders").innerHTML=d.providers.length?d.providers.map(providerCard).join(""):`<div class="empty">尚未配置供应商</div>`;
  $("dashboardTests").innerHTML=d.recent_tests.length?`<table><thead><tr><th>供应商</th><th>模式</th><th>状态</th><th>时间</th></tr></thead><tbody>${d.recent_tests.slice(0,8).map(t=>`<tr><td>${esc(t.provider_name)}</td><td>${esc(t.mode)}</td><td>${badge(t.status,statusKind(t.status))}</td><td>${esc(t.finished_at||t.created_at||"")}</td></tr>`).join("")}</tbody></table>`:`<div class="empty">暂无测试记录</div>`;
  $("requestLogs").innerHTML=d.recent_requests.length?`<table><thead><tr><th>时间</th><th>客户端</th><th>类型</th><th>供应商</th><th>模型</th><th>协议</th><th>耗时</th><th>结果</th></tr></thead><tbody>${d.recent_requests.map(r=>`<tr><td>${esc(r.created_at)}</td><td>${esc(r.client_name||"-")}</td><td>${esc(r.request_kind)}</td><td>${esc(r.provider_name||"-")}</td><td>${esc(r.resolved_model||"-")}</td><td>${badge(r.protocol||"-","blue")}</td><td>${r.latency_ms}ms</td><td>${r.success?badge("成功","good"):badge("失败","bad")}</td></tr>`).join("")}</tbody></table>`:`<div class="empty">Bot 尚未通过网关发起调用</div>`;
}
function providerCard(p){return `<div class="provider-card"><div class="provider-top"><strong>${esc(p.name)}</strong>${badge(p.last_status,statusKind(p.last_status))}</div><div class="provider-url">${esc(p.base_url)}</div><div class="provider-meta">${badge("主文本："+(p.main_text_model||"未设置"),"blue")}${p.main_vision_model?badge("主视觉："+p.main_vision_model,"blue"):""}${badge("协议："+(p.protocol_order||[]).join(" → "),"gray")}${p.auto_test_enabled?badge(`每 ${p.auto_test_interval_hours} 小时深测`,"good"):badge("未启用定时深测","gray")}</div></div>`}
async function loadProviders(){
  state.providers=await api("/api/admin/providers");
  $("providersTable").innerHTML=state.providers.length?`<table><thead><tr><th>优先级</th><th>供应商</th><th>主模型</th><th>备用模型</th><th>协议</th><th>定时深测</th><th>状态</th><th>操作</th></tr></thead><tbody>${state.providers.map(p=>`<tr><td>${p.priority}</td><td><strong>${esc(p.name)}</strong><div class="provider-url">${esc(p.base_url)}</div><div>${esc(p.api_key_masked||"")}</div></td><td>${badge(p.main_text_model||"未设置","blue")}${p.main_vision_model?`<br>${badge("视觉 "+p.main_vision_model,"blue")}`:""}</td><td>${(p.backup_text_models||[]).map(x=>badge(x,"gray")).join("")||"-"}</td><td>${(p.protocol_order||[]).map(x=>badge(x,"gray")).join("")}</td><td>${p.auto_test_enabled?badge(`每 ${p.auto_test_interval_hours} 小时`,"good"):badge("关闭","gray")}<div class="hint">${esc(p.next_test_at||"")}</div></td><td>${badge(p.last_status,statusKind(p.last_status))}<div class="hint">${esc(p.last_test_at||"")}</div></td><td><div class="actions"><button onclick="openProviderDialog(${p.id})">编辑</button><button onclick="openTestDialog(${p.id},'ordinary')">普通测试</button><button onclick="openTestDialog(${p.id},'deep')">深度测试</button><button onclick="deleteProvider(${p.id})">删除</button></div></td></tr>`).join("")}</tbody></table>`:`<div class="empty">尚未配置供应商。新增后可立即执行深度测试。</div>`;
}
function openProviderDialog(id){
  const p=id?state.providers.find(x=>x.id===id):null;
  $("providerDialogTitle").textContent=p?"编辑供应商":"新增供应商";$("providerId").value=p?.id||"";
  $("providerName").value=p?.name||"";$("providerPriority").value=p?.priority||100;$("providerBaseUrl").value=p?.base_url||"";$("providerApiKey").value="";
  $("providerMainText").value=p?.main_text_model||"";$("providerBackupText").value=(p?.backup_text_models||[]).join(", ");
  $("providerMainVision").value=p?.main_vision_model||"";$("providerBackupVision").value=(p?.backup_vision_models||[]).join(", ");
  $("providerEnabled").checked=p?.enabled??true;$("providerAutoTest").checked=p?.auto_test_enabled??false;$("providerInterval").value=p?.auto_test_interval_hours||12;
  const protocols=new Set(p?.protocol_order||["responses","chat","legacy"]);document.querySelectorAll('input[name="protocol"]').forEach(x=>x.checked=protocols.has(x.value));
  $("providerDialog").showModal();
}
async function saveProvider(){
  const id=Number($("providerId").value||0);const data={name:$("providerName").value.trim(),priority:Number($("providerPriority").value||100),base_url:$("providerBaseUrl").value.trim(),api_key:$("providerApiKey").value.trim(),main_text_model:$("providerMainText").value.trim(),backup_text_models:splitModels($("providerBackupText").value),main_vision_model:$("providerMainVision").value.trim(),backup_vision_models:splitModels($("providerBackupVision").value),protocol_order:[...document.querySelectorAll('input[name="protocol"]:checked')].map(x=>x.value),enabled:$("providerEnabled").checked,auto_test_enabled:$("providerAutoTest").checked,auto_test_interval_hours:Number($("providerInterval").value||12),auto_test_options:{}};
  try{await api(id?`/api/admin/providers/${id}`:"/api/admin/providers",{method:id?"PUT":"POST",body:JSON.stringify(data)});$("providerDialog").close();toast("供应商已保存");await loadProviders();await loadDashboard()}catch(err){toast(err.message)}
}
async function deleteProvider(id){if(!confirm("删除供应商及其测试记录？"))return;try{await api(`/api/admin/providers/${id}`,{method:"DELETE"});toast("已删除");await loadProviders();await loadDashboard()}catch(err){toast(err.message)}}
function openTestDialog(id,mode="ordinary"){
  const p=state.providers.find(x=>x.id===id);$("testProviderId").value=id;$("testDialogTitle").textContent=`测试：${p?.name||""}`;
  document.querySelector(`input[name="testMode"][value="${mode}"]`).checked=true;$("testModels").value=mode==="ordinary"?(p?.main_text_model||""):"";
  $("testDiscover").checked=mode==="deep";$("testAllModels").checked=mode==="deep";$("testAutoApply").checked=mode==="deep";
  $("testDialog").showModal();
}
async function startTest(){
  const id=Number($("testProviderId").value);const mode=document.querySelector('input[name="testMode"]:checked').value;
  const options={discover_models:$("testDiscover").checked,test_all_discovered_models:$("testAllModels").checked,selected_models:splitModels($("testModels").value),include_v1_root:$("testV1Root").checked,include_root:$("testPlainRoot").checked,responses_text:$("testResponses").checked,chat_text:$("testChat").checked,legacy_text:$("testLegacy").checked,responses_vision:$("testResponsesVision").checked,chat_vision:$("testChatVision").checked,require_vision_for_full:$("testRequireVision").checked,timeout_seconds:Number($("testTimeout").value||45),auto_apply_results:$("testAutoApply").checked};
  try{const r=await api(`/api/admin/providers/${id}/tests`,{method:"POST",body:JSON.stringify({mode,options})});$("testDialog").close();toast(`测试任务 #${r.run_id} 已启动`);switchPage("tests");await loadTests();showTest(r.run_id)}catch(err){toast(err.message)}
}
async function loadTests(){
  state.tests=await api("/api/admin/tests");
  $("testsTable").innerHTML=state.tests.length?`<table><thead><tr><th>ID</th><th>供应商</th><th>模式</th><th>状态</th><th>开始</th><th>结束</th><th>操作</th></tr></thead><tbody>${state.tests.map(t=>`<tr><td>#${t.id}</td><td>${esc(t.provider_name)}</td><td>${badge(t.mode,t.mode==="deep"?"blue":"gray")}</td><td>${badge(t.status,statusKind(t.status))}</td><td>${esc(t.started_at||t.created_at||"")}</td><td>${esc(t.finished_at||"-")}</td><td><div class="actions"><button onclick="showTest(${t.id})">查看</button>${t.status==="completed"?`<button onclick="window.open('/api/admin/tests/${t.id}/report')">报告</button><button onclick="window.open('/api/admin/tests/${t.id}/raw')">原始JSON</button>`:""}</div></td></tr>`).join("")}</tbody></table>`:`<div class="empty">暂无测试记录</div>`;
}
async function showTest(id){
  try{const t=await api(`/api/admin/tests/${id}`);const box=$("testDetail");box.classList.remove("hidden");
    if(!t.result){box.innerHTML=`<div class="panel-head"><div><h2>测试 #${id}</h2><p>${esc(t.provider_name)} · ${esc(t.status)}</p></div></div><div class="empty">${t.status==="failed"?esc(t.error||"测试失败"):"测试正在运行，页面会自动刷新。"}</div>`;return}
    const r=t.result;box.innerHTML=`<div class="panel-head"><div><h2>测试 #${id} 结果</h2><p>${esc(t.provider_name)} · ${esc(r.started_at)} 至 ${esc(r.finished_at)}</p></div><div class="actions"><button onclick="window.open('/api/admin/tests/${id}/report')">下载中文报告</button><button onclick="window.open('/api/admin/tests/${id}/raw')">下载原始JSON</button></div></div>
    <div class="provider-meta">${badge(`发现 ${r.discovery?.models?.length||0} 个模型`,"blue")}${badge(`测试 ${r.model_results?.length||0} 个`,"gray")}${badge(`文本可用 ${(r.model_results||[]).filter(x=>x.text_available).length} 个`,"good")}${badge(`视觉可用 ${(r.model_results||[]).filter(x=>x.vision_available).length} 个`,"good")}</div>
    <div class="test-result-grid">${(r.model_results||[]).map(modelResultCard).join("")}</div>`;
  }catch(err){toast(err.message)}
}
function modelResultCard(m){const attempts=[...(m.all_text_tests||[]),...(m.all_vision_tests||[])];return `<div class="model-result"><h3>${esc(m.model)}</h3><div>${m.text_available?badge("文本可用","good"):badge("文本不可用","bad")}${m.vision_available?badge("视觉可用","good"):badge("视觉未通过","gray")}</div><div class="attempt-list">${attempts.map(a=>`<div class="attempt">${a.success?badge("成功","good"):badge("失败","bad")} ${esc(a.api_type)} · ${esc(a.url)} · ${a.elapsed}s${a.error?`<div class="hint">${esc(a.error)}</div>`:""}</div>`).join("")}</div></div>`}
async function loadClients(){
  state.clients=await api("/api/admin/clients");
  $("clientsTable").innerHTML=state.clients.length?`<table><thead><tr><th>名称</th><th>令牌前缀</th><th>状态</th><th>创建时间</th><th>最近使用</th><th>操作</th></tr></thead><tbody>${state.clients.map(c=>`<tr><td>${esc(c.name)}</td><td><code>${esc(c.token_prefix)}...</code></td><td>${c.enabled?badge("启用","good"):badge("停用","bad")}</td><td>${esc(c.created_at)}</td><td>${esc(c.last_used_at||"-")}</td><td><div class="actions"><button onclick="toggleClient(${c.id})">${c.enabled?"停用":"启用"}</button><button onclick="deleteClient(${c.id})">删除</button></div></td></tr>`).join("")}</tbody></table>`:`<div class="empty">尚未创建 Bot 客户端令牌</div>`;
}
async function createClient(){const name=prompt("客户端名称，例如：客服电脑-01");if(!name)return;try{const r=await api("/api/admin/clients",{method:"POST",body:JSON.stringify({name})});await navigator.clipboard?.writeText(r.token);alert(`客户端令牌只显示一次：\n\n${r.token}\n\n已尝试复制到剪贴板，请立即填入 Bot。`);await loadClients();await loadDashboard()}catch(err){toast(err.message)}}
async function toggleClient(id){try{await api(`/api/admin/clients/${id}/toggle`,{method:"POST"});await loadClients()}catch(err){toast(err.message)}}
async function deleteClient(id){if(!confirm("删除此客户端令牌？该 Bot 将立即无法调用。"))return;try{await api(`/api/admin/clients/${id}`,{method:"DELETE"});await loadClients();await loadDashboard()}catch(err){toast(err.message)}}
(async()=>{try{const me=await api("/api/admin/me");showApp(me.username)}catch{showLogin()}})();
