async function copyTextCompat(value){
  try{await navigator.clipboard.writeText(value);return true}catch{}
  const box=document.createElement("textarea");box.value=value;box.style.position="fixed";box.style.opacity="0";document.body.appendChild(box);box.select();let ok=false;try{ok=document.execCommand("copy")}catch{}box.remove();return ok;
}
async function loadClients(){
  state.clients=await api("/api/admin/mobile-bot/clients");
  $("clientsTable").innerHTML=state.clients.length?`<table><thead><tr><th>名称</th><th>令牌</th><th>Bot 状态</th><th>版本/客服账号</th><th>创建时间</th><th>最近使用</th><th>操作</th></tr></thead><tbody>${state.clients.map(c=>`<tr><td><strong>${esc(c.name)}</strong></td><td><code>${esc(c.token_prefix)}...</code><div class="hint">${c.token_available?"可复制完整令牌":"等待新版 Bot 同步留存"}</div></td><td>${c.online?badge("在线","good"):(c.enabled?badge("离线","gray"):badge("停用","bad"))}</td><td>${esc(c.app_version||"-")}<div class="hint">${esc((c.seller_nicks||[]).join("、")||"-")}</div></td><td>${esc(c.created_at)}</td><td>${esc(c.last_used_at||"-")}</td><td><div class="actions">${c.token_available?`<button onclick="copyClientToken(${c.id})">复制令牌</button>`:`<button onclick="rotateClientToken(${c.id})">重新生成</button>`}<button onclick="window.open('/bot/','_blank')">打开 Web 端</button><button onclick="toggleClient(${c.id})">${c.enabled?"停用":"启用"}</button><button onclick="deleteClient(${c.id})">删除</button></div></td></tr>`).join("")}</tbody></table>`:`<div class="empty">尚未创建 Bot 客户端令牌</div>`;
}
async function createClient(){
  const name=prompt("客户端名称，例如：客服电脑-01");if(!name)return;
  try{const r=await api("/api/admin/mobile-bot/clients",{method:"POST",body:JSON.stringify({name})});const copied=await copyTextCompat(r.token);alert(`客户端令牌：\n\n${r.token}\n\n${copied?"已复制到剪贴板。":"请手动复制并填入 Windows Bot。"}`);await loadClients();await loadDashboard()}catch(err){toast(err.message)}
}
async function copyClientToken(id){
  try{const r=await api(`/api/admin/mobile-bot/clients/${id}/token`);const copied=await copyTextCompat(r.token);toast(copied?"完整客户端令牌已复制":"复制失败，请检查浏览器剪贴板权限")}catch(err){toast(err.message)}
}
async function rotateClientToken(id){
  if(!confirm("重新生成后，旧令牌会立即失效，Windows Bot 必须填写新令牌才能重新连接。确认继续？"))return;
  try{const r=await api(`/api/admin/mobile-bot/clients/${id}/rotate`,{method:"POST"});const copied=await copyTextCompat(r.token);alert(`新客户端令牌：\n\n${r.token}\n\n${copied?"已复制到剪贴板。":"请立即手动保存。"}`);await loadClients();await loadDashboard()}catch(err){toast(err.message)}
}
