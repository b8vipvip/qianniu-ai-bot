[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Utf8PreserveBom {
    param([string]$Path, [string]$Text)
    $bytes = [IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($hasBom))
}

function Replace-Once {
    param([string]$Path, [string]$Old, [string]$New)
    $text = [IO.File]::ReadAllText($Path)
    $first = $text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Expected text not found in $Path" }
    $second = $text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) { throw "Expected text is ambiguous in $Path" }
    $updated = $text.Substring(0, $first) + $New + $text.Substring($first + $Old.Length)
    Write-Utf8PreserveBom -Path $Path -Text $updated
}

function Replace-CSharpMethod {
    param([string]$Path, [string]$SignatureText, [string]$Replacement)
    $text = [IO.File]::ReadAllText($Path)
    $signatureIndex = $text.IndexOf($SignatureText, [StringComparison]::Ordinal)
    if ($signatureIndex -lt 0) { throw "Method signature not found in $Path : $SignatureText" }
    $second = $text.IndexOf($SignatureText, $signatureIndex + $SignatureText.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) { throw "Method signature is ambiguous in $Path : $SignatureText" }
    $openBrace = $text.IndexOf('{', $signatureIndex)
    if ($openBrace -lt 0) { throw "Opening brace not found for $SignatureText" }
    $depth = 0
    $closeBrace = -1
    for ($i = $openBrace; $i -lt $text.Length; $i++) {
        if ($text[$i] -eq '{') { $depth++ }
        elseif ($text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { $closeBrace = $i; break }
        }
    }
    if ($closeBrace -lt 0) { throw "Closing brace not found for $SignatureText" }
    $methodStart = $text.LastIndexOf("`n", $signatureIndex)
    if ($methodStart -lt 0) { $methodStart = 0 } else { $methodStart++ }
    $methodLength = $closeBrace - $methodStart + 1
    $updated = $text.Substring(0, $methodStart) + $Replacement + $text.Substring($methodStart + $methodLength)
    Write-Utf8PreserveBom -Path $Path -Text $updated
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$qnPath = Join-Path $root 'src\Bot\ChromeNs\QN.cs'
$robotPath = Join-Path $root 'src\Bot\AssistWindow\Widget\Robot\CtlRobot.xaml.cs'
$conversationPath = Join-Path $root 'src\Bot\AssistWindow\Widget\Robot\CtlConversation.xaml.cs'
$targetsPath = Join-Path $root 'src\Directory.Build.targets'
$helperPath = Join-Path $root 'src\Bot\ChromeNs\IncomingMessageSafety.cs'

$helper = @'
using Bot.ChatRecord;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Bot.ChromeNs
{
    internal sealed class IncomingMessageDecision
    {
        public bool ShouldCallAi { get; set; }
        public string MessageLabel { get; set; }
        public string Note { get; set; }
    }

    internal sealed class IncomingMessageDeduplicator
    {
        private readonly object _sync = new object();
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _order = new Queue<string>();
        private readonly int _capacity;

        public IncomingMessageDeduplicator(int capacity)
        {
            _capacity = Math.Max(100, capacity);
        }

        public bool TryAccept(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return true;
            lock (_sync)
            {
                if (_seen.Contains(key)) return false;
                _seen.Add(key);
                _order.Enqueue(key);
                while (_order.Count > _capacity)
                {
                    var old = _order.Dequeue();
                    _seen.Remove(old);
                }
                return true;
            }
        }
    }

    internal static class IncomingMessageSafety
    {
        private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic" };
        private static readonly string[] VideoExtensions = { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v" };
        private static readonly string[] AudioExtensions = { ".amr", ".mp3", ".wav", ".m4a", ".aac", ".ogg" };

        public static IncomingMessageDecision Evaluate(QNChatMessage message, string messageText, DateTime safetyStartedAt)
        {
            DateTime messageTime;
            if (TryGetMessageTime(message, out messageTime) && messageTime < safetyStartedAt.AddSeconds(-8))
            {
                return Skip("历史消息", "已跳过：这是 Bot 启动前的历史或未读消息，未调用AI，也未发送给买家。");
            }

            var unsupportedType = DetectUnsupportedType(message, messageText);
            if (!string.IsNullOrWhiteSpace(unsupportedType))
            {
                return Skip("[" + unsupportedType + "]", "已跳过：收到" + unsupportedType + "消息，当前版本未启用对应内容理解能力；未调用AI，也未发送给买家。");
            }

            if (string.IsNullOrWhiteSpace(messageText))
            {
                return Skip("[空白或未知消息]", "已跳过：消息内容为空或类型无法识别，未调用AI，也未发送给买家。");
            }

            return new IncomingMessageDecision
            {
                ShouldCallAi = true,
                MessageLabel = messageText,
                Note = string.Empty
            };
        }

        public static string BuildMessageKey(QNChatMessage message, string messageText)
        {
            if (message == null) return string.Empty;
            if (message.mcode != null && (!string.IsNullOrWhiteSpace(message.mcode.clientId) || !string.IsNullOrWhiteSpace(message.mcode.messageId)))
            {
                return "mcode:" + (message.mcode.clientId ?? string.Empty) + ":" + (message.mcode.messageId ?? string.Empty);
            }
            if (message.ext != null && message.ext.ww_msgid != 0)
            {
                return "ww:" + message.ext.ww_msgid;
            }
            return string.Join("|", new[]
            {
                message.cid == null ? string.Empty : message.cid.ccode,
                message.fromid == null ? string.Empty : message.fromid.nick,
                message.toid == null ? string.Empty : message.toid.nick,
                message.sendTime ?? string.Empty,
                message.sortTimeMicrosecond ?? string.Empty,
                message.templateId.ToString(CultureInfo.InvariantCulture),
                messageText ?? string.Empty,
                message.originalData == null ? string.Empty : (message.originalData.fileId ?? string.Empty)
            });
        }

        public static long GetSortValue(QNChatMessage message)
        {
            if (message == null) return 0;
            DateTime time;
            if (TryGetMessageTime(message, out time)) return time.Ticks;
            long raw;
            if (long.TryParse(message.sortTimeMicrosecond, out raw)) return raw;
            return 0;
        }

        private static IncomingMessageDecision Skip(string label, string note)
        {
            return new IncomingMessageDecision { ShouldCallAi = false, MessageLabel = label, Note = note };
        }

        private static string DetectUnsupportedType(QNChatMessage message, string messageText)
        {
            if (message == null) return "未知类型";
            var original = message.originalData;
            var fileId = original == null ? string.Empty : (original.fileId ?? string.Empty);
            var url = original == null ? string.Empty : (original.url ?? string.Empty);
            var combined = ((messageText ?? string.Empty) + " " + (message.summary ?? string.Empty)).Trim().ToLowerInvariant();

            if (HasExtension(fileId, ImageExtensions) || HasExtension(url, ImageExtensions) || ContainsMarker(combined, "图片", "image", "photo")) return "图片";
            if (HasExtension(fileId, VideoExtensions) || HasExtension(url, VideoExtensions) || ContainsMarker(combined, "视频", "video")) return "视频";
            if (HasExtension(fileId, AudioExtensions) || HasExtension(url, AudioExtensions) || ContainsMarker(combined, "语音", "音频", "voice", "audio")) return "语音";
            if (ContainsMarker(combined, "位置", "定位", "location")) return "位置";
            if (ContainsMarker(combined, "文件", "附件", "file")) return "文件";
            if (!string.IsNullOrWhiteSpace(fileId)) return "文件";
            return string.Empty;
        }

        private static bool ContainsMarker(string text, params string[] markers)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (var marker in markers)
            {
                if (text == "[" + marker + "]" || text == marker || text.Contains("[" + marker + "]")) return true;
            }
            return false;
        }

        private static bool HasExtension(string value, IEnumerable<string> extensions)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var clean = value.Split('?', '#')[0].ToLowerInvariant();
            return extensions.Any(clean.EndsWith);
        }

        private static bool TryGetMessageTime(QNChatMessage message, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (message == null) return false;
            if (TryParseTimeValue(message.sendTime, out localTime)) return true;
            return TryParseTimeValue(message.sortTimeMicrosecond, out localTime);
        }

        private static bool TryParseTimeValue(string value, out DateTime localTime)
        {
            localTime = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(value)) return false;
            long raw;
            if (long.TryParse(value.Trim(), out raw))
            {
                try
                {
                    if (raw > 1000000000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw / 1000L).LocalDateTime;
                    else if (raw > 100000000000L) localTime = DateTimeOffset.FromUnixTimeMilliseconds(raw).LocalDateTime;
                    else if (raw > 1000000000L) localTime = DateTimeOffset.FromUnixTimeSeconds(raw).LocalDateTime;
                    if (localTime != DateTime.MinValue) return true;
                }
                catch
                {
                }
            }

            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dto)
                || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dto))
            {
                localTime = dto.LocalDateTime;
                return true;
            }
            return false;
        }
    }
}
'@
[IO.File]::WriteAllText($helperPath, $helper, [Text.UTF8Encoding]::new($true))

