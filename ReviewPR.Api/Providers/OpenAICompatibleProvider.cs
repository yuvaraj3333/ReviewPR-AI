
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewPR.Api.Providers;

public sealed class OpenAICompatibleProvider(
    HttpClient client,
    IConfiguration configuration) : IReviewProvider
{
    public string Id => "compatible";

    public async Task<ReviewResult> ReviewAsync(
        string model,
        ReviewRequest request,
        CancellationToken ct)
    {
        string? apiKey =
            configuration["AI:Compatible:ApiKey"];

        string? baseUrl =
            configuration["AI:Compatible:BaseUrl"];

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Customer AI API key is not configured.");

        if (string.IsNullOrWhiteSpace(baseUrl) ||
            !Uri.TryCreate(
                baseUrl,
                UriKind.Absolute,
                out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                "A valid HTTPS AI endpoint is required.");
        }

        // The configured base URL must be a trusted
        // administrator-controlled /v1-style API root.
        // Never accept arbitrary URLs from PR content.
        var chatEndpoint = new Uri(
            endpoint.AbsoluteUri.TrimEnd('/') +
            "/chat/completions");

        var payload = new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = ProviderHelpers.Instructions
                },
                new
                {
                    role = "user",
                    content = ProviderHelpers.Prompt(request)
                }
            },
            temperature = 0
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            chatEndpoint);

        message.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                apiKey);

        message.Content = JsonContent.Create(payload);

        using var response =
            await client.SendAsync(message, ct);

        await ProviderHelpers.ThrowOnFailure(
            response,
            ct);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct));

        var root = document.RootElement;

        var content = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException(
                "AI provider returned an empty review.");

        return ProviderHelpers.Parse(content);
    }
}
