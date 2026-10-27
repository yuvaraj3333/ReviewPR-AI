
using System.Net.Http.Headers;
using System.Text.Json;

namespace ReviewPR.Api.Services;

public sealed record DiscoveredModel(string Id, string Provider);

public sealed class ModelDiscoveryService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;

    public ModelDiscoveryService(
        IHttpClientFactory httpFactory,
        IConfiguration configuration)
    {
        _httpFactory = httpFactory;
        _configuration = configuration;
    }

    public async Task<List<DiscoveredModel>> DiscoverAsync(
        string provider,
        CancellationToken cancellationToken)
    {
        provider = provider.ToLowerInvariant();

        if (provider != "openai" && provider != "gemini")
            throw new ArgumentException("Unsupported provider.");

        var keyName = provider == "openai"
            ? "AI:OpenAI:ApiKey"
            : "AI:Gemini:ApiKey";

        var key = _configuration[keyName];

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"No {provider} API key configured.");

        var client = _httpFactory.CreateClient("ModelDiscovery");

        var url = provider == "openai"
            ? "https://api.openai.com/v1/models"
            : "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000";

        using var request = new HttpRequestMessage(
            HttpMethod.Get, url);

        if (provider == "openai")
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", key);
        }
        else
        {
            request.Headers.Add("x-goog-api-key", key);
        }

        using var response = await client.SendAsync(
            request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Model discovery failed with upstream HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);

        var results = new List<DiscoveredModel>();

        var root = document.RootElement;

        if (provider == "openai")
        {
            if (!root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "OpenAI returned an invalid models response.");
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) &&
                    id.ValueKind == JsonValueKind.String)
                {
                    var modelId = id.GetString();

                    if (!string.IsNullOrWhiteSpace(modelId))
                        results.Add(new DiscoveredModel(modelId, provider));
                }
            }
        }
        else
        {
            if (!root.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "Gemini returned an invalid models response.");
            }

            foreach (var item in models.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String)
                    continue;

                var supportsGenerateContent = false;

                if (item.TryGetProperty(
                    "supportedGenerationMethods", out var methods) &&
                    methods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var method in methods.EnumerateArray())
                    {
                        if (method.ValueKind == JsonValueKind.String &&
                            method.GetString() == "generateContent")
                        {
                            supportsGenerateContent = true;
                            break;
                        }
                    }
                }

                if (!supportsGenerateContent)
                    continue;

                var modelId = name.GetString();

                if (!string.IsNullOrWhiteSpace(modelId))
                {
                    results.Add(new DiscoveredModel(
                        modelId.Replace("models/", ""), provider));
                }
            }
        }

        return results
            .DistinctBy(m => m.Id)
            .OrderBy(m => m.Id)
            .ToList();
    }
}
