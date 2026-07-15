using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Bot.Knowledge
{
    public class ClipboardKnowledgeData
    {
        public string Text { get; set; }
        public List<KnowledgeMediaItem> Images { get; set; }
        public List<KnowledgeMediaItem> Videos { get; set; }
        public List<string> Warnings { get; set; }
        public bool HasAnalyzableContent { get { return !string.IsNullOrWhiteSpace(Text) || Images.Count > 0 || Videos.Count > 0; } }
        public ClipboardKnowledgeData() { Text = string.Empty; Images = new List<KnowledgeMediaItem>(); Videos = new List<KnowledgeMediaItem>(); Warnings = new List<string>(); }
    }
    public class KnowledgeMediaItem
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string Kind { get; set; }
        public string AiUrl { get; set; }
        public long SizeBytes { get; set; }
        public override string ToString() { return Kind + " - " + Name; }
    }
    public static class KnowledgeClipboardParser
    {
        public const long MaxImageBytes = 8 * 1024 * 1024;
        private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };
        private static readonly string[] VideoExts = { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv", ".m4v" };
        public static ClipboardKnowledgeData ReadClipboard()
        {
            var data = new ClipboardKnowledgeData();
            IDataObject obj = Clipboard.GetDataObject();
            if (obj == null) return data;
            if (obj.GetDataPresent(DataFormats.UnicodeText)) data.Text += Clipboard.GetText(TextDataFormat.UnicodeText) + "\n";
            else if (obj.GetDataPresent(DataFormats.Text)) data.Text += Clipboard.GetText(TextDataFormat.Text) + "\n";
            if (obj.GetDataPresent(DataFormats.Html))
            {
                var html = Clipboard.GetText(TextDataFormat.Html);
                data.Text += HtmlToText(html) + "\n";
                ExtractHtmlMedia(html, data);
            }
            if (obj.GetDataPresent(DataFormats.Bitmap))
            {
                var bmp = Clipboard.GetImage();
                if (bmp != null)
                {
                    var bytes = BitmapToPng(bmp);
                    if (bytes.Length <= MaxImageBytes) data.Images.Add(new KnowledgeMediaItem { Kind = "图片", Name = "剪贴板图片.png", Source = "Clipboard Bitmap", SizeBytes = bytes.Length, AiUrl = "data:image/png;base64," + Convert.ToBase64String(bytes) });
                    else data.Warnings.Add("剪贴板图片超过8MB，已跳过。");
                }
            }
            if (obj.GetDataPresent(DataFormats.FileDrop))
            {
                var files = obj.GetData(DataFormats.FileDrop) as string[];
                if (files != null) foreach (var file in files) AddFile(file, data);
            }
            data.Text = Regex.Replace(data.Text ?? string.Empty, "[ \t\r\n]+", " ").Trim();
            return data;
        }
        private static void AddFile(string file, ClipboardKnowledgeData data)
        {
            if (!File.Exists(file)) return;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ImageExts.Contains(ext))
            {
                var info = new FileInfo(file);
                if (info.Length > MaxImageBytes) { data.Warnings.Add(Path.GetFileName(file) + " 超过8MB，已跳过。"); return; }
                var bytes = File.ReadAllBytes(file);
                data.Images.Add(new KnowledgeMediaItem { Kind = "图片", Name = Path.GetFileName(file), Source = file, SizeBytes = bytes.Length, AiUrl = MimeDataUri(ext, bytes) });
            }
            else if (VideoExts.Contains(ext)) data.Videos.Add(new KnowledgeMediaItem { Kind = "视频", Name = Path.GetFileName(file), Source = file, SizeBytes = new FileInfo(file).Length });
        }
        private static string MimeDataUri(string ext, byte[] bytes)
        {
            var mime = ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : ext == ".bmp" ? "image/bmp" : ext == ".gif" ? "image/gif" : "image/png";
            return "data:" + mime + ";base64," + Convert.ToBase64String(bytes);
        }
        private static byte[] BitmapToPng(BitmapSource source)
        {
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(source));
            using (var ms = new MemoryStream()) { enc.Save(ms); return ms.ToArray(); }
        }
        private static string HtmlToText(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return string.Empty;
            var s = Regex.Replace(html, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", " ");
            return WebUtility.HtmlDecode(s);
        }
        private static void ExtractHtmlMedia(string html, ClipboardKnowledgeData data)
        {
            if (string.IsNullOrWhiteSpace(html)) return;
            foreach (Match m in Regex.Matches(html, "<(img|video|source)[^>]+src=[\"']?([^\"' >]+)", RegexOptions.IgnoreCase))
            {
                var tag = m.Groups[1].Value.ToLowerInvariant(); var url = WebUtility.HtmlDecode(m.Groups[2].Value);
                if (string.IsNullOrWhiteSpace(url)) continue;
                var isVideo = tag == "video" || VideoExts.Any(x => url.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                if (isVideo) data.Videos.Add(new KnowledgeMediaItem { Kind = "视频", Name = Path.GetFileName(url), Source = url });
                else data.Images.Add(new KnowledgeMediaItem { Kind = "图片", Name = Path.GetFileName(url), Source = url, AiUrl = url });
            }
        }
    }
}
