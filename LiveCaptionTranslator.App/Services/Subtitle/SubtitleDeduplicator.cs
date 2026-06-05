using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Subtitle;

public sealed class SubtitleDeduplicator
{
    private static readonly char[] FinalPunctuation =
    [
        '.',
        '?',
        '!',
        '。',
        '？',
        '！'
    ];

    private readonly SubtitleNormalizer _normalizer;
    private readonly HashSet<string> _submittedKeys = new(StringComparer.Ordinal);
    private readonly List<string> _submittedTexts = [];
    private string _lastRealtimeText = string.Empty;
    private string _bufferText = string.Empty;
    private DateTimeOffset _bufferCreatedAt = DateTimeOffset.Now;
    private DateTimeOffset _bufferUpdatedAt = DateTimeOffset.Now;

    public SubtitleDeduplicator()
        : this(new SubtitleNormalizer())
    {
    }

    public SubtitleDeduplicator(SubtitleNormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    public int StableDelayMs { get; init; } = 800;

    public int MaxBufferLength { get; init; } = 500;

    public int MinSegmentLength { get; init; } = 2;

    public string RealtimeText => _lastRealtimeText;

    public string BufferText => _bufferText;

    public SubtitleDeduplicationResult Push(
        string? rawText,
        string source = "LiveCaptions",
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.Now;
        var normalized = _normalizer.Normalize(rawText);
        if (string.Equals(normalized, _lastRealtimeText, StringComparison.Ordinal))
        {
            return CreateResult([]);
        }

        _lastRealtimeText = normalized;
        if (normalized.Length == 0)
        {
            return CreateResult([]);
        }

        var pendingText = RemoveSubmittedText(normalized);
        if (pendingText.Length == 0)
        {
            _bufferText = string.Empty;
            return CreateResult([]);
        }

        var submitted = new List<CaptionSegment>();
        UpdateBuffer(pendingText, source, now, submitted);
        SubmitReadyBoundarySegments(source, now, submitted);

        if (_bufferText.Length >= MaxBufferLength)
        {
            SubmitBuffer(source, now, submitted);
        }

        return CreateResult(submitted);
    }

    public SubtitleDeduplicationResult Tick(
        string source = "LiveCaptions",
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.Now;
        var submitted = new List<CaptionSegment>();

        if (_bufferText.Length >= MinSegmentLength &&
            now - _bufferUpdatedAt >= TimeSpan.FromMilliseconds(StableDelayMs))
        {
            SubmitBuffer(source, now, submitted);
        }

        return CreateResult(submitted);
    }

    public void Reset()
    {
        _lastRealtimeText = string.Empty;
        _bufferText = string.Empty;
        _bufferCreatedAt = DateTimeOffset.Now;
        _bufferUpdatedAt = _bufferCreatedAt;
        _submittedKeys.Clear();
        _submittedTexts.Clear();
    }

    private void UpdateBuffer(
        string pendingText,
        string source,
        DateTimeOffset timestamp,
        List<CaptionSegment> submitted)
    {
        if (_bufferText.Length == 0)
        {
            StartBuffer(pendingText, timestamp);
            return;
        }

        if (string.Equals(pendingText, _bufferText, StringComparison.Ordinal))
        {
            return;
        }

        if (pendingText.Contains(_bufferText, StringComparison.Ordinal))
        {
            _bufferText = pendingText;
            _bufferUpdatedAt = timestamp;
            return;
        }

        if (_bufferText.Contains(pendingText, StringComparison.Ordinal))
        {
            _bufferText = pendingText;
            _bufferUpdatedAt = timestamp;
            return;
        }

        SubmitBuffer(source, timestamp, submitted);
        StartBuffer(pendingText, timestamp);
    }

    private void StartBuffer(string text, DateTimeOffset timestamp)
    {
        _bufferText = text;
        _bufferCreatedAt = timestamp;
        _bufferUpdatedAt = timestamp;
    }

    private void SubmitReadyBoundarySegments(
        string source,
        DateTimeOffset timestamp,
        List<CaptionSegment> submitted)
    {
        while (_bufferText.Length > 0)
        {
            var boundary = FindFirstBoundary(_bufferText);
            if (boundary is null)
            {
                return;
            }

            var (index, includeBoundary) = boundary.Value;
            var segmentEnd = includeBoundary ? index + 1 : index;
            var segmentText = _bufferText[..segmentEnd].Trim();
            var remainingText = _bufferText[(index + 1)..].Trim();

            SubmitText(segmentText, source, _bufferCreatedAt, timestamp, submitted);

            _bufferText = remainingText;
            _bufferCreatedAt = timestamp;
            _bufferUpdatedAt = timestamp;
        }
    }

    private void SubmitBuffer(
        string source,
        DateTimeOffset timestamp,
        List<CaptionSegment> submitted)
    {
        var segmentText = _bufferText.Trim();
        SubmitText(segmentText, source, _bufferCreatedAt, timestamp, submitted);

        _bufferText = string.Empty;
        _bufferCreatedAt = timestamp;
        _bufferUpdatedAt = timestamp;
    }

    private void SubmitText(
        string text,
        string source,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        List<CaptionSegment> submitted)
    {
        var normalized = _normalizer.Normalize(text);
        if (normalized.Length < MinSegmentLength)
        {
            return;
        }

        if (_submittedKeys.Contains(normalized))
        {
            return;
        }

        var segment = new CaptionSegment
        {
            Text = normalized,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            IsFinal = true,
            Source = source
        };

        _submittedKeys.Add(normalized);
        _submittedTexts.Add(normalized);
        submitted.Add(segment);
    }

    private string RemoveSubmittedText(string text)
    {
        var pending = text.Trim();
        var changed = true;

        while (changed && pending.Length > 0)
        {
            changed = false;

            for (var i = _submittedTexts.Count - 1; i >= 0; i--)
            {
                var submittedText = _submittedTexts[i];
                if (string.Equals(pending, submittedText, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                if (pending.StartsWith(submittedText, StringComparison.Ordinal))
                {
                    pending = TrimSegmentSeparator(pending[submittedText.Length..]);
                    changed = true;
                    break;
                }

                var index = pending.IndexOf(submittedText, StringComparison.Ordinal);
                if (index >= 0 && index + submittedText.Length < pending.Length)
                {
                    pending = TrimSegmentSeparator(pending[(index + submittedText.Length)..]);
                    changed = true;
                    break;
                }
            }
        }

        return pending;
    }

    private static (int Index, bool IncludeBoundary)? FindFirstBoundary(string text)
    {
        var punctuationIndex = text.IndexOfAny(FinalPunctuation);
        var newlineIndex = text.IndexOf('\n');

        if (punctuationIndex < 0 && newlineIndex < 0)
        {
            return null;
        }

        if (punctuationIndex >= 0 && (newlineIndex < 0 || punctuationIndex < newlineIndex))
        {
            return (punctuationIndex, true);
        }

        return (newlineIndex, false);
    }

    private static string TrimSegmentSeparator(string text)
    {
        return text.TrimStart(' ', '\t', '\n', '.', '?', '!', '。', '？', '！', ',', '，', ';', '；', ':', '：');
    }

    private SubtitleDeduplicationResult CreateResult(IReadOnlyList<CaptionSegment> submitted)
    {
        return new SubtitleDeduplicationResult(_lastRealtimeText, _bufferText, submitted);
    }
}
