using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewPR.Api.Providers;

public sealed class OpenAIProvider(HttpClient client, IConfiguration config) : IReviewProvider
{
    public string Id => "openai";

    public async Task<ReviewResult> ReviewAsync(string model, ReviewRequest request, CancellationToken ct)
    {
        var key = config["AI:OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAI key is not configured in local development secrets.");

        var schema = new
        {
            type = "object",
            properties = new
            {
                summary = new { type = "string" },
                findings = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            filePath = new { type = "string" }, line = new { type = "integer" },
                            severity = new { type = "string" }, category = new { type = "string" },
                            description = new { type = "string" }, suggestedFix = new { type = "string" }
                        },
                        required = new[] { "filePath", "line", "severity", "category", "description", "suggestedFix" },
                        additionalProperties = false
                    }
                }
            },
            required = new[] { "summary", "findings" },
            additionalProperties = false
        };
        var payload = new
        {
            model,
            store = false,
            instructions = ProviderHelpers.Instructions,
            input = ProviderHelpers.Prompt(request),
            text = new { format = new { type = "json_schema", name = "reviewpr_result", strict = true, schema } }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(message, ct);
        await ProviderHelpers.ThrowOnFailure(response, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "completed")
            throw new InvalidOperationException("OpenAI did not complete the review.");
        if (!root.TryGetProperty("output", out var outputs) || outputs.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("OpenAI output missing.");
        foreach (var output in outputs.EnumerateArray())
        {
            if (!output.TryGetProperty("type", out var type) || type.GetString() != "message" ||
                !output.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in content.EnumerateArray())
                if (item.TryGetProperty("type", out var itemType) && itemType.GetString() == "output_text" &&
                    item.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String)
                    return ProviderHelpers.Parse(value.GetString()!);
        }
        throw new InvalidOperationException("OpenAI returned no structured review text.");
    }
}
