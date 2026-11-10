using System.Net.Http.Headers;
using System.Text.Json;

namespace ReviewPR.Api.Services;

// Fixed upstream endpoints prevent user-supplied URLs and SSRF. Model-list calls
// validate a key against the provider; they do not execute billable inference.
public sealed class ByokProviderModelsService(IHttpClientFactory factory)
{
    public sealed record ProviderInfo(string Id, string Name, bool RequiresKey,
        bool ModelDiscoverySupported);
    public sealed record ModelListResult(bool Success, int UpstreamStatus,
        string[] Models, bool Truncated);

    public static readonly ProviderInfo[] PublicProviders =
    [
        new("mock", "Mock (local test)", false, true),
        new("openai", "OpenAI", true, true),
        new("gemini", "Google Gemini", true, true),
        new("anthropic", "Anthropic Claude", true, true),
        new("groq", "Groq", true, true),
        new("openrouter", "OpenRouter", true, true),
        new("compatible", "OpenAI-compatible (not connected)", true, false)
    ];

    public static bool IsSupported(string provider) =>
        provider is "openai" or "gemini" or "anthropic" or "groq" or "openrouter";

    private static string? Endpoint(string provider) => provider switch
    {
        "openai" => "https://api.openai.com/v1/models",
        "gemini" => "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100",
        "anthropic" => "https://api.anthropic.com/v1/models?limit=100",
        "groq" => "https://api.groq.com/openai/v1/models",
        "openrouter" => "https://openrouter.ai/api/v1/models",
        _ => null
    };

    public async Task<ModelListResult> ListAsync(string provider, string apiKey, CancellationToken ct)
    {
        // Strongly restricted fixed endpoint set; no custom base URL from browser.
        string url = Endpoint(provider) ?? throw new ArgumentException("Unsupported provider.");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 4096 ||
            apiKey.Contains('\r') || apiKey.Contains('\n'))
            throw new ArgumentException("Invalid provider credential.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (provider == "gemini") request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (provider == "anthropic")
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        using var response = await factory.CreateClient("ByokProviderModels")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        int status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
            return new ModelListResult(false, status, [], false);
        if (response.Content.Headers.ContentLength is > 4_000_000)
            throw new InvalidDataException("Model catalog too large.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + count > 4_000_000)
                throw new InvalidDataException("Model catalog exceeded maximum size.");
            bounded.Write(buffer, 0, count);
        }
        bounded.Position = 0;
        using var json = await JsonDocument.ParseAsync(bounded, cancellationToken: ct);
        string arrayName = provider == "gemini" ? "models" : "data";
        if (!json.RootElement.TryGetProperty(arrayName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Model catalog schema was invalid.");
        var names = new List<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string property = provider == "gemini" ? "name" : "id";
            if (!entry.TryGetProperty(property, out var id) ||
                id.ValueKind != JsonValueKind.String) continue;
            string? name = id.GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (provider == "gemini" && name.StartsWith("models/", StringComparison.Ordinal))
                name = name[7..];
            if (name.Length > 100 || names.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;
            names.Add(name);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        bool truncated = names.Count > 200;
        return new ModelListResult(true, status, names.Take(200).ToArray(), truncated);
    }
}
