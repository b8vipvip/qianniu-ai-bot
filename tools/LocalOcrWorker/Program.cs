using RapidOcrNet;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QianniuAiBot.LocalOcrWorker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var started = Stopwatch.StartNew();
        try
        {
            var imagePath = ReadArg(args, "--image");
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                Write(new WorkerResult(false, string.Empty, 0d, started.ElapsedMilliseconds, "RapidOcrNet/PP-OCRv6-small", "image_not_found"));
                return 2;
            }

            var timeoutMs = 9000;
            var timeoutRaw = ReadArg(args, "--timeout-ms");
            if (int.TryParse(timeoutRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                timeoutMs = Math.Clamp(parsed, 1500, 30000);
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            using var ocr = new RapidOcr();
            ocr.InitModels(RapidOcrModelSet.PPOCRv6Small);
            var result = await ocr.DetectAsync(imagePath, RapidOcrOptions.PPOCRv6, null, cts.Token);
            var text = (result.StrRes ?? string.Empty).Trim();
            var confidence = CalculateConfidence(result.TextBlocks);
            Write(new WorkerResult(true, text, confidence, started.ElapsedMilliseconds, "RapidOcrNet/PP-OCRv6-small/ONNXRuntime", string.Empty));
            return 0;
        }
        catch (OperationCanceledException)
        {
            Write(new WorkerResult(false, string.Empty, 0d, started.ElapsedMilliseconds, "RapidOcrNet/PP-OCRv6-small/ONNXRuntime", "timeout_or_cancelled"));
            return 3;
        }
        catch (Exception ex)
        {
            Write(new WorkerResult(false, string.Empty, 0d, started.ElapsedMilliseconds, "RapidOcrNet/PP-OCRv6-small/ONNXRuntime", ex.GetType().Name + ": " + ex.Message));
            return 1;
        }
    }

    private static double CalculateConfidence(IEnumerable<object>? blocks)
    {
        if (blocks == null) return 0d;
        double sum = 0d;
        long count = 0;
        foreach (var block in blocks)
        {
            if (block == null) continue;
            var property = block.GetType().GetProperty("CharScores");
            var values = property?.GetValue(block) as IEnumerable;
            if (values == null) continue;
            foreach (var value in values)
            {
                if (value == null) continue;
                try
                {
                    var score = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(score) || double.IsInfinity(score)) continue;
                    sum += Math.Clamp(score, 0d, 1d);
                    count++;
                }
                catch
                {
                }
            }
        }
        return count < 1 ? 0d : Math.Clamp(sum / count, 0d, 1d);
    }

    private static string ReadArg(string[] args, string name)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }
        return string.Empty;
    }

    private static void Write(WorkerResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        }));
    }

    private sealed record WorkerResult(
        bool Ok,
        string Text,
        double Confidence,
        long ElapsedMs,
        string Engine,
        string Error);
}
