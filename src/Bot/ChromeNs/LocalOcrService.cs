using BotLib;
using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    public sealed class LocalOcrResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public long ElapsedMs { get; set; }
        public bool CacheHit { get; set; }
        public string Engine { get; set; }
        public string Error { get; set; }
        public string ImageSha256 { get; set; }
    }

    internal sealed class LocalOcrWorkerResponse
    {
        public bool ok { get; set; }
        public string text { get; set; }
        public double confidence { get; set; }
        public long elapsedMs { get; set; }
        public string engine { get; set; }
        public string error { get; set; }
    }

    internal sealed class LocalOcrCacheEnvelope
    {
        public string ImageSha256 { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string Text { get; set; }
        public double Confidence { get; set; }
        public string Engine { get; set; }
    }

    /// <summary>
    /// Runs OCR locally through the bundled self-contained C# ONNX worker.
    /// No image bytes are uploaded by this service. Failures are soft: the normal
    /// vision pipeline continues without OCR evidence.
    /// </summary>
    public static class LocalOcrService
    {
        private const int DefaultTimeoutMs = 9000;
        private const int MaxEvidenceChars = 6000;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

        public static async Task<LocalOcrResult> TryRecognizeAsync(
            string imagePath,
            CancellationToken cancellationToken,
            int timeoutMs = DefaultTimeoutMs)
        {
            var startedAt = DateTime.UtcNow;
            imagePath = (imagePath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return Failure("本地图片文件不存在", startedAt);
            }

            string sha256;
            try
            {
                sha256 = ComputeSha256(imagePath);
            }
            catch (Exception ex)
            {
                return Failure("计算图片哈希失败: " + ex.Message, startedAt);
            }

            var cached = TryReadCache(sha256);
            if (cached != null)
            {
                return new LocalOcrResult
                {
                    Success = true,
                    Text = Limit(cached.Text),
                    Confidence = cached.Confidence,
                    ElapsedMs = Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
                    CacheHit = true,
                    Engine = cached.Engine,
                    Error = string.Empty,
                    ImageSha256 = sha256
                };
            }

            var workerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "local-ocr", "LocalOcrWorker.exe");
            if (!File.Exists(workerPath))
            {
                return Failure("本地OCR组件未安装: " + workerPath, startedAt, sha256);
            }

            Process process = null;
            try
            {
                var safeTimeout = Math.Max(1500, timeoutMs);
                var psi = new ProcessStartInfo
                {
                    FileName = workerPath,
                    Arguments = "--image \"" + EscapeArgument(imagePath) + "\" --timeout-ms "
                        + safeTimeout.ToString(CultureInfo.InvariantCulture),
                    WorkingDirectory = Path.GetDirectoryName(workerPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    return Failure("本地OCR进程启动失败", startedAt, sha256);
                }

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                var exitTask = Task.Run(() => process.WaitForExit(safeTimeout));
                var timeoutTask = Task.Delay(safeTimeout, cancellationToken);
                var completed = await Task.WhenAny(exitTask, timeoutTask);
                if (completed != exitTask || !exitTask.Result)
                {
                    TryKill(process);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Failure("本地OCR已取消", startedAt, sha256);
                    }
                    return Failure("本地OCR超时", startedAt, sha256);
                }

                var stdout = (await stdoutTask ?? string.Empty).Trim();
                var stderr = (await stderrTask ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(stdout))
                {
                    return Failure("本地OCR无输出" + (string.IsNullOrWhiteSpace(stderr) ? string.Empty : ": " + LimitError(stderr)), startedAt, sha256);
                }

                LocalOcrWorkerResponse response;
                try
                {
                    response = JsonConvert.DeserializeObject<LocalOcrWorkerResponse>(stdout);
                }
                catch (Exception ex)
                {
                    return Failure("本地OCR输出解析失败: " + ex.Message, startedAt, sha256);
                }

                if (response == null || !response.ok)
                {
                    var error = response == null ? "本地OCR返回空结果" : response.error;
                    return Failure(string.IsNullOrWhiteSpace(error) ? "本地OCR识别失败" : error, startedAt, sha256);
                }

                var text = Limit(response.text);
                var result = new LocalOcrResult
                {
                    Success = true,
                    Text = text,
                    Confidence = Clamp(response.confidence),
                    ElapsedMs = response.elapsedMs > 0
                        ? response.elapsedMs
                        : Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
                    CacheHit = false,
                    Engine = string.IsNullOrWhiteSpace(response.engine) ? "RapidOcrNet/ONNX" : response.engine.Trim(),
                    Error = string.Empty,
                    ImageSha256 = sha256
                };
                TryWriteCache(result);
                Log.Info("本地OCR完成: sha256=" + ShortHash(sha256)
                    + ", chars=" + (result.Text == null ? 0 : result.Text.Length)
                    + ", confidence=" + result.Confidence.ToString("0.000", CultureInfo.InvariantCulture)
                    + ", elapsedMs=" + result.ElapsedMs
                    + ", cacheHit=false");
                return result;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return Failure("本地OCR已取消", startedAt, sha256);
            }
            catch (Exception ex)
            {
                TryKill(process);
                return Failure("本地OCR异常: " + ex.Message, startedAt, sha256);
            }
            finally
            {
                if (process != null) process.Dispose();
            }
        }

        public static string BuildPromptEvidence(LocalOcrResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.Text)) return string.Empty;
            return "\n\n[本地OCR预识别，仅作辅助证据，可能存在错字；请以图片本身为准]\n"
                + Limit(result.Text)
                + "\n[OCR置信度=" + Clamp(result.Confidence).ToString("0.000", CultureInfo.InvariantCulture)
                + ", 引擎=" + (result.Engine ?? "local") + "]";
        }

        private static LocalOcrCacheEnvelope TryReadCache(string sha256)
        {
            try
            {
                var path = GetCachePath(sha256);
                if (!File.Exists(path)) return null;
                var envelope = JsonConvert.DeserializeObject<LocalOcrCacheEnvelope>(File.ReadAllText(path, Encoding.UTF8));
                if (envelope == null || !string.Equals(envelope.ImageSha256, sha256, StringComparison.OrdinalIgnoreCase)) return null;
                if (envelope.CreatedAtUtc == default(DateTime) || DateTime.UtcNow - envelope.CreatedAtUtc > CacheTtl)
                {
                    try { File.Delete(path); } catch { }
                    return null;
                }
                Log.Info("本地OCR缓存命中: sha256=" + ShortHash(sha256));
                return envelope;
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteCache(LocalOcrResult result)
        {
            if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.ImageSha256)) return;
            try
            {
                var path = GetCachePath(result.ImageSha256);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var envelope = new LocalOcrCacheEnvelope
                {
                    ImageSha256 = result.ImageSha256,
                    CreatedAtUtc = DateTime.UtcNow,
                    Text = Limit(result.Text),
                    Confidence = Clamp(result.Confidence),
                    Engine = result.Engine
                };
                File.WriteAllText(path, JsonConvert.SerializeObject(envelope), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private static string GetCachePath(string sha256)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QianniuAiBot",
                "ocr-cache");
            return Path.Combine(root, sha256 + ".json");
        }

        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string EscapeArgument(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private static string Limit(string value)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length <= MaxEvidenceChars) return value;
            return value.Substring(0, MaxEvidenceChars) + "…";
        }

        private static string LimitError(string value)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= 500 ? value : value.Substring(0, 500) + "…";
        }

        private static double Clamp(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0d;
            return Math.Max(0d, Math.Min(1d, value));
        }

        private static string ShortHash(string sha256)
        {
            if (string.IsNullOrWhiteSpace(sha256)) return string.Empty;
            return sha256.Length <= 12 ? sha256 : sha256.Substring(0, 12);
        }

        private static LocalOcrResult Failure(string error, DateTime startedAt, string sha256 = null)
        {
            Log.Info("本地OCR跳过/失败: " + error);
            return new LocalOcrResult
            {
                Success = false,
                Text = string.Empty,
                Confidence = 0d,
                ElapsedMs = Math.Max(0, (long)(DateTime.UtcNow - startedAt).TotalMilliseconds),
                CacheHit = false,
                Engine = "RapidOcrNet/ONNX",
                Error = error ?? string.Empty,
                ImageSha256 = sha256 ?? string.Empty
            };
        }

        private static void TryKill(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch
            {
            }
        }
    }
}