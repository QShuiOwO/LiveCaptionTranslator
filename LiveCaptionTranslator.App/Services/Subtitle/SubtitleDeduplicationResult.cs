using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Subtitle;

public sealed record SubtitleDeduplicationResult(
    string RealtimeText,
    string BufferText,
    IReadOnlyList<CaptionSegment> SubmittedSegments);
