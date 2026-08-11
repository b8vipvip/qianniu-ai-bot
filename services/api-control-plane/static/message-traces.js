titles["message-traces"]=["消息处理日志","按 ShopKey 跟踪买家消息从识别、答案生成到真实发送的完整链路。"];

const previousRefreshCurrent=refreshCurrent;
refreshCurrent=async function(){
  if(state.currentPage==="message-traces"){
    try{await loadMessageTraces()}catch(err){console.warn(err)}
    return;
  }
  return previousRefreshCurrent();
};

function traceStatusKind(value){
  value=String(value||"").toLowerCase();
  if(value==="success"||value==="ready")return "good";
  if(value==="failed")return "bad";
  if(value==="cancelled")return "warn";
  if(value==="processing")return "blue";
  return "gray";
}

function traceStageName(value){
  const map={
    message_received:"收到买家消息",
    answer_generation_started:"开始获取答案",
    answer_ready:"答案生成完成",
    delivery_confirmed:"发送确认",
    delivery_failed:"发送失败",
    manual_intervention:"人工介入",
    processing_failed:"处理失败"
  };
  return map[value]||value||"-";
}

function traceQuery(){
  const query=new URLSearchParams();
  const fields=[
    ["client_id","traceClientId"],
    ["shop_key","traceShopKey"],
    ["seller","traceSeller"],
    ["buyer","traceBuyer"],
    ["status","traceStatus"],
    ["trace_id","traceId"]
  ];
  fields.forEach(([key,id])=>{
    const el=$(id);const value=el?String(el.value||"").trim():"";
    if(value)query.set(key,value);
  });
  query.set("limit","500");
  return query.toString();
}

async function loadMessageTraces(){
  const box=$("messageTraceTable");
  if(!box)return;
  const rows=await api("/api/admin/message-processing-traces?"+traceQuery());
  state.messageTraces=rows;
  if(!rows.length){box.innerHTML=`<div class="empty">暂无符合条件的消息处理日志。新版 Windows Bot 在线后会自动上报。</div>`;return;}
  box.innerHTML=`<table><thead><tr><th>时间（北京时间）</th><th>客户端 / 店铺</th><th>客服 → 买家</th><th>链路ID</th><th>阶段</th><th>状态</th><th>耗时</th><th>摘要 / 详情</th></tr></thead><tbody>${rows.map(r=>`<tr>
    <td>${esc(cnTime(r.occurred_at||r.created_at,""))}</td>
    <td><strong>${esc(r.client_name||("#"+r.client_id))}</strong><div class="hint">${esc(r.shop_key||"-")}</div></td>
    <td>${esc(r.seller||"-")}<div class="hint">→ ${esc(r.buyer||"-")}</div></td>
    <td><button class="ghost" data-trace-id="${esc(r.trace_id||"")}" onclick="filterTrace(this.dataset.traceId)">${esc(String(r.trace_id||"").slice(0,10)||"-")}</button></td>
    <td>${esc(traceStageName(r.stage))}</td>
    <td>${badge(r.status||"-",traceStatusKind(r.status))}</td>
    <td>${Number(r.duration_ms||0)>0?esc(r.duration_ms+"ms"):"-"}</td>
    <td><strong>${esc(r.summary||"-")}</strong>${r.detail?`<div class="hint">${esc(r.detail)}</div>`:""}</td>
  </tr>`).join("")}</tbody></table>`;
}

function filterTrace(traceId){
  const input=$("traceId");
  if(input)input.value=traceId||"";
  loadMessageTraces().catch(err=>toast(err.message));
}

function clearTraceFilters(){
  ["traceClientId","traceShopKey","traceSeller","traceBuyer","traceId"].forEach(id=>{const el=$(id);if(el)el.value=""});
  const status=$("traceStatus");if(status)status.value="";
  loadMessageTraces().catch(err=>toast(err.message));
}
