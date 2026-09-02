const OCR_MIB = 1024 * 1024;

function ocrEl(id){return document.getElementById(id)}

async function ocrApi(path,options={}){
  const response=await fetch(path,{
    credentials:"same-origin",
    headers:{"Content-Type":"application/json",...(options.headers||{})},
    ...options
  });
  let data=null;
  try{data=await response.json()}catch{}
  if(!response.ok){
    if(response.status===401){
      throw new Error("管理员登录已失效，请返回控制台重新登录");
    }
    throw new Error((data&&data.detail)||`请求失败 HTTP ${response.status}`);
  }
  return data||{};
}

function ocrSourceLabel(value){
  if(value==="database")return "控制台 / SQLite";
  if(value==="environment-defaults")return "环境变量默认值";
  return value||"-";
}

function ocrPriorityLabel(value){
  return value==="ai_first"?"AI 视觉接口优先":"OCR 优先";
}

function ocrSetResult(message,ok=true){
  const box=ocrEl("ocrResult");
  box.textContent=message||"";
  box.className=`ocr-result ${ok?"good":"bad"}`;
}

function ocrRender(data){
  ocrEl("ocrStatus").textContent=data.enabled?"已启用":"已停用";
  ocrEl("ocrEngine").textContent=data.engine||"RapidOCR/ONNXRuntime";
  ocrEl("ocrEngineLoaded").textContent=data.engine_loaded?"已加载":"按需加载（尚未执行识别）";
  ocrEl("ocrActiveRequests").textContent=String(data.active_requests||0);
  ocrEl("ocrSource").textContent=ocrSourceLabel(data.source);
  ocrEl("ocrUpdatedAt").textContent=data.updated_at||"-";
  ocrEl("ocrEnabled").checked=!!data.enabled;
  ocrEl("ocrMaxImageMb").value=(Number(data.max_image_bytes||0)/OCR_MIB).toFixed(2).replace(/\.00$/,"");
  ocrEl("ocrTimeoutSeconds").value=String(data.timeout_seconds??8);
  ocrEl("ocrMaxConcurrency").value=String(data.max_concurrency??2);
  ocrEl("ocrMaxTextChars").value=String(data.max_text_chars??6000);
}

function ocrRenderPriority(data){
  const priority=data&&data.vision_priority==="ai_first"?"ai_first":"ocr_first";
  ocrEl("ocrVisionPriority").value=priority;
  ocrEl("ocrVisionPriorityStatus").textContent=ocrPriorityLabel(priority);
}

async function loadOcrSettings(){
  const refresh=ocrEl("ocrRefreshBtn");
  if(refresh)refresh.disabled=true;
  try{
    const [data,priority]=await Promise.all([
      ocrApi("/api/admin/ocr/settings"),
      ocrApi("/api/admin/ocr/vision-priority")
    ]);
    ocrRender(data);
    ocrRenderPriority(priority);
    ocrSetResult("");
  }catch(err){
    ocrSetResult(err.message||String(err),false);
  }finally{
    if(refresh)refresh.disabled=false;
  }
}

async function saveOcrSettings(event){
  if(event)event.preventDefault();
  const save=ocrEl("ocrSaveBtn");
  save.disabled=true;
  try{
    const maxImageMb=Number(ocrEl("ocrMaxImageMb").value);
    const timeoutSeconds=Number(ocrEl("ocrTimeoutSeconds").value);
    const maxConcurrency=Number(ocrEl("ocrMaxConcurrency").value);
    const maxTextChars=Number(ocrEl("ocrMaxTextChars").value);
    const visionPriority=ocrEl("ocrVisionPriority").value;
    if(!Number.isFinite(maxImageMb)||maxImageMb<0.25||maxImageMb>64)throw new Error("最大图片大小必须在 0.25–64 MB 之间");
    if(!Number.isFinite(timeoutSeconds)||timeoutSeconds<1||timeoutSeconds>30)throw new Error("OCR 超时必须在 1–30 秒之间");
    if(!Number.isInteger(maxConcurrency)||maxConcurrency<1||maxConcurrency>8)throw new Error("最大并发必须是 1–8 的整数");
    if(!Number.isInteger(maxTextChars)||maxTextChars<256||maxTextChars>12000)throw new Error("最大返回文本字符必须是 256–12000 的整数");
    if(!["ocr_first","ai_first"].includes(visionPriority))throw new Error("视觉理解优先级无效");

    const data=await ocrApi("/api/admin/ocr/settings",{
      method:"PUT",
      body:JSON.stringify({
        enabled:ocrEl("ocrEnabled").checked,
        max_image_bytes:Math.round(maxImageMb*OCR_MIB),
        timeout_seconds:timeoutSeconds,
        max_concurrency:maxConcurrency,
        max_text_chars:maxTextChars
      })
    });
    const priority=await ocrApi("/api/admin/ocr/vision-priority",{
      method:"PUT",
      body:JSON.stringify({vision_priority:visionPriority})
    });
    ocrRender(data);
    ocrRenderPriority(priority);
    ocrSetResult("OCR 参数和视觉理解优先级已保存并立即生效，无需重启服务。",true);
  }catch(err){
    ocrSetResult(err.message||String(err),false);
  }finally{
    save.disabled=false;
  }
}

async function resetOcrSettings(){
  if(!confirm("恢复环境变量/内置默认值？当前控制台 OCR 参数和视觉理解优先级会被覆盖。"))return;
  const button=ocrEl("ocrResetBtn");
  button.disabled=true;
  try{
    const [data,priority]=await Promise.all([
      ocrApi("/api/admin/ocr/settings/reset",{method:"POST",body:"{}"}),
      ocrApi("/api/admin/ocr/vision-priority/reset",{method:"POST",body:"{}"})
    ]);
    ocrRender(data);
    ocrRenderPriority(priority);
    ocrSetResult("已恢复环境默认值并立即生效。",true);
  }catch(err){
    ocrSetResult(err.message||String(err),false);
  }finally{
    button.disabled=false;
  }
}

ocrEl("ocrSettingsForm").addEventListener("submit",saveOcrSettings);
ocrEl("ocrResetBtn").addEventListener("click",resetOcrSettings);
ocrEl("ocrRefreshBtn").addEventListener("click",loadOcrSettings);
loadOcrSettings();
