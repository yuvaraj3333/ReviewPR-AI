using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewPR.Api.Providers;

public sealed class AnthropicProvider(
    HttpClient client,
    IConfiguration configuration) : IReviewProvider
{
    public string Id => "anthropic";

    public async Task<ReviewResult> ReviewAsync(
        string model,
        ReviewRequest request,
        CancellationToken ct)
    {
        string? apiKey = configuration["AI:Anthropic:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Anthropic API key is not configured.");

        var payload = new
        {
            model,
            max_tokens = 4096,
            system = ProviderHelpers.Instructions,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = ProviderHelpers.Prompt(request)
                }
            }
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.anthropic.com/v1/messages");

        message.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        message.Headers.TryAddWithoutValidation(
            "anthropic-version", "2023-06-01");

        message.Content = JsonContent.Create(payload);

        using var response = await client.SendAsync(message, ct);

        await ProviderHelpers.ThrowOnFailure(response, ct);

        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(ct));

        var root = document.RootElement;

        if (!root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Anthropic returned an invalid response.");
        }

        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var type) &&
                type.GetString() == "text" &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return ProviderHelpers.Parse(text.GetString() ?? "");
            }
        }

        throw new InvalidOperationException(
            "Anthropic returned no structured review text.");
    }
}