const versionUpdateState={loading:false,last:null};
titles["version-update"]=["版本更新","同步 GitHub 版本、在线更新服务端，并缓存客户端正式版后推送更新通知。"];

function vuBytes(value){
  const n=Number(value||0);if(!n)return "0 B";
  const units=["B","KB","MB","GB"];let x=n,i=0;while(x>=1024&&i<units.length-1){x/=1024;i++}
  return `${x.toFixed(i?1:0)} ${units[i]}`;
}
function vuPercent(value){return Math.max(0,Math.min(100,Number(value||0)));}
function vuProgress(value){const p=vuPercent(value);return `<div style="min-width:220px"><progress max="100" value="${p}" style="width:100%;height:18px"></progress><div class="hint">${p.toFixed(0)}%</div></div>`;}
function vuStateBadge(stateText){const s=String(stateText||"");if(s==="completed"||s==="ready")return badge("已完成","good");if(s==="failed")return badge("失败","bad");if(["running","queued","downloading","verifying","connecting","resuming","retrying"].includes(s))return badge(s==="retrying"?"网络重试中":"进行中","warn");return badge(s||"等待","gray");}
function vuEta(seconds){const n=Number(seconds);if(!Number.isFinite(n)||n<0)return "--";if(n<60)return `${Math.ceil(n)} 秒`;if(n<3600)return `${Math.ceil(n/60)} 分钟`;return `${(n/3600).toFixed(1)} 小时`;}

async function loadVersionUpdate(){
  if(versionUpdateState.loading)return;
  versionUpdateState.loading=true;
  try{const data=await api("/api/admin/version-update/status");versionUpdateState.last=data;renderVersionUpdate(data)}
  catch(err){if($("versionUpdateStatus"))$("versionUpdateStatus").innerHTML=`<div class="empty">${esc(err.message)}</div>`}
  finally{versionUpdateState.loading=false}
}

function renderVersionUpdate(data){
  const server=data.server||{}, update=server.update||{}, gh=server.github||{}, agent=update.agent||{}, serverNet=server.network||{};
  const client=data.client||{}, pkg=client.package||{}, push=client.push||{}, clientNet=client.network||{};
  const serverCurrent=server.current_short_sha||"未知";
  const serverLatest=gh.short_sha||"同步失败";
  const serverStatus=server.sync_error?badge("GitHub 同步失败","bad"):(!server.current_commit?badge("等待识别当前版本","gray"):(server.update_available?badge("发现新版本","warn"):badge("已是最新","good")));
  const agentHint=agent.online?"":`<p class="hint">首次启用服务端网页更新需要在 Ubuntu 执行一次：<code>sudo bash /opt/qianniu-ai-bot/scripts/install-api-control-plane-update-agent.sh</code></p>`;
  const gitTransport=(agent.git_transport||serverNet.git_transport||"ssh-443")==="ssh-443"?"Git SSH 443":esc(agent.git_transport||serverNet.git_transport||"SSH");
  $("serverVersionCard").innerHTML=`
    <div class="provider-card">
      <div class="provider-top"><strong>API 控制面</strong>${serverStatus}</div>
      <div class="form-grid" style="margin-top:12px">
        <label>当前服务端提交<div class="code-card"><code>${esc(serverCurrent)}</code></div></label>
        <label>GitHub master<div class="code-card"><code>${esc(serverLatest)}</code></div></label>
      </div>
      <p class="hint">${esc(gh.message||server.sync_error||"")}${gh.committed_at?` · ${esc(cnTime(gh.committed_at))}`:""}</p>
      <div class="provider-meta">${agent.online?badge("主机更新代理在线","good"):badge("主机更新代理离线","bad")}${badge(gitTransport,"blue")}${badge(`源码失败最多重试 ${Number(agent.git_fetch_attempts||serverNet.git_fetch_attempts||5)} 次`,"gray")}${vuStateBadge(update.state)}</div>
      ${agentHint}
      <div style="margin-top:12px">${vuProgress(update.progress_percent)}</div>
      <p><strong>${esc(update.phase||"等待更新")}</strong> · ${esc(update.message||"")}</p>
      <p class="hint">服务端源码默认通过 GitHub 官方 <code>ssh.github.com:443</code> 拉取，启用 SSH keepalive、连接超时和指数退避重试；GitHub 网络失败不会提前停止旧服务。</p>
      <div class="actions"><button class="primary" onclick="startServerVersionUpdate()" ${agent.online?"":"disabled"}>更新服务端到 GitHub 最新版</button></div>
    </div>`;

  const pkgPhase=pkg.ready?"ready":(pkg.error?"failed":(pkg.phase||(pkg.downloading?"downloading":"waiting")));
  const clientProgress=pkg.ready?100:vuPercent(pkg.progress_percent);
  const downloadText=pkg.ready?`服务端已完整缓存并校验 ${vuBytes(pkg.downloaded_bytes||client.size)}`:`${vuBytes(pkg.downloaded_bytes)} / ${vuBytes(pkg.total_bytes||client.size)}`;
  const speed=Number(pkg.speed_bps||0)>0?`${vuBytes(pkg.speed_bps)}/s`:"--";
  const retryText=Number(pkg.max_attempts||clientNet.max_attempts||0)>0?`第 ${Number(pkg.attempt||0)}/${Number(pkg.max_attempts||clientNet.max_attempts)} 次`:"";
  const retryWait=pkgPhase==="retrying"&&Number(pkg.retry_in_seconds||0)>0?` · ${Number(pkg.retry_in_seconds)} 秒后续传`:"";
  const proxyBadge=(pkg.proxy_enabled||clientNet.proxy_enabled)?badge("HTTPS 代理已启用","blue"):badge("HTTPS 直连","gray");
  $("clientVersionCard").innerHTML=`
    <div class="provider-card">
      <div class="provider-top"><strong>Windows 客户端正式版</strong>${client.sync_error?badge("同步失败","bad"):badge(client.version||"未知","blue")}</div>
      <div class="form-grid" style="margin-top:12px">
        <label>正式版版本<div class="code-card"><code>${esc(client.version||"-")}</code></div></label>
        <label>Release Tag<div class="code-card"><code>${esc(client.tag||"-")}</code></div></label>
        <label>安装包大小<div class="code-card"><code>${esc(vuBytes(client.size))}</code></div></label>
        <label>构建提交<div class="code-card"><code>${esc(String(client.commit||"").slice(0,12)||"-")}</code></div></label>
      </div>
      <p class="hint">发布时间：${esc(cnTime(client.published_at,"-"))}　SHA-256：${esc(client.sha256||"-")}</p>
      <div class="provider-meta">${vuStateBadge(pkgPhase)}${badge("HTTP Range 断点续传","good")}${proxyBadge}${badge(`SSE 在线连接 ${Number(push.active_streams||0)}`,push.active_streams?"good":"gray")}${push.last_push_version?badge(`最近推送 ${push.last_push_version}`,"blue"):badge("尚无推送记录","gray")}</div>
      <div style="margin-top:12px">${vuProgress(clientProgress)}</div>
      <p><strong>${pkg.ready?"安装包已就绪":pkgPhase==="failed"?"安装包准备失败":pkgPhase==="retrying"?"GitHub 网络异常，自动续传重试":pkgPhase==="verifying"?"正在校验安装包":pkgPhase==="resuming"?"正在从断点恢复":"服务端正在准备安装包"}</strong> · ${esc(downloadText)}${pkg.error?` · ${esc(pkg.error)}`:""}</p>
      <p class="hint">速度：${esc(speed)}　预计剩余：${esc(vuEta(pkg.eta_seconds))}　${esc(retryText)}${esc(retryWait)}${pkg.last_error&&!pkg.error?`　最近网络错误：${esc(pkg.last_error)}`:""}</p>
      <p class="hint">网络中断时保留 <code>.partial</code>，下一次使用 HTTP Range 从已下载位置继续；默认最多自动重试 ${Number(pkg.max_attempts||clientNet.max_attempts||8)} 次并指数退避。完整下载后才执行 SHA-256/大小校验并通过 SSE 推送，客户端不会直连 GitHub。</p>
      <p class="hint">如服务器到 GitHub HTTPS 长期不稳定，可在控制面 <code>.env</code> 设置 <code>BOT_UPDATE_GITHUB_PROXY=socks5h://127.0.0.1:1080</code>（或 HTTP 代理）后重启控制面；页面会显示“HTTPS 代理已启用”。</p>
      <div class="actions"><button class="primary" onclick="startClientReleaseUpdate()">同步并缓存 GitHub 最新正式版</button></div>
    </div>`;
}

async function syncVersionUpdate(){
  try{$("syncVersionBtn").disabled=true;toast("正在同步 GitHub 版本状态…");const data=await api("/api/admin/version-update/sync",{method:"POST"});versionUpdateState.last=data;renderVersionUpdate(data);toast("GitHub 版本状态已同步")}
  catch(err){toast(err.message)}finally{$("syncVersionBtn").disabled=false}
}
async function startServerVersionUpdate(){
  if(!confirm("确认将 API 控制面自动更新到 GitHub master 最新版本？源码将通过 Git SSH 443 自动重试，随后先构建新镜像、备份数据，再切换服务；失败会按现有部署脚本回滚。"))return;
  try{const r=await api("/api/admin/version-update/server/start",{method:"POST"});toast(r.already_latest?"服务端已经是 GitHub 最新版":"服务端更新任务已启动");await loadVersionUpdate()}catch(err){toast(err.message)}
}
async function startClientReleaseUpdate(){
  try{const r=await api("/api/admin/version-update/client/start",{method:"POST"});toast(r.ready?"客户端正式版已缓存，更新通知将由服务端推送":"服务端已开始从 GitHub 断点续传客户端正式版");await loadVersionUpdate()}catch(err){toast(err.message)}
}

const originalRefreshCurrent=refreshCurrent;
refreshCurrent=async function(){if(state.currentPage==="version-update")return loadVersionUpdate();return originalRefreshCurrent();};
setInterval(()=>{if(state.currentPage==="version-update")loadVersionUpdate()},1000);
