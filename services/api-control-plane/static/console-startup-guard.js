(()=>{
  const startupMessage="控制台前端启动失败。服务端页面已加载，请刷新后查看浏览器控制台错误；若仍失败请检查静态脚本是否完整返回。";

  function revealFallback(message=startupMessage){
    const app=document.getElementById("appView");
    if(app&&!app.classList.contains("hidden"))return;
    const login=document.getElementById("loginView");
    const error=document.getElementById("loginError");
    if(login)login.classList.remove("hidden");
    if(error&&!error.textContent)error.textContent=message;
  }

  window.addEventListener("error",event=>{
    const target=event&&event.target;
    if(target&&target.tagName==="SCRIPT"){
      revealFallback(`控制台脚本加载失败：${target.src||"未知脚本"}`);
      return;
    }
    revealFallback();
  },true);

  window.addEventListener("unhandledrejection",()=>revealFallback());

  window.addEventListener("DOMContentLoaded",()=>{
    const login=document.getElementById("loginView");
    const app=document.getElementById("appView");
    if(login&&app&&login.classList.contains("hidden")&&app.classList.contains("hidden")){
      revealFallback();
    }
  });
})();
