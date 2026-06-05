namespace LiveCaptionTranslator.App.Models;

public sealed record CaptionSegment
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Text { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public bool IsFinal { get; init; }

    public string Source { get; init; } = string.Empty;
}
