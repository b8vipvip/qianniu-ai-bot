(function(){
  const embeddedPages={
    wecom:{frameId:"wecomFrame",src:"/static/wecom.html?embedded=1"},
    "recharge-query":{frameId:"rechargeQueryFrame",src:"/static/recharge-query.html?embedded=1"}
  };
  const knownPages=new Set(["dashboard","providers","tests","clients","message-traces","wecom","recharge-query","deploy"]);

  if(typeof titles!=="undefined"){
    titles.wecom=["企业微信","配置企业微信应用、加密回调和 AI 转人工策略。"];
    titles["recharge-query"]=["充值结果自动查询","配置充值状态查询、后台访问 Key 和即时测试。"];
  }

  // app.js historically attaches switchPage() to every .nav element, including real links.
  // Restore normal anchor behaviour for Bot Web and any future external first-level entry.
  document.querySelectorAll("a.nav:not([data-page])").forEach(link=>{link.onclick=null;});

  function hideLegacyChildShell(frame,page){
    try{
      const doc=frame.contentDocument;
      if(!doc)return;
      const aside=doc.querySelector("aside");
      if(aside)aside.style.display="none";
      const shell=doc.querySelector(".shell");
      if(shell){
        shell.style.display="block";
        shell.style.minHeight="0";
      }
      const main=doc.querySelector("main");
      if(main){
        main.style.maxWidth="none";
        main.style.width="100%";
        main.style.margin="0";
        main.style.padding="0 2px 8px";
      }
      doc.documentElement.style.background="transparent";
      doc.body.style.background="transparent";
      if(page==="wecom"){
        const header=doc.querySelector("header.top");
        if(header)header.style.display="none";
      }else if(page==="recharge-query"&&main){
        const heading=main.querySelector(":scope > h1");
        if(heading)heading.style.display="none";
      }

      const resize=()=>{
        try{
          const height=Math.max(720,doc.documentElement.scrollHeight,doc.body?doc.body.scrollHeight:0);
          frame.style.height=(height+12)+"px";
        }catch{}
      };
      resize();
      if(typeof ResizeObserver!=="undefined"){
        const observer=new ResizeObserver(resize);
        observer.observe(doc.documentElement);
        frame._consoleResizeObserver=observer;
      }else{
        setTimeout(resize,300);
        setTimeout(resize,1200);
      }
    }catch(err){
      console.warn("嵌入控制台页面样式同步失败",err);
    }
  }

  function ensureEmbeddedPage(page){
    const config=embeddedPages[page];
    if(!config)return;
    const frame=document.getElementById(config.frameId);
    if(!frame)return;
    if(!frame.dataset.loaded){
      frame.dataset.loaded="1";
      frame.addEventListener("load",()=>hideLegacyChildShell(frame,page));
      frame.src=config.src;
    }
  }

  function routeUrl(page){
    return page==="dashboard"?"/":`/?page=${encodeURIComponent(page)}`;
  }

  document.querySelectorAll("button.nav[data-page]").forEach(button=>{
    button.addEventListener("click",()=>{
      const page=button.dataset.page;
      if(!knownPages.has(page))return;
      ensureEmbeddedPage(page);
      history.replaceState({page},"",routeUrl(page));
    });
  });

  const requested=new URLSearchParams(location.search).get("page");
  if(requested&&knownPages.has(requested)&&requested!=="dashboard"){
    ensureEmbeddedPage(requested);
    if(typeof switchPage==="function")switchPage(requested);
  }

  window.addEventListener("popstate",()=>{
    const page=new URLSearchParams(location.search).get("page")||"dashboard";
    if(!knownPages.has(page)||typeof switchPage!=="function")return;
    ensureEmbeddedPage(page);
    switchPage(page);
  });
})();
