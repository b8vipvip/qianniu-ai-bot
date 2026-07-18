using Bot.ChatRecord;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Bot.ChromeNs
{
    internal sealed class VisionImageResult
    {
        public bool Success { get; set; }
        public string ImageUrl { get; set; }
        public string MimeType { get; set; }
        public long Bytes { get; set; }
        public string Error { get; set; }
    }

    internal sealed class VisionImageResolver
    {
        private static readonly string[] AllowedMimeTypes = { "image/jpeg", "image/png", "image/webp", "image/gif" };

        public async Task<VisionImageResult> ResolveAsync(QNChatMessage message, AiEndpointConfig endpoint, CancellationToken cancellationToken)
        {
            var url = ExtractUrl(message);
            if (string.IsNullOrWhiteSpace(url)) return Fail("图片 URL 不存在");

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return Fail("图片 URL 协议不受支持");
            }

            var limit = Math.Max(1, Math.Min(20, endpoint.MaxImageSizeMb)) * 1024L * 1024L;
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(180, endpoint.VisionTimeoutSeconds)));
                using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode) return Fail("图片下载失败：HTTP " + (int)response.StatusCode);
                    var length = response.Content.Headers.ContentLength;
                    if (length.HasValue && length.Value > limit) return Fail("图片超过大小限制");

                    var bytes = await ReadWithLimitAsync(response.Content, limit, cancellationToken);
                    if (bytes == null) return Fail("图片超过大小限制");
                    var headerMime = response.Content.Headers.ContentType == null
                        ? string.Empty
                        : (response.Content.Headers.ContentType.MediaType ?? string.Empty).ToLowerInvariant();
                    var detectedMime = DetectMime(bytes);
                    if (string.IsNullOrWhiteSpace(detectedMime)) return Fail("图片数据损坏或格式不支持");
                    if (!AllowedMimeTypes.Contains(detectedMime, StringComparer.OrdinalIgnoreCase)) return Fail("MIME 类型不支持");
                    if (!string.IsNullOrWhiteSpace(headerMime)
                        && !AllowedMimeTypes.Contains(headerMime, StringComparer.OrdinalIgnoreCase))
                    {
                        return Fail("MIME 类型不支持");
                    }
                    if (!string.IsNullOrWhiteSpace(headerMime)
                        && !string.Equals(headerMime, detectedMime, StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail("图片 MIME 与实际内容不一致");
                    }

                    var dataUri = "data:" + detectedMime + ";base64," + Convert.ToBase64String(bytes);
                    return new VisionImageResult
                    {
                        Success = true,
                        ImageUrl = dataUri,
                        MimeType = detectedMime,
                        Bytes = bytes.LongLength
                    };
                }
            }
        }

        internal static string ExtractUrl(QNChatMessage message)
        {
            if (message == null || message.originalData == null) return string.Empty;
            if (!string.IsNullOrWhiteSpace(message.originalData.url)) return message.originalData.url.Trim();
            return string.IsNullOrWhiteSpace(message.originalData.fileId) ? string.Empty : message.originalData.fileId.Trim();
        }

        internal static bool LooksLikeImage(byte[] bytes, string mime)
        {
            var detected = DetectMime(bytes);
            return !string.IsNullOrWhiteSpace(detected)
                && string.Equals(detected, mime, StringComparison.OrdinalIgnoreCase);
        }

        internal static string DetectMime(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12) return string.Empty;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return "image/png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return "image/jpeg";
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return "image/gif";
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return "image/webp";
            return string.Empty;
        }

        private static async Task<byte[]> ReadWithLimitAsync(HttpContent content, long limit, CancellationToken cancellationToken)
        {
            using (var input = await content.ReadAsStreamAsync())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read <= 0) break;
                    if (output.Length + read > limit) return null;
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static VisionImageResult Fail(string error)
        {
            return new VisionImageResult { Success = false, Error = error };
        }
    }
}
