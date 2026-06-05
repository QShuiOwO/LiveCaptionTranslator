using LiveCaptionTranslator.App.Models;

namespace LiveCaptionTranslator.App.Services.Translation;

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}
