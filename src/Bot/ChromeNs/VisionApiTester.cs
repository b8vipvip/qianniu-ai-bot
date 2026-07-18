using Bot.Options;
using Newtonsoft.Json.Linq;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Bot.ChromeNs
{
    internal static class VisionApiTester
    {
        public static ApiEndpointTestResult Test(AiEndpointConfig endpoint)
        {
            if (endpoint == null || !endpoint.SupportsVision || string.IsNullOrWhiteSpace(endpoint.VisionModel))
            {
                return new ApiEndpointTestResult
                {
                    Success = false,
                    ShortStatus = "未配置视觉模型",
                    DisplayText = "失败：请先启用图片视觉理解并填写 VisionModel。"
                };
            }
            var marker = "QN_VISION_TEST_" + Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            var dataUri = CreateTestImageDataUri(marker);
            var payload = VisionRequestService.BuildVisionPayload(endpoint, dataUri, "请只回复图片中的随机测试字符串，不要添加其他内容。");
            return AiEndpointTester.TestVisionPayload(endpoint, payload, marker);
        }

        internal static string CreateTestImageDataUri(string marker)
        {
            using (var bmp = new Bitmap(420, 120))
            using (var g = Graphics.FromImage(bmp))
            using (var font = new Font("Arial", 22, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.Black))
            using (var ms = new MemoryStream())
            {
                g.Clear(Color.White);
                g.DrawString(marker, font, brush, 12, 38);
                bmp.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
    }
}
