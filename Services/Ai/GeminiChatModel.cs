using System.Text;
using System.Text.Json;
using PortfolioApi.Common;
using PortfolioApi.Interfaces;

namespace PortfolioApi.Services.Ai;

/// <summary>
/// Google Gemini (generativelanguage.googleapis.com) behind <see cref="IChatModel"/>.
/// Uses the native <c>:generateContent</c> endpoint with a <c>systemInstruction</c>, so
/// a persona is enforced server-side and never travels in the user turn.
///
/// Config (environment variables shown in double-underscore form):
///   AI__GeminiApiKey  — Google AI Studio API key (required; calls 503 without it)
///   AI__Model         — model id, default "gemini-3.6-flash" (a GA model — older
///                       ids like gemini-1.5-pro / gemini-2.0-flash are RETIRED and 404)
///   AI__BaseUrl       — default "https://generativelanguage.googleapis.com/v1beta"
///
/// Back-compat: if AI__GeminiApiKey is unset it falls back to AI__OpenAiApiKey, so an
/// existing deployment's secret name keeps working.
/// </summary>
public sealed class GeminiChatModel : IChatModel
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GeminiChatModel> _log;

    public GeminiChatModel(HttpClient http, IConfiguration cfg, ILogger<GeminiChatModel> log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string ModelId => string.IsNullOrWhiteSpace(_cfg["AI:Model"]) ? "gemini-3.6-flash" : _cfg["AI:Model"]!;

    private string BaseUrl => (string.IsNullOrWhiteSpace(_cfg["AI:BaseUrl"])
        ? "https://generativelanguage.googleapis.com/v1beta"
        : _cfg["AI:BaseUrl"]!).TrimEnd('/');

    public async Task<ApiResponse<string>> CompleteAsync(
        string systemPrompt, string userPrompt, ChatOptions? options = null, CancellationToken ct = default)
    {
        var (apiKey, keySource, error) = ResolveKey();
        if (error is not null) return ApiResponse<string>.Fail(error, 503);

        var opts = options ?? new ChatOptions();
        var model = ModelId;
        var url = $"{BaseUrl}/models/{model}:generateContent";

        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = userPrompt } } } },
            generationConfig = new
            {
                temperature = opts.Temperature,
                topP = opts.TopP,
                maxOutputTokens = opts.MaxOutputTokens,
            },
        };

        using var msg = new HttpRequestMessage(HttpMethod.Post, url);
        msg.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        msg.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var status = (int)resp.StatusCode;
                // Mask the key (first 6 + length) so the exact reason is diagnosable in
                // production WITHOUT leaking the secret. Google's body carries the precise
                // reason, e.g. "API_KEY_INVALID" or "PERMISSION_DENIED".
                var masked = apiKey!.Length <= 8 ? "******" : $"{apiKey[..6]}…(len {apiKey.Length})";
                _log.LogError(
                    "Gemini call FAILED {Status}. key={Source}={Masked}, model={Model}, url={Url}. Provider response: {Body}",
                    status, keySource, masked, model, url, Truncate(body, 1000));

                return ApiResponse<string>.Fail(status switch
                {
                    400 => "AI provider rejected the request (check the model name / API key format).",
                    401 or 403 => "AI provider rejected the API key. See the server log for Google's exact reason (invalid key, restricted key, or the Generative Language API not enabled for this project).",
                    404 => $"AI model '{model}' was not found for this API key. Set AI__Model to a model your key can access.",
                    429 => "AI provider is rate-limiting requests. Please try again shortly.",
                    _ => $"AI provider error ({status}).",
                }, 502);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // A safety filter can block the prompt before any candidate is produced.
            if (root.TryGetProperty("promptFeedback", out var pf) && pf.TryGetProperty("blockReason", out var br))
            {
                _log.LogWarning("Gemini blocked the prompt: {Reason}", br.GetString());
                return ApiResponse<string>.Fail(
                    "The AI declined to answer this request. Please adjust the input and try again.", 502);
            }

            var text = ExtractText(root);
            return string.IsNullOrWhiteSpace(text)
                ? ApiResponse<string>.Fail("The AI returned an empty response.", 502)
                : ApiResponse<string>.Ok(text.Trim(), "OK");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ApiResponse<string>.Fail("The AI request timed out. Please try again.", 504);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gemini completion failed.");
            return ApiResponse<string>.Fail("Could not reach the AI provider. Please try again later.", 502);
        }
    }

    public async Task<ApiResponse<object>> CheckHealthAsync(CancellationToken ct = default)
    {
        var (apiKey, keySource, error) = ResolveKey();
        if (error is not null) return ApiResponse<object>.Fail(error, 503);

        var model = ModelId;
        var masked = apiKey!.Length <= 8 ? "******" : $"{apiKey[..6]}…(len {apiKey.Length})";

        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models?pageSize=200");
            msg.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            using var resp = await _http.SendAsync(msg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError("ai-health: Gemini ListModels {Status}. key={Source}={Masked}. Provider response: {Body}",
                    (int)resp.StatusCode, keySource, masked, Truncate(body, 600));
                return ApiResponse<object>.Ok(new
                {
                    ok = false, provider = "gemini", keySource, keyMasked = masked, model,
                    status = (int)resp.StatusCode,
                    reason = Truncate(body, 500),
                }, "AI key check FAILED — see reason.");
            }

            var names = new List<string>();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var arr))
                foreach (var m in arr.EnumerateArray())
                    if (m.TryGetProperty("name", out var n))
                        names.Add((n.GetString() ?? string.Empty).Replace("models/", string.Empty));

            var modelOk = names.Any(x => x.Equals(model, StringComparison.OrdinalIgnoreCase));
            return ApiResponse<object>.Ok(new
            {
                ok = true, provider = "gemini", keySource, keyMasked = masked,
                model, modelAvailable = modelOk,
                usableModels = names.Where(x => x.Contains("flash") || x.Contains("pro")).OrderBy(x => x).Take(25).ToArray(),
                message = modelOk
                    ? "Key valid and the configured model is available."
                    : $"Key VALID, but model '{model}' is NOT in this account's list — set AI__Model to one of usableModels.",
            }, "OK");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ai-health: Gemini check threw.");
            return ApiResponse<object>.Fail($"AI health check error: {ex.Message}", 502);
        }
    }

    /// <summary>Resolves the API key and reports which env var it came from.</summary>
    private (string? Key, string Source, string? Error) ResolveKey()
    {
        var rawKey = _cfg["AI:GeminiApiKey"];
        var keySource = "AI__GeminiApiKey";
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            rawKey = _cfg["AI:OpenAiApiKey"];               // back-compat secret name
            keySource = "AI__OpenAiApiKey (fallback)";
        }
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            _log.LogWarning("AI requested but neither AI__GeminiApiKey nor AI__OpenAiApiKey is configured on this service.");
            return (null, keySource, "AI reading is not configured on the server yet.");
        }

        // Trim surrounding whitespace/newlines — the #1 cause of a spurious 401/403 is a
        // stray "\n" pasted into the hosting provider's env editor.
        var apiKey = rawKey.Trim();
        if (apiKey.Length != rawKey.Length)
            _log.LogWarning("Gemini API key from {Source} had surrounding whitespace/newline — trimmed.", keySource);

        // A Google AI Studio key starts with "AIza". An OpenAI key ("sk-…") passed to
        // Google is rejected with 401/403 — catch that misconfiguration explicitly.
        if (apiKey.StartsWith("sk-", StringComparison.OrdinalIgnoreCase))
            _log.LogError("The key from {Source} looks like an OpenAI key (sk-…), NOT a Google AI key (AIza…). Set AI__GeminiApiKey to a Google AI Studio key.", keySource);
        else if (!apiKey.StartsWith("AIza", StringComparison.Ordinal))
            _log.LogWarning("The key from {Source} does not start with the expected Google prefix 'AIza'.", keySource);

        return (apiKey, keySource, null);
    }

    /// <summary>Concatenate all text parts of the first candidate's content.</summary>
    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var cands)
            || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            return string.Empty;

        var first = cands[0];
        if (!first.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
            if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                sb.Append(t.GetString());
        return sb.ToString();
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s[..n];
}
