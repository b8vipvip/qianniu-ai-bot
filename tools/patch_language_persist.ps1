param()

$root = Split-Path $PSScriptRoot -Parent

Write-Host "Apply permanent language patch..."

$inject = "$root\src\Bin\inject.js"

if(Test-Path $inject){

$content = Get-Content $inject -Raw -Encoding UTF8


if($content -notmatch "force zh-CN persistent"){

$patch=@'

// force zh-CN persistent
(function(){
 try{
   localStorage.setItem("locale","zh-CN");
   localStorage.setItem("language","zh-CN");
   localStorage.setItem("lang","zh-CN");
   document.cookie="locale=zh-CN";
   document.cookie="language=zh-CN";
 }catch(e){}
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