using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Triviela.Providers;

public sealed class ClaudeLlmProvider(
    HttpClient http,
    IOptions<ClaudeOptions> options,
    LlmCostMeter costs,
    ILogger<ClaudeLlmProvider> logger) : ILlmProvider
{
    private readonly ClaudeOptions _opts = options.Value;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "claude";
    public bool IsEnabled => _opts.IsConfigured;
    public bool SupportsWebSearch => true;

    public async Task<LlmResponse?> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (!IsEnabled) return null;

        var system = request.Json
            ? request.System + "\nOutput ONLY valid JSON — no prose, no markdown fences."
            : request.System;

        object body = request.WebSearch
            ? new
            {
                model = _opts.Model,
                max_tokens = request.MaxTokens,
                system,
                tools = new[] { new { type = "web_search_20250305", name = "web_search", max_uses = Math.Max(1, request.WebSearchMaxUses) } },
                messages = new[] { new { role = "user", content = request.Prompt } }
            }
            : new
            {
                model = _opts.Model,
                max_tokens = request.MaxTokens,
                system,
                messages = new[] { new { role = "user", content = request.Prompt } }
            };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
            {
                Content = JsonContent.Create(body, options: JsonOpts)
            };
            req.Headers.Add("x-api-key", _opts.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Claude returned {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            RecordCost(doc.RootElement);

            if (!doc.RootElement.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return null;

            var blocks = content.EnumerateArray().ToArray();
            int lastSearch = -1;
            string? sourceUrl = null;
            for (int i = 0; i < blocks.Length; i++)
            {
                var type = blocks[i].TryGetProperty("type", out var bt) ? bt.GetString() : null;
                if (type == "web_search_tool_result")
                {
                    lastSearch = i;
                    sourceUrl ??= FirstResultUrl(blocks[i]);
                }
            }

            var sb = new StringBuilder();
            for (int i = lastSearch + 1; i < blocks.Length; i++)
                if (blocks[i].TryGetProperty("type", out var bt) && bt.GetString() == "text" && blocks[i].TryGetProperty("text", out var tx))
                    sb.Append(tx.GetString());

            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : new LlmResponse(text, sourceUrl);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Claude call timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Claude call failed");
            return null;
        }
    }

    private void RecordCost(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        long input = ReadLong(u, "input_tokens");
        long cached = ReadLong(u, "cache_read_input_tokens");
        long output = ReadLong(u, "output_tokens");
        int searches = 0;
        if (u.TryGetProperty("server_tool_use", out var st) && st.ValueKind == JsonValueKind.Object)
            searches = (int)ReadLong(st, "web_search_requests");

        var cost = input / 1_000_000m * _opts.InputUsdPerMillion
                 + cached / 1_000_000m * _opts.CachedInputUsdPerMillion
                 + output / 1_000_000m * _opts.OutputUsdPerMillion
                 + searches * _opts.UsdPerWebSearch;
        costs.RecordCost(cost);
    }

    private static long ReadLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string? FirstResultUrl(JsonElement searchResultBlock)
    {
        if (!searchResultBlock.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in content.EnumerateArray())
            if (r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
        return null;
    }
}
