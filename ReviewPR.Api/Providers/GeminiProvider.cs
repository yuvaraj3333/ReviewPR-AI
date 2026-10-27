using System.Net.Http.Json;
using System.Text.Json;

namespace ReviewPR.Api.Providers;

public sealed class GeminiProvider(
    HttpClient client,
    IConfiguration config) : IReviewProvider
{
    public string Id => "gemini";

    public async Task<ReviewResult> ReviewAsync(
        string model,
        ReviewRequest request,
        CancellationToken ct)
    {
        var key = config["AI:Gemini:ApiKey"];

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured.");
        }

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new
                        {
                            text = ProviderHelpers.Prompt(request)
                        }
                    }
                }
            },

            systemInstruction = new
            {
                parts = new[]
                {
                    new
                    {
                        text = ProviderHelpers.Instructions
                    }
                }
            },

            generationConfig = new
            {
                responseMimeType = "application/json",
                maxOutputTokens = 2048
            }
        };

        var endpoint =
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint);

            message.Headers.Add("x-goog-api-key", key);
            message.Content = JsonContent.Create(payload);

            using var response = await client.SendAsync(
                message,
                ct);

            int status = (int)response.StatusCode;

            bool transient =
                status == 408 ||
                status == 429 ||
                status == 500 ||
                status == 502 ||
                status == 503 ||
                status == 504;

            if (!response.IsSuccessStatusCode &&
                transient &&
                attempt < maxAttempts)
            {
                double delaySeconds =
                    Math.Pow(2, attempt - 1) +
                    Random.Shared.NextDouble();

                await Task.Delay(
                    TimeSpan.FromSeconds(delaySeconds),
                    ct);

                continue;
            }

            await ProviderHelpers.ThrowOnFailure(
                response,
                ct);

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct));

            var root = document.RootElement;

            if (!root.TryGetProperty(
                    "candidates",
                    out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() == 0)
            {
                throw new InvalidOperationException(
                    "Gemini returned no candidates.");
            }

            var candidate = candidates[0];

            if (!candidate.TryGetProperty(
                    "finishReason",
                    out var finishReason) ||
                finishReason.GetString() != "STOP")
            {
                throw new InvalidOperationException(
                    "Gemini review did not finish normally.");
            }

            var content = candidate
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return ProviderHelpers.Parse(content ?? "");
        }

        throw new InvalidOperationException(
            "Gemini review could not be completed.");
    }
}
