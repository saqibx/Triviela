using System.Text.RegularExpressions;

namespace Triviela.Providers;

public static class LlmText
{
    private static readonly Regex AnsiEscapes = new(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b[@-_]|\x1b", RegexOptions.Compiled);

    private static readonly Regex ControlChars = new(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.Compiled);

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = AnsiEscapes.Replace(text, "");
        s = ControlChars.Replace(s, "");
        return s;
    }
}

public sealed record LlmRequest(
    string System,
    string Prompt,
    int MaxTokens,
    bool Json = false,
    bool WebSearch = false,
    int WebSearchMaxUses = 1);

public sealed record LlmResponse(string Text, string? SourceUrl = null);

public interface ILlmProvider
{
    string Name { get; }

    bool IsEnabled { get; }

    bool SupportsWebSearch { get; }

    Task<LlmResponse?> CompleteAsync(LlmRequest request, CancellationToken ct);
}
