namespace LiveCaptionTranslator.App.Models;

public sealed record TranslationResult
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string SourceText { get; init; } = string.Empty;

    public string TranslatedText { get; init; } = string.Empty;

    public string SourceLanguage { get; init; } = "auto";

    public string TargetLanguage { get; init; } = "zho_Hans";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public DateTimeOffset? CompletedAt { get; init; }

    public string Status { get; init; } = "Pending";

    public string? ErrorMessage { get; init; }
}