$qnText = [IO.File]::ReadAllText($qnPath)
if ($qnText.IndexOf('using System.Threading;', [StringComparison]::Ordinal) -lt 0) {
    Replace-Once -Path $qnPath -Old 'using System.Threading.Tasks;' -New "using System.Threading;`r`nusing System.Threading.Tasks;"
}

$qnText = [IO.File]::ReadAllText($qnPath)
if ($qnText.IndexOf('_incomingMessageGate', [StringComparison]::Ordinal) -lt 0) {
    Replace-Once -Path $qnPath -Old '        private DateTime _lastSellerEchoTime = DateTime.MinValue;' -New @'
        private DateTime _lastSellerEchoTime = DateTime.MinValue;
        private readonly SemaphoreSlim _incomingMessageGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        private readonly IncomingMessageDeduplicator _incomingMessageDeduplicator = new IncomingMessageDeduplicator(2000);
        private readonly DateTime _messageSafetyStartedAt = DateTime.Now;
'@
}

$sendWithRetry = @'
        public async Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)
        {
            await _sendGate.WaitAsync();
            try
            {
                if (!await EnsureActiveBuyerForSendAsync(buyer))
                {
                    BotConnectionDiagnostics.RecordSendAttempt(false, "无法确认目标买家会话");
                    return false;
                }

                var ok = await SendTextAsync(buyer, text);
                var retry = Math.Max(0, retryCount);
                for (var i = 0; !ok && i < retry; i++)
                {
                    Log.Info("自动发送失败，准备重试第" + (i + 1) + "次。buyer=" + buyer + ", text=" + text);
                    await Task.Delay(1800);
                    if (!await EnsureActiveBuyerForSendAsync(buyer)) return false;
                    ok = await SendTextAsync(buyer, text);
                }
                return ok;
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task<bool> EnsureActiveBuyerForSendAsync(string buyer)
        {
            buyer = (buyer ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buyer) || cdp == null) return false;

            for (var attempt = 0; attempt < 22; attempt++)
            {
                try
                {
                    var current = await GetCurrentConversationID();
                    var currentNick = current == null || current.Result == null ? string.Empty : (current.Result.Nick ?? string.Empty).Trim();
                    if (currentNick == buyer)
                    {
                        SetActiveConversationByNick(Seller == null ? string.Empty : Seller.Nick, buyer, "sendVerified");
                        return true;
                    }
                    if (attempt == 0)
                    {
                        Log.Info("发送前切换目标买家: target=" + buyer + ", current=" + currentNick);
                        OpenChat(buyer);
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("发送前确认买家会话失败: " + ex.Message);
                    if (attempt == 0) OpenChat(buyer);
                }
                await Task.Delay(250);
            }

            Log.Error("发送已阻止：无法确认当前会话为目标买家。target=" + buyer);
            return false;
        }
'@
Replace-CSharpMethod -Path $qnPath -SignatureText 'public async Task<bool> SendTextWithRetryAsync(string buyer, string text, int retryCount = 1)' -Replacement $sendWithRetry

$shopRobot = @'
        private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)
        {
            // 这是后台新消息通知，不等于千牛当前可见聊天已经切换。
            // 绝不能在这里修改 QN.Buyer 或提前打开聊天，否则后台买家的答案可能发到当前可见买家。
            if (e != null && e.Seller != null && e.Buyer != null)
            {
                Log.Info("收到后台买家消息通知: seller=" + e.Seller.Nick + ", buyer=" + e.Buyer.Nick);
            }
            if (EvShopRobotReceriveNewMessage != null)
            {
                EvShopRobotReceriveNewMessage(this, e);
            }
        }
'@
Replace-CSharpMethod -Path $qnPath -SignatureText 'private void Cdp_EvShopRobotReceriveNewMessage(object sender, ShopRobotReceriveNewMessageEventArgs e)' -Replacement $shopRobot

$receiveMethod = @'
        private async Task ProcessIncomingMessageAsync(QNChatMessage message)
        {
            if (message == null) return;
            var messageText = GetMessageText(message);
            var messageKey = IncomingMessageSafety.BuildMessageKey(message, messageText);
            if (!_incomingMessageDeduplicator.TryAccept(messageKey))
            {
                Log.Info("重复消息已跳过: key=" + messageKey);
                return;
            }

            if (IsSellerMessage(message))
            {
                RecordSellerEcho(message.toid.nick, messageText);
                return;
            }
            if (!IsBuyerMessage(message)) return;

            var sellerNick = message.toid.nick;
            var buyerNick = message.fromid.nick;
            var decision = IncomingMessageSafety.Evaluate(message, messageText, _messageSafetyStartedAt);
            var displayQuestion = string.IsNullOrWhiteSpace(messageText) ? decision.MessageLabel : messageText;

            if (!decision.ShouldCallAi)
            {
                AddSkippedConversation(sellerNick, buyerNick, displayQuestion, decision.Note);
                Log.Info("买家消息安全跳过: buyer=" + buyerNick + ", reason=" + decision.Note);
                return;
            }

            var botEnabled = Params.Robot.CanUseRobotReal;
            var autoSend = Params.Robot.GetIsAutoReply();
            if (!botEnabled)
            {
                AddSkippedConversation(sellerNick, buyerNick, displayQuestion, "Bot已停用，未调用AI，也未发送给买家。");
                return;
            }

            var answer = MyOpenAI.GetAnswer(sellerNick, buyerNick, messageText);
            var conversationCtl = Desk.Inst == null ? null : Desk.Inst.AddConversation(sellerNick, buyerNick, messageText, answer, autoSend);
            if (!autoSend) return;

            if (string.IsNullOrWhiteSpace(answer) || answer.StartsWith("错误："))
            {
                if (conversationCtl != null) conversationCtl.SetSendResult(false, "未发送：AI错误");
                return;
            }

            var sendOk = await SendTextWithRetryAsync(buyerNick, answer, 1);
            if (conversationCtl != null)
            {
                conversationCtl.SetSendResult(sendOk, sendOk ? "已发送" : "发送失败：目标买家会话未确认或发送未完成");
            }
        }

        private void AddSkippedConversation(string seller, string buyer, string question, string note)
        {
            if (Desk.Inst == null) return;
            var ctl = Desk.Inst.AddConversation(seller, buyer, question, note, false);
            if (ctl != null) ctl.SetSkipped(note);
        }

        private async void Cdp_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.Message)) return;
            await _incomingMessageGate.WaitAsync();
            try
            {
                if (EvRecieveNewMessage != null)
                {
                    EvRecieveNewMessage(this, e);
                }

                Log.Info("收到千牛新消息事件: " + e.Message);
                var chatRes = JsonConvert.DeserializeObject<ChatResponse>(e.Message);
                if (chatRes == null || chatRes.result == null)
                {
                    Log.Error("收到新消息但无法解析: " + e.Message);
                    return;
                }

                var messages = chatRes.result
                    .Where(m => m != null)
                    .OrderBy(IncomingMessageSafety.GetSortValue)
                    .ToList();

                // GetNewMsg 有时一次返回同一买家的多条未读消息。只处理该批次最新一条，避免启动或网络恢复时连续轰炸买家。
                var latestBuyerMessages = messages
                    .Where(IsBuyerMessage)
                    .GroupBy(m => (m.toid == null ? string.Empty : m.toid.nick) + "#" + (m.fromid == null ? string.Empty : m.fromid.nick))
                    .ToDictionary(g => g.Key, g => g.Last());

                foreach (var message in messages)
                {
                    if (IsBuyerMessage(message))
                    {
                        var buyerKey = message.toid.nick + "#" + message.fromid.nick;
                        QNChatMessage latest;
                        if (latestBuyerMessages.TryGetValue(buyerKey, out latest) && !object.ReferenceEquals(message, latest))
                        {
                            var oldKey = IncomingMessageSafety.BuildMessageKey(message, GetMessageText(message));
                            _incomingMessageDeduplicator.TryAccept(oldKey);
                            Log.Info("同批次较早买家消息已合并跳过: buyer=" + message.fromid.nick + ", key=" + oldKey);
                            continue;
                        }
                    }

                    await ProcessIncomingMessageAsync(message);
                    await Task.Delay(250);
                }
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
            }
            finally
            {
                _incomingMessageGate.Release();
            }
        }
