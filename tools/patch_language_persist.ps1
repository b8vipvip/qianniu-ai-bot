param()

$root = Split-Path $PSScriptRoot -Parent

Write-Host "Apply permanent language patch..."

$inject = "$root\src\Bin\inject.js"

if(Test-Path $inject){

$content = Get-Content $inject -Raw -Encoding UTF8


if($content -notmatch "qnbot persistent zh-CN language lock"){

$patch=@'

// qnbot persistent zh-CN language lock
(function(){
  function lockZhCnLanguage(){
    try{
      localStorage.setItem("locale","zh-CN");
      localStorage.setItem("language","zh-CN");
      localStorage.setItem("lang","zh-CN");
      sessionStorage.setItem("locale","zh-CN");
      sessionStorage.setItem("language","zh-CN");
      sessionStorage.setItem("lang","zh-CN");
      document.cookie="locale=zh-CN; path=/; max-age=31536000";
      document.cookie="language=zh-CN; path=/; max-age=31536000";
      document.cookie="lang=zh-CN; path=/; max-age=31536000";
    }catch(e){}
  }
  lockZhCnLanguage();
  try { window.addEventListener("DOMContentLoaded", lockZhCnLanguage, true); } catch(e) {}
  try { window.addEventListener("load", lockZhCnLanguage, true); } catch(e) {}
})();

'@

$content=$patch+$content

Set-Content `
$inject `
$content `
-Encoding UTF8

Write-Host "inject.js patched"

}

}


$cs="$root\src\Bot\Common\QNInject.cs"

if(Test-Path $cs){

$content=Get-Content $cs -Raw -Encoding UTF8


if($content -notmatch "zh-CN-persistent"){

$content=$content.Replace(
"public class QNInject",
@"
// zh-CN-persistent
// language repair enabled

public class QNInject
"@
)

Set-Content `
$cs `
$content `
-Encoding UTF8

Write-Host "QNInject.cs patched"

}

}

Write-Host "Done"
