using System.Text;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Subtitle;

public static class SubtitleDeduplicatorDebugScenarios
{
    public static string RunAll()
    {
        var builder = new StringBuilder();

        RunScenario(
            builder,
            "测试 1：英文修正扩展",
            [
                "If I make flour with it this",
                "If I make flour with it, this will give us the next.",
                "If I make flour with it, this will give us the next advancement you can see."
            ],
            ["If I make flour with it, this will give us the next advancement you can see."]);

        RunScenario(
            builder,
            "测试 2：英文增量字幕",
            [
                "I think",
                "I think this",
                "I think this is",
                "I think this is useful."
            ],
            ["I think this is useful."]);

        RunScenario(
            builder,
            "测试 3：日文字幕",
            [
                "これは",
                "これはテスト",
                "これはテストです。"
            ],
            ["これはテストです。"]);

        RunScenario(
            builder,
            "测试 4：中文字幕",
            [
                "你好",
                "你好这是",
                "你好这是一个测试。"
            ],
            ["你好这是一个测试。"]);

        RunScenario(
            builder,
            "测试 5：新句独立提交",
            [
                "If I make flour with it, this will give us the next advancement.",
                "Alright with what we have."
            ],
            [
                "If I make flour with it, this will give us the next advancement.",
                "Alright with what we have."
            ],
            tickBetweenInputs: true);

        RunReplacementScenario(builder);
        RunTranscriptWindowScenario(builder);

        return builder.ToString().TrimEnd();
    }

    private static void RunScenario(
        StringBuilder builder,
        string name,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string> expected,
        bool tickBetweenInputs = false)
    {
        var deduplicator = new SubtitleDeduplicator();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var finalSegments = new List<CaptionSegment>();

        foreach (var input in inputs)
        {
            ApplyResult(finalSegments, deduplicator.Push(input, "Debug", now));
            now = now.AddMilliseconds(400);

            if (tickBetweenInputs)
            {
                now = now.AddMilliseconds(2200);
                ApplyResult(finalSegments, deduplicator.Tick("Debug", now));
            }
        }

        if (!tickBetweenInputs)
        {
            now = now.AddMilliseconds(2200);
            ApplyResult(finalSegments, deduplicator.Tick("Debug", now));
        }

        var actual = finalSegments.Select(segment => segment.Text).ToList();
        var passed = actual.SequenceEqual(expected, StringComparer.Ordinal);

        builder.AppendLine($"[{name}] {(passed ? "PASS" : "FAIL")}，提交 {actual.Count} 个片段");
        builder.AppendLine($"  期望：{string.Join(" | ", expected)}");
        builder.AppendLine($"  实际：{string.Join(" | ", actual)}");
    }

    private static void RunReplacementScenario(StringBuilder builder)
    {
        var deduplicator = new SubtitleDeduplicator();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var finalSegments = new List<CaptionSegment>();

        ApplyResult(finalSegments, deduplicator.Push("If I make flour with it this", "Debug", now));
        now = now.AddMilliseconds(2200);
        ApplyResult(finalSegments, deduplicator.Tick("Debug", now));

        now = now.AddMilliseconds(400);
        ApplyResult(finalSegments, deduplicator.Push("If I make flour with it, this will give us the next.", "Debug", now));
        now = now.AddMilliseconds(2200);
        ApplyResult(finalSegments, deduplicator.Tick("Debug", now));

        var expected = new[] { "If I make flour with it, this will give us the next." };
        var actual = finalSegments.Select(segment => segment.Text).ToList();
        var passed = actual.SequenceEqual(expected, StringComparer.Ordinal);

        builder.AppendLine($"[额外测试：更完整版本替换旧短句] {(passed ? "PASS" : "FAIL")}，提交 {actual.Count} 个片段");
        builder.AppendLine($"  期望：{string.Join(" | ", expected)}");
        builder.AppendLine($"  实际：{string.Join(" | ", actual)}");
    }

    private static void RunTranscriptWindowScenario(StringBuilder builder)
    {
        var deduplicator = new SubtitleDeduplicator();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var finalSegments = new List<CaptionSegment>();

        ApplyResult(finalSegments, deduplicator.Push(
            "Now the question is what is in here?",
            "Debug",
            now));

        now = now.AddMilliseconds(400);
        ApplyResult(finalSegments, deduplicator.Push(
            "Now the question is what is in here?\nNot a lot of great stuff if I'm being honest.",
            "Debug",
            now));

        now = now.AddMilliseconds(2200);
        ApplyResult(finalSegments, deduplicator.Tick("Debug", now));

        var expected = new[]
        {
            "Now the question is what is in here?",
            "Not a lot of great stuff if I'm being honest."
        };
        var actual = finalSegments.Select(segment => segment.Text).ToList();
        var passed = actual.SequenceEqual(expected, StringComparer.Ordinal);

        builder.AppendLine($"[额外测试：滚动 transcript 多句拆分] {(passed ? "PASS" : "FAIL")}，提交 {actual.Count} 个片段");
        builder.AppendLine($"  期望：{string.Join(" | ", expected)}");
        builder.AppendLine($"  实际：{string.Join(" | ", actual)}");
    }

    private static void ApplyResult(List<CaptionSegment> finalSegments, SubtitleDeduplicationResult result)
    {
        foreach (var replacedSegment in result.ReplacedSegments)
        {
            finalSegments.RemoveAll(segment => segment.Id == replacedSegment.Id);
        }

        finalSegments.AddRange(result.SubmittedSegments);
    }
}