'@
Replace-CSharpMethod -Path $qnPath -SignatureText 'private void Cdp_EvRecieveNewMessage(object sender, RecieveNewMessageEventArgs e)' -Replacement $receiveMethod

$refreshItems = @'
        private static string BuildGoodsIdentity(ZnkfItem item)
        {
            if (item == null) return string.Empty;
            var itemId = Convert.ToString(item.itemId);
            if (!string.IsNullOrWhiteSpace(itemId)) return "id:" + itemId.Trim();
            if (!string.IsNullOrWhiteSpace(item.itemUrl)) return "url:" + item.itemUrl.Trim();
            return "fallback:" + (item.title ?? string.Empty).Trim() + "|" + (item.price ?? string.Empty).Trim();
        }

        private async void RefreshItems()
        {
            try
            {
                if (_preQN == null || _preQN.Buyer == null) return;
                pgDownGoods.Visibility = Visibility.Visible;
                RemoveCtlGoods();
                var itemRecord = await _preQN.GetItemRecords(_preQN.Buyer.TargetId);
                if (itemRecord == null || itemRecord.data == null || itemRecord.data.underInquiryItemList == null)
                {
                    pgDownGoods.Visibility = Visibility.Collapsed;
                    return;
                }

                var distinctItems = itemRecord.data.underInquiryItemList
                    .Where(item => item != null)
                    .GroupBy(BuildGoodsIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(8)
                    .ToList();

                foreach (var item in distinctItems)
                {
                    panelGoods.Children.Add(new CtlOneGoods(item));
                }
                pgDownGoods.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                pgDownGoods.Visibility = Visibility.Collapsed;
            }
        }
'@
Replace-CSharpMethod -Path $robotPath -SignatureText 'private async void RefreshItems()' -Replacement $refreshItems

$conversationText = [IO.File]::ReadAllText($conversationPath)
if ($conversationText.IndexOf('_canResend', [StringComparison]::Ordinal) -lt 0) {
    Replace-Once -Path $conversationPath -Old '        private string _answer = string.Empty;' -New "        private string _answer = string.Empty;`r`n        private bool _canResend = true;"
}

$setupMethod = @'
        public void Setup(string seller, string buyer, string question, string answer, bool isAutoReply)
        {
            _seller = seller ?? string.Empty;
            _buyer = buyer ?? string.Empty;
            _answer = answer ?? string.Empty;
            _canResend = true;
            txtQuestion.Text = question ?? string.Empty;
            txtAnswer.Text = _answer;
            txtStatus.Text = isAutoReply ? "正在发送..." : "仅生成答案";
            txtStatus.Foreground = new SolidColorBrush(isAutoReply ? Color.FromRgb(47, 128, 237) : Color.FromRgb(107, 114, 128));
            txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
        }

        public void SetSkipped(string detail)
        {
            _canResend = false;
            Ui(() =>
            {
                txtAnswer.Text = _answer;
                txtStatus.Text = string.IsNullOrWhiteSpace(detail) ? "已跳过，未发送" : "已跳过，未发送";
                txtStatus.ToolTip = detail ?? string.Empty;
                txtStatus.Foreground = new SolidColorBrush(Color.FromRgb(242, 153, 74));
                txtTime.Text = DateTime.Now.ToString("HH:mm:ss");
            });
        }
'@
Replace-CSharpMethod -Path $conversationPath -SignatureText 'public void Setup(string seller, string buyer, string question, string answer, bool isAutoReply)' -Replacement $setupMethod

$mouseMethod = @'
        private void txtAnswer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_canResend) return;
            e.Handled = true;
            var menu = new ContextMenu();
            var item = new MenuItem { Header = "重发这条答案" };
            item.Click += (s, args) => RaiseResendRequested();
            menu.Items.Add(item);
            menu.PlacementTarget = txtAnswer;
            menu.IsOpen = true;
        }
'@
Replace-CSharpMethod -Path $conversationPath -SignatureText 'private void txtAnswer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)' -Replacement $mouseMethod

$targetsText = [IO.File]::ReadAllText($targetsPath)
if ($targetsText.IndexOf('IncomingMessageSafety.cs', [StringComparison]::Ordinal) -lt 0) {
    Replace-Once -Path $targetsPath -Old '</Project>' -New @'
  <ItemGroup Condition="Exists('$(MSBuildProjectDirectory)\ChromeNs\IncomingMessageSafety.cs')">
    <Compile Include="$(MSBuildProjectDirectory)\ChromeNs\IncomingMessageSafety.cs" />
  </ItemGroup>
</Project>
'@
}

Set-Location $root
git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed' }

Write-Host 'MESSAGE_SAFETY_PATCH=PASS'
git status --short
