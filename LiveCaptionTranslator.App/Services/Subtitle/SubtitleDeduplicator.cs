using System.Text;
using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Subtitle;

public sealed class SubtitleDeduplicator
{
    private static readonly char[] SentenceEndPunctuation =
    [
        '.',
        '?',
        '!',
        '。',
        '？',
        '！'
    ];

    private readonly SubtitleNormalizer _normalizer;
    private readonly List<CaptionSegment> _finalSegments = [];
    private string _lastRealtimeText = string.Empty;
    private string _pendingText = string.Empty;
    private DateTimeOffset _pendingCreatedAt = DateTimeOffset.Now;
    private DateTimeOffset _pendingUpdatedAt = DateTimeOffset.Now;

    public SubtitleDeduplicator()
        : this(new SubtitleNormalizer())
    {
    }

    public SubtitleDeduplicator(SubtitleNormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    public int StableDelayMs { get; init; } = 1800;

    public int HardFinalDelayMs { get; init; } = 3500;

    public int MaxBufferLength { get; init; } = 600;

    public int MinSegmentLength { get; init; } = 8;

    public double SimilarityThreshold { get; init; } = 0.72;

    public int SentenceEndHoldMs { get; init; } = 1200;

    public string RealtimeText => _lastRealtimeText;

    public string BufferText => _pendingText;

    public bool IsSameUtterance(string oldText, string newText)
    {
        return AreSameUtterance(oldText, newText, SimilarityThreshold);
    }

    public static bool AreSameUtterance(string oldText, string newText, double similarityThreshold = 0.72)
    {
        var oldNormalized = NormalizeForComparison(oldText);
        var newNormalized = NormalizeForComparison(newText);

        if (oldNormalized.Length == 0 || newNormalized.Length == 0)
        {
            return false;
        }

        if (string.Equals(oldNormalized, newNormalized, StringComparison.Ordinal))
        {
            return true;
        }

        if (newNormalized.Contains(oldNormalized, StringComparison.Ordinal) ||
            oldNormalized.Contains(newNormalized, StringComparison.Ordinal))
        {
            return true;
        }

        var prefixRatio = LongestCommonPrefixRatio(oldNormalized, newNormalized);
        if (prefixRatio >= 0.68 && LongestCommonPrefixLength(oldNormalized, newNormalized) >= 12)
        {
            return true;
        }

        return WordJaccardSimilarity(oldNormalized, newNormalized) > similarityThreshold;
    }

    public static bool IsMoreCompleteUtterance(string candidateText, string existingText)
    {
        var candidate = NormalizeForComparison(candidateText);
        var existing = NormalizeForComparison(existingText);
        return candidate.Length > existing.Length;
    }

    public SubtitleDeduplicationResult Push(
        string? rawText,
        string source = "LiveCaptions",
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.Now;
        var normalized = _normalizer.Normalize(rawText);
        if (string.Equals(normalized, _lastRealtimeText, StringComparison.Ordinal))
        {
            return CreateResult([], [], []);
        }

        _lastRealtimeText = normalized;
        if (normalized.Length == 0)
        {
            return CreateResult([], [], []);
        }

        var submitted = new List<CaptionSegment>();
        var replaced = new List<CaptionSegment>();
        var logs = new List<string>();
        var candidates = ExtractTranscriptCandidates(normalized);
        if (candidates.Count == 0)
        {
            return CreateResult(submitted, replaced, logs);
        }

        foreach (var candidate in candidates)
        {
            ProcessCandidate(candidate, source, now, submitted, replaced, logs);
        }

        return CreateResult(submitted, replaced, logs);
    }

    public SubtitleDeduplicationResult Tick(
        string source = "LiveCaptions",
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.Now;
        var submitted = new List<CaptionSegment>();
        var replaced = new List<CaptionSegment>();
        var logs = new List<string>();

        if (ShouldSubmitPending(now))
        {
            var oldPending = _pendingText;
            var submittedFinal = SubmitPending(source, now, submitted, replaced);
            logs.Add(CreateDecisionLog(oldPending, oldPending, true, false, submittedFinal, replaced.Count > 0));
        }

        return CreateResult(submitted, replaced, logs);
    }

    public void Reset()
    {
        _lastRealtimeText = string.Empty;
        _pendingText = string.Empty;
        _pendingCreatedAt = DateTimeOffset.Now;
        _pendingUpdatedAt = _pendingCreatedAt;
        _finalSegments.Clear();
    }

    private bool UpdateSamePending(string newText, DateTimeOffset timestamp)
    {
        _pendingUpdatedAt = timestamp;

        if (IsMoreCompleteUtterance(newText, _pendingText))
        {
            _pendingText = newText;
            return true;
        }

        return false;
    }

    private void StartPending(string text, DateTimeOffset timestamp)
    {
        _pendingText = text;
        _pendingCreatedAt = timestamp;
        _pendingUpdatedAt = timestamp;
    }

    private void ProcessCandidate(
        TranscriptCandidate candidate,
        string source,
        DateTimeOffset timestamp,
        List<CaptionSegment> submitted,
        List<CaptionSegment> replaced,
        List<string> logs)
    {
        var newText = RemoveExactFinalPrefixes(candidate.Text);
        if (newText.Length == 0)
        {
            return;
        }

        var oldPending = _pendingText;
        var replacedBefore = replaced.Count;
        ReplaceSupersededFinalSegments(newText, replaced, logs);

        var isSameUtterance = _pendingText.Length > 0 && IsSameUtterance(_pendingText, newText);
        var updatedPending = false;
        var submittedFinal = false;

        if (candidate.HasFollowingText && candidate.IsComplete)
        {
            if (_pendingText.Length > 0 && isSameUtterance)
            {
                _pendingText = string.Empty;
                _pendingCreatedAt = timestamp;
                _pendingUpdatedAt = timestamp;
            }
            else if (_pendingText.Length > 0)
            {
                submittedFinal = SubmitPending(source, timestamp, submitted, replaced);
            }

            submittedFinal = SubmitText(newText, source, timestamp, timestamp, submitted, replaced) || submittedFinal;
        }
        else if (_pendingText.Length == 0)
        {
            StartPending(newText, timestamp);
            updatedPending = true;
        }
        else if (isSameUtterance)
        {
            updatedPending = UpdateSamePending(newText, timestamp);
        }
        else
        {
            submittedFinal = SubmitPending(source, timestamp, submitted, replaced);
            StartPending(newText, timestamp);
            updatedPending = true;
        }

        if (_pendingText.Length >= MaxBufferLength)
        {
            submittedFinal = SubmitPending(source, timestamp, submitted, replaced) || submittedFinal;
        }

        logs.Add(CreateDecisionLog(
            oldPending,
            newText,
            isSameUtterance,
            updatedPending,
            submittedFinal,
            replaced.Count > replacedBefore));
    }

    private bool ShouldSubmitPending(DateTimeOffset timestamp)
    {
        if (_pendingText.Length < MinSegmentLength)
        {
            return false;
        }

        var unchangedFor = timestamp - _pendingUpdatedAt;
        var pendingAge = timestamp - _pendingCreatedAt;
        if (HasSentenceEndPunctuation(_pendingText) &&
            unchangedFor >= TimeSpan.FromMilliseconds(SentenceEndHoldMs))
        {
            return true;
        }

        if (unchangedFor >= TimeSpan.FromMilliseconds(StableDelayMs))
        {
            return true;
        }

        return pendingAge >= TimeSpan.FromMilliseconds(HardFinalDelayMs) &&
               unchangedFor >= TimeSpan.FromMilliseconds(SentenceEndHoldMs);
    }

    private bool SubmitPending(
        string source,
        DateTimeOffset timestamp,
        List<CaptionSegment> submitted,
        List<CaptionSegment> replaced)
    {
        var text = _pendingText.Trim();
        var wasSubmitted = SubmitText(text, source, _pendingCreatedAt, timestamp, submitted, replaced);

        _pendingText = string.Empty;
        _pendingCreatedAt = timestamp;
        _pendingUpdatedAt = timestamp;

        return wasSubmitted;
    }

    private bool SubmitText(
        string text,
        string source,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        List<CaptionSegment> submitted,
        List<CaptionSegment> replaced)
    {
        var normalized = _normalizer.Normalize(text);
        if (normalized.Length < MinSegmentLength)
        {
            return false;
        }

        var sameFinals = _finalSegments
            .Where(segment => IsSameUtterance(segment.Text, normalized))
            .ToList();

        if (sameFinals.Any(segment => string.Equals(segment.Text, normalized, StringComparison.Ordinal)))
        {
            return false;
        }

        if (sameFinals.Any(segment => !IsMoreCompleteUtterance(normalized, segment.Text)))
        {
            return false;
        }

        foreach (var oldSegment in sameFinals)
        {
            _finalSegments.Remove(oldSegment);
            replaced.Add(oldSegment);
        }

        var segment = new CaptionSegment
        {
            Text = normalized,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            IsFinal = true,
            Source = source
        };

        _finalSegments.Add(segment);
        submitted.Add(segment);
        return true;
    }

    private string RemoveExactFinalPrefixes(string text)
    {
        var pending = text.Trim();
        var changed = true;

        while (changed && pending.Length > 0)
        {
            changed = false;

            for (var i = _finalSegments.Count - 1; i >= 0; i--)
            {
                var finalText = _finalSegments[i].Text;
                if (string.Equals(pending, finalText, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                if (pending.StartsWith(finalText, StringComparison.Ordinal))
                {
                    pending = TrimSegmentSeparator(pending[finalText.Length..]);
                    changed = true;
                    break;
                }
            }
        }

        return pending;
    }

    private void ReplaceSupersededFinalSegments(
        string newText,
        List<CaptionSegment> replaced,
        List<string> logs)
    {
        var superseded = _finalSegments
            .Where(segment => IsSameUtterance(segment.Text, newText) &&
                              IsMoreCompleteUtterance(newText, segment.Text))
            .ToList();

        foreach (var segment in superseded)
        {
            _finalSegments.Remove(segment);
            replaced.Add(segment);
            logs.Add($"替换旧 FinalSegment：{segment.Text}");
        }
    }

    private static bool HasSentenceEndPunctuation(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && SentenceEndPunctuation.Contains(trimmed[^1]);
    }

    private static string TrimSegmentSeparator(string text)
    {
        return text.TrimStart(' ', '\t', '\n', '.', '?', '!', '。', '？', '！', ',', '，', '、', ';', '；', ':', '：');
    }

    private static List<TranscriptCandidate> ExtractTranscriptCandidates(string transcript)
    {
        var candidates = new List<TranscriptCandidate>();
        var start = 0;

        for (var index = 0; index < transcript.Length; index++)
        {
            if (!SentenceEndPunctuation.Contains(transcript[index]))
            {
                continue;
            }

            AddCandidate(transcript[start..(index + 1)], isComplete: true);
            start = index + 1;
        }

        if (start < transcript.Length)
        {
            AddCandidate(transcript[start..], isComplete: false);
        }

        for (var index = 0; index < candidates.Count; index++)
        {
            candidates[index] = candidates[index] with
            {
                HasFollowingText = index < candidates.Count - 1
            };
        }

        return candidates;

        void AddCandidate(string text, bool isComplete)
        {
            var normalized = text.Trim();
            if (normalized.Length == 0)
            {
                return;
            }

            candidates.Add(new TranscriptCandidate(normalized, isComplete, HasFollowingText: false));
        }
    }

    private static string CreateDecisionLog(
        string oldPending,
        string newText,
        bool isSameUtterance,
        bool updatedPending,
        bool submittedFinal,
        bool replacedFinal)
    {
        return
            $"Dedup判断 | old pending: {FormatLogText(oldPending)} | new text: {FormatLogText(newText)} | " +
            $"IsSameUtterance: {isSameUtterance} | 更新PendingSegment: {updatedPending} | " +
            $"提交FinalSegment: {submittedFinal} | 替换旧FinalSegment: {replacedFinal}";
    }

    private static string FormatLogText(string text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? "<empty>"
            : text.Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static string NormalizeForComparison(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasSpace = false;

        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSpace = false;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    previousWasSpace = true;
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static int LongestCommonPrefixLength(string oldText, string newText)
    {
        var maxLength = Math.Min(oldText.Length, newText.Length);
        var index = 0;
        while (index < maxLength && oldText[index] == newText[index])
        {
            index++;
        }

        return index;
    }

    private static double LongestCommonPrefixRatio(string oldText, string newText)
    {
        var minLength = Math.Min(oldText.Length, newText.Length);
        return minLength == 0 ? 0 : (double)LongestCommonPrefixLength(oldText, newText) / minLength;
    }

    private static double WordJaccardSimilarity(string oldText, string newText)
    {
        var oldTokens = Tokenize(oldText);
        var newTokens = Tokenize(newText);
        if (oldTokens.Count == 0 || newTokens.Count == 0)
        {
            return 0;
        }

        var intersection = oldTokens.Intersect(newTokens, StringComparer.Ordinal).Count();
        var union = oldTokens.Union(newTokens, StringComparer.Ordinal).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsCjkOrKana(ch))
            {
                FlushWord();
                tokens.Add(ch.ToString());
            }
            else if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
            }
            else
            {
                FlushWord();
            }
        }

        FlushWord();
        return tokens;

        void FlushWord()
        {
            if (word.Length == 0)
            {
                return;
            }

            tokens.Add(word.ToString());
            word.Clear();
        }
    }

    private static bool IsCjkOrKana(char ch)
    {
        return ch is >= '\u3040' and <= '\u30ff' or
               >= '\u3400' and <= '\u4dbf' or
               >= '\u4e00' and <= '\u9fff' or
               >= '\uf900' and <= '\ufaff';
    }

    private SubtitleDeduplicationResult CreateResult(
        IReadOnlyList<CaptionSegment> submitted,
        IReadOnlyList<CaptionSegment> replaced,
        IReadOnlyList<string> logEntries)
    {
        return new SubtitleDeduplicationResult(_lastRealtimeText, _pendingText, submitted, replaced, logEntries);
    }

    private sealed record TranscriptCandidate(string Text, bool IsComplete, bool HasFollowingText);
}
