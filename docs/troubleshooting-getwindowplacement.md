# bot.exe 日志：GetWindowPlacement return false 排查说明

## 日志现象

典型日志：

```text
System.Exception: GetWindowPlacement return false
   在 Bot.Automation.WinApi.GetWindowPlacement(Int32 hwnd)

ERROR: send message fail.hwnd=199796,desc=SendForGetText,total fail count=10,
ok count=0
Info: ChatDesk disposed,seller=千牛接待台
```

## 结论

这类错误不是 AI 接口配置错误。它发生在 bot 的 Windows 窗口自动化层。

含义是：bot 记录了一个千牛窗口或控件的 hwnd，但是调用 Windows API `GetWindowPlacement(hwnd)` 时失败。随后 bot 又尝试用 `SendForGetText` 获取控件文本，连续 10 次失败，最终释放当前接待台对象 `ChatDesk disposed`。

也就是说，bot 已经能识别接待台或买家，但在读取聊天内容/控件文本时失败。

## 常见原因

1. 千牛窗口句柄已失效
   - 千牛切换会话、刷新插件、重启接待台后，原 hwnd 失效；
   - bot 没有重新枚举窗口，继续使用旧 hwnd。

2. 权限级别不一致
   - 千牛以管理员运行，bot 普通权限运行；
   - 或 bot 以管理员运行，千牛普通权限运行。

3. 千牛窗口被最小化、隐藏、遮挡或远程桌面断开锁屏
   - Windows Server 上 RDP 断开后，UI 自动化经常失效；
   - GetWindowPlacement/SendMessage 对隐藏窗口、销毁窗口、跨会话窗口更容易失败。

4. 千牛版本/模式不匹配
   - 项目要求千牛多账号模式；
   - 需要开启千牛无障碍/讲述人模式；
   - 千牛版本变动后控件结构或窗口标题变化，bot 旧逻辑定位失败。

5. 注入插件或官方智能客服插件改变了窗口结构
   - 旺旺插件中心启用/注入插件后，千牛的聊天区域可能重建，导致旧 hwnd 失效；
   - 部分 UI 文案变成繁体，说明本地资源/插件状态可能被改变。

6. DPI/缩放/多显示器环境不兼容
   - 远程桌面分辨率、缩放比例变化后，窗口自动化和坐标定位可能失效。

## 建议修复顺序

### 1. 先统一权限

彻底退出千牛和 bot。

不要一方管理员、一方普通用户。建议先都用普通权限启动：

```powershell
Stop-Process -Name bot -Force -ErrorAction SilentlyContinue
Stop-Process -Name AliWorkbench -Force -ErrorAction SilentlyContinue
```

然后手动打开千牛，再打开 bot。

如果千牛必须管理员运行，则 bot 也右键“以管理员身份运行”。

### 2. 关闭自动回复，先测识别

bot 右上角取消勾选：

```text
自动回复
```

先确认它能稳定识别买家，不闪退，再打开自动回复。

### 3. 重启千牛并固定环境

- 开启多账号模式；
- 开启千牛无障碍/讲述人模式；
- 千牛窗口保持前台可见；
- 不要最小化；
- 显示缩放设为 100%；
- 固定分辨率；
- 不要在运行过程中切换 RDP 分辨率。

### 4. 暂停千牛插件中心里的智能客服类插件

如果 bot 注入后千牛文字变成繁体，先在旺旺插件中心关闭：

- 智能客服；
- AI 辅助接待；
- 其他可能改变接待台的插件。

重启千牛后再测试。

### 5. 抓 Windows 应用崩溃日志

```powershell
$start = (Get-Date).AddMinutes(-30)
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$start} |
Where-Object { $_.ProviderName -match '\.NET Runtime|Application Error|Windows Error Reporting' -or $_.Message -match 'bot|千牛|AliWorkbench' } |
Select-Object TimeCreated, ProviderName, Id, LevelDisplayName, Message |
Format-List |
Out-File C:\openbot\crash-events.txt -Encoding utf8
notepad C:\openbot\crash-events.txt
```

### 6. 枚举当前相关窗口

```powershell
Get-Process | Where-Object { $_.ProcessName -match 'bot|Ali|Qianniu|WangWang' } |
Select-Object Id, ProcessName, MainWindowHandle, MainWindowTitle, Path |
Format-List
```

如果 `MainWindowHandle` 是 0，或者 bot 日志里的 hwnd 对不上当前千牛窗口，说明 bot 正在使用失效句柄。

## 规避建议

当前二进制版本如果源码不可见，无法直接修复 `GetWindowPlacement` 异常。临时规避只能靠环境稳定：

- 固定千牛版本；
- 固定窗口位置和分辨率；
- 禁止自动升级；
- 不要最小化；
- 不要断开 RDP 锁屏；
- 自动回复前先保持千牛接待台前台打开；
- 每次启动顺序固定为：先千牛，再 bot。

更稳的长期方案是拿到源码后修改：

- 调用 `GetWindowPlacement` 前先 `IsWindow(hwnd)`；
- 失败时重新枚举窗口，不应抛异常导致接待台 disposed；
- `SendForGetText` 连续失败时重建 ChatDesk；
- 所有 UI 自动化异常写日志，不直接闪退；
- 给 hwnd、进程 ID、窗口标题、控件类名打详细日志。
