using System.Net.Http.Headers;
using System.Text.Json;

namespace ReviewPR.Api.Services;

public sealed class ByokProviderModelsService(IHttpClientFactory factory)
{
    public sealed record ProviderInfo(
        string Id, string Name, bool RequiresKey,
        bool ModelDiscoverySupported);

    public sealed record ModelListResult(
        bool Success, int UpstreamStatus,
        string[] Models, bool Truncated);

    private const int PageSize = 1000;
    private const int MaxPages = 100;
    private const int MaxModels = 10_000;
    private const int MaxPageBytes = 4_000_000;
    private const int MaxTotalBytes = 20_000_000;

    public static readonly ProviderInfo[] PublicProviders =
    [
        new("mock", "Mock (local test)", false, true),
        new("openai", "OpenAI", true, true),
        new("gemini", "Google Gemini", true, true),
        new("anthropic", "Anthropic Claude", true, true),
        new("groq", "Groq", true, true),
        new("openrouter", "OpenRouter", true, true)
    ];

    public static bool IsSupported(string provider) =>
        provider is "openai" or "gemini" or "anthropic"
            or "groq" or "openrouter";

    public static string ProviderName(string provider) => provider switch
    {
        "openai" => "OpenAI",
        "gemini" => "Google Gemini",
        "anthropic" => "Anthropic Claude",
        "groq" => "Groq",
        "openrouter" => "OpenRouter",
        _ => "Unknown provider"
    };

    public static string? ReviewBaseUrl(string provider) => provider switch
    {
        "groq" => "https://api.groq.com/openai/v1",
        "openrouter" => "https://openrouter.ai/api/v1",
        _ => null
    };

    private static string? Endpoint(string provider) => provider switch
    {
        "openai" => "https://api.openai.com/v1/models",
        "gemini" => "https://generativelanguage.googleapis.com/v1beta/models",
        "anthropic" => "https://api.anthropic.com/v1/models",
        "groq" => "https://api.groq.com/openai/v1/models",
        "openrouter" => "https://openrouter.ai/api/v1/models",
        _ => null
    };

    public async Task<ModelListResult> ListAsync(
        string provider, string apiKey, CancellationToken ct)
    {
        return await ListAsync(
            factory.CreateClient("ByokProviderModels"),
            provider, apiKey, ct);
    }

    public static async Task<ModelListResult> ListAsync(
        HttpClient client, string provider,
        string apiKey, CancellationToken ct)
    {
        string endpoint = Endpoint(provider)
            ?? throw new ArgumentException("Unsupported AI provider.");

        if (string.IsNullOrWhiteSpace(apiKey) ||
            apiKey.Length > 4096 ||
            apiKey.Contains('\r') ||
            apiKey.Contains('\n'))
        {
            throw new ArgumentException("Invalid provider API key.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        int totalBytes = 0;
        int lastStatus = 200;
        bool hasMore = true;
        bool truncated = false;
        string? cursor = null;

        for (int page = 0; page < MaxPages && hasMore; page++)
        {
            ct.ThrowIfCancellationRequested();

            string url = BuildPageUrl(provider, endpoint, cursor, page);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            if (provider == "gemini")
            {
                request.Headers.TryAddWithoutValidation(
                    "x-goog-api-key", apiKey);
            }
            else if (provider == "anthropic")
            {
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation(
                    "anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            lastStatus = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
                return new ModelListResult(false, lastStatus, [], false);

            byte[] body = await ReadBoundedResponseAsync(
                response, MaxPageBytes, ct);

            totalBytes += body.Length;

            if (totalBytes > MaxTotalBytes)
                throw new InvalidDataException(
                    "Model catalog exceeded the total response size limit.");

            using var json = JsonDocument.Parse(body);

            string arrayName = provider == "gemini" ? "models" : "data";

            if (!json.RootElement.TryGetProperty(arrayName, out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "Model catalog schema was invalid.");
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                string property = provider == "gemini" ? "name" : "id";

                if (!entry.TryGetProperty(property, out var id) ||
                    id.ValueKind != JsonValueKind.String)
                    continue;

                string? name = id.GetString();

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (provider == "gemini")
                {
                    if (!entry.TryGetProperty(
                            "supportedGenerationMethods", out var methods) ||
                        methods.ValueKind != JsonValueKind.Array)
                        continue;

                    bool supportsGenerateContent = false;

                    foreach (var method in methods.EnumerateArray())
                    {
                        if (method.ValueKind == JsonValueKind.String &&
                            string.Equals(
                                method.GetString(),
                                "generateContent",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            supportsGenerateContent = true;
                            break;
                        }
                    }

                    if (!supportsGenerateContent)
                        continue;

                    if (name.StartsWith("models/", StringComparison.Ordinal))
                        name = name[7..];
                }

                if (name.Length > 100)
                    continue;

                names.Add(name);

                if (names.Count >= MaxModels)
                {
                    truncated = true;
                    hasMore = false;
                    break;
                }
            }

            if (!hasMore)
                break;

            cursor = GetNextCursor(provider, json.RootElement, page, array);

            hasMore = cursor is not null;

            if (hasMore && !seenCursors.Add(cursor!))
            {
                throw new InvalidDataException(
                    "Provider returned a repeated pagination cursor.");
            }


        }

        if (hasMore)
            truncated = true;

        string[] result = names
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ModelListResult(
            true, lastStatus, result, truncated);
    }

    private static string BuildPageUrl(
        string provider, string endpoint, string? cursor, int page)
    {
        return provider switch
        {
            "gemini" => endpoint +
                "?pageSize=1000" +
                (cursor is null ? "" :
                    "&pageToken=" + Uri.EscapeDataString(cursor)),

            "anthropic" => endpoint +
                "?limit=1000" +
                "&lifecycle=active%2Cdeprecated%2Cretired" +
                (cursor is null ? "" :
                    "&after_id=" + Uri.EscapeDataString(cursor)),

            "openrouter" => endpoint +
                "?output_modalities=all",

            _ => endpoint
        };
    }

    private static string? GetNextCursor(
        string provider,
        JsonElement root,
        int page,
        JsonElement array)
    {
        if (provider == "gemini")
        {
            return root.TryGetProperty("nextPageToken", out var token) &&
                   token.ValueKind == JsonValueKind.String
                ? token.GetString()
                : null;
        }

        if (provider == "anthropic")
        {
            bool more = root.TryGetProperty("has_more", out var hasMore) &&
                        hasMore.ValueKind == JsonValueKind.True;

            if (!more)
                return null;

            if (!root.TryGetProperty("last_id", out var lastId) ||
                lastId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(lastId.GetString()))
            {
                throw new InvalidDataException(
                    "Anthropic pagination response did not contain last_id.");
            }

            return lastId.GetString();
        }


        return null;
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length &&
            length > maxBytes)
        {
            throw new InvalidDataException(
                "Model catalog page exceeded the response size limit.");
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(ct);

        using var bounded = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;

        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bounded.Length + count > maxBytes)
            {
                throw new InvalidDataException(
                    "Model catalog page exceeded the response size limit.");
            }

            await bounded.WriteAsync(
                buffer.AsMemory(0, count), ct);
        }

        return bounded.ToArray();
    }
}
