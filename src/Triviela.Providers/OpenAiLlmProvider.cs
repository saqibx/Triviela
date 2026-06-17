using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Triviela.Providers;

public sealed class OpenAiLlmProvider(
    HttpClient http,
    IOptions<OpenAiOptions> options,
    LlmCostMeter costs,
    ILogger<OpenAiLlmProvider> logger) : ILlmProvider
{
    private readonly OpenAiOptions _opts = options.Value;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "openai";
    public bool IsEnabled => _opts.IsConfigured;
    public bool SupportsWebSearch => false;

    public async Task<LlmResponse?> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        if (!IsEnabled) return null;
        if (request.WebSearch) return null;

        object body = request.Json
            ? new
            {
                model = _opts.Model,
                max_completion_tokens = request.MaxTokens,
                response_format = new { type = "json_object" },
                messages = new[]
                {
                    new { role = "system", content = request.System },
                    new { role = "user", content = request.Prompt }
                }
            }
            : new
            {
                model = _opts.Model,
                max_completion_tokens = request.MaxTokens,
                messages = new[]
                {
                    new { role = "system", content = request.System },
                    new { role = "user", content = request.Prompt }
                }
            };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
            {
                Content = JsonContent.Create(body, options: JsonOpts)
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opts.ApiKey);

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("OpenAI returned {Status}", (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            RecordCost(doc.RootElement);

            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            var msg = choices[0].TryGetProperty("message", out var m) ? m : default;
            var text = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c) ? c.GetString() : null;
            return string.IsNullOrWhiteSpace(text) ? null : new LlmResponse(text!.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("OpenAI call timed out");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OpenAI call failed");
            return null;
        }
    }

    private void RecordCost(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;
        long input = ReadLong(u, "prompt_tokens");
        long output = ReadLong(u, "completion_tokens");

        long cached = 0;
        if (u.TryGetProperty("prompt_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object)
            cached = ReadLong(d, "cached_tokens");
        var uncached = Math.Max(0, input - cached);

        var cost = uncached / 1_000_000m * _opts.InputUsdPerMillion
                 + cached / 1_000_000m * _opts.CachedInputUsdPerMillion
                 + output / 1_000_000m * _opts.OutputUsdPerMillion;
        costs.RecordCost(cost);
    }

    private static long ReadLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
