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
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return Fail("图片 URL 协议不受支持");
            if (uri.Scheme == Uri.UriSchemeHttps && !HasSensitiveQuery(uri)) return new VisionImageResult { Success = true, ImageUrl = url, MimeType = GuessMime(url), Bytes = 0 };

            var limit = Math.Max(1, Math.Min(20, endpoint.MaxImageSizeMb)) * 1024L * 1024L;
            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(Math.Max(10, Math.Min(180, endpoint.VisionTimeoutSeconds)));
                using (var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode) return Fail("图片下载失败");
                    var mime = (response.Content.Headers.ContentType == null ? string.Empty : response.Content.Headers.ContentType.MediaType) ?? string.Empty;
                    if (!AllowedMimeTypes.Contains(mime, StringComparer.OrdinalIgnoreCase)) return Fail("MIME 类型不支持");
                    var len = response.Content.Headers.ContentLength;
                    if (len.HasValue && len.Value > limit) return Fail("图片超过大小限制");
                    var bytes = await ReadWithLimitAsync(response.Content, limit, cancellationToken);
                    if (bytes == null) return Fail("图片超过大小限制");
                    if (!LooksLikeImage(bytes, mime)) return Fail("图片数据损坏");
                    var dataUri = "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
                    if (dataUri.Length > limit * 2) return Fail("Base64 后请求过大");
                    return new VisionImageResult { Success = true, ImageUrl = dataUri, MimeType = mime, Bytes = bytes.LongLength };
                }
            }
        }

        internal static string ExtractUrl(QNChatMessage message)
        {
            if (message == null || message.originalData == null) return string.Empty;
            return message.originalData.url ?? message.originalData.fileId ?? string.Empty;
        }

        internal static bool LooksLikeImage(byte[] bytes, string mime)
        {
            if (bytes == null || bytes.Length < 12) return false;
            if (mime == "image/png") return bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            if (mime == "image/jpeg") return bytes[0] == 0xFF && bytes[1] == 0xD8;
            if (mime == "image/gif") return bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46;
            if (mime == "image/webp") return bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46;
            return false;
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

        private static bool HasSensitiveQuery(Uri uri) { return !string.IsNullOrWhiteSpace(uri.Query); }
        private static string GuessMime(string url) { var u = (url ?? string.Empty).ToLowerInvariant(); if (u.Contains(".png")) return "image/png"; if (u.Contains(".webp")) return "image/webp"; if (u.Contains(".gif")) return "image/gif"; return "image/jpeg"; }
        private static VisionImageResult Fail(string error) { return new VisionImageResult { Success = false, Error = error }; }
    }
}
