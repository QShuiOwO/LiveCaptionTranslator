using System.Text;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Subtitle;

public static class SubtitleDeduplicatorDebugScenarios
{
    public static string RunAll()
    {
        var builder = new StringBuilder();
        RunScenario(builder, "英文增量字幕", [
            "I think",
            "I think this",
            "I think this is",
            "I think this is useful"
        ], advanceAfterLastMs: 900);

        RunScenario(builder, "中文字幕", [
            "我觉得",
            "我觉得这个",
            "我觉得这个很有用。"
        ]);

        RunScenario(builder, "日文字幕", [
            "これは",
            "これは便利",
            "これは便利です。"
        ]);

        RunScenario(builder, "重复文本", [
            "hello world.",
            "hello world.",
            "hello world."
        ]);

        RunScenario(builder, "长文本", [
            new string('a', 520)
        ]);

        return builder.ToString().TrimEnd();
    }

    private static void RunScenario(
        StringBuilder builder,
        string name,
        IReadOnlyList<string> inputs,
        int advanceAfterLastMs = 0)
    {
        var deduplicator = new SubtitleDeduplicator();
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var submitted = new List<CaptionSegment>();

        foreach (var input in inputs)
        {
            submitted.AddRange(deduplicator.Push(input, "Debug", now).SubmittedSegments);
            now = now.AddMilliseconds(120);
        }

        if (advanceAfterLastMs > 0)
        {
            now = now.AddMilliseconds(advanceAfterLastMs);
            submitted.AddRange(deduplicator.Tick("Debug", now).SubmittedSegments);
        }

        builder.AppendLine($"[{name}] 提交 {submitted.Count} 个片段");
        foreach (var segment in submitted)
        {
            builder.AppendLine($"  - {segment.Text}");
        }
    }
}
