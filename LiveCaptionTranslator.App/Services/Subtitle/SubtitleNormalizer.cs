using System.Text;

namespace LiveCaptionTranslator.App.Services.Subtitle;

public sealed class SubtitleNormalizer
{
    public string Normalize(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var unified = rawText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        if (unified.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(unified.Length);
        var lines = unified.Split('\n');
        var previousLineWasBlank = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var isBlank = line.Length == 0;

            if (isBlank && previousLineWasBlank)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(line);
            previousLineWasBlank = isBlank;
        }

        return builder.ToString().Trim();
    }
}
