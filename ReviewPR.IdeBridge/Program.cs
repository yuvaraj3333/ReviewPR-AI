using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using ReviewPR.Api;
using ReviewPR.Api.Providers;

// A local, single-invocation process: one request on stdin, one JSON response on stdout.
// No API listener, Azure DevOps token, cloud tunnel, on-disk credentials or PR publishing.
internal sealed record BridgeRequest(
    string? Provider, string? Model, string? ApiKey, string? RepositoryId, string? Diff);

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex ModelPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$", RegexOptions.CultureInvariant);

    private static void Reply(object payload) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Reply(new { success = false, error = "This bridge accepts input on stdin only." });
            return 2;
        }

        try
        {
            // The extension enforces a smaller limit; independently bound the bridge input.
            string json = await Console.In.ReadToEndAsync();
            if (json.Length is < 2 or > 400_000)
                throw new ArgumentException("Review request size is outside the allowed range.");

            BridgeRequest input = JsonSerializer.Deserialize<BridgeRequest>(json, JsonOptions)
                ?? throw new ArgumentException("Review request is missing.");

            string provider = (input.Provider ?? "").Trim().ToLowerInvariant();
            string model = (input.Model ?? "").Trim();
            string key = input.ApiKey ?? "";
            string diff = input.Diff ?? "";
            string repo = (input.RepositoryId ?? "").Trim();

            if (provider is not ("openai" or "gemini"))
                throw new ArgumentException("Only OpenAI and Gemini are supported by this local bridge.");
            if (!ModelPattern.IsMatch(model))
                throw new ArgumentException("Invalid AI model identifier.");
            if (key.Length is < 8 or > 4096 || key.Contains('\n') || key.Contains('\r'))
                throw new ArgumentException("Missing or invalid provider key.");
            if (diff.Length is < 10 or > 160_000 ||
                !diff.Contains("diff --git ", StringComparison.Ordinal))
                throw new ArgumentException("Git diff is missing or too large.");
            if (repo.Length is < 1 or > 200 || repo.Contains('\n') || repo.Contains('\r'))
                throw new ArgumentException("Invalid repository name.");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [provider == "openai" ? "AI:OpenAI:ApiKey" : "AI:Gemini:ApiKey"] = key
                })
                .Build();

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(100)
            };
            IReviewProvider engine = provider switch
            {
                "openai" => new OpenAIProvider(client, config),
                "gemini" => new GeminiProvider(client, config),
                _ => throw new InvalidOperationException("Unsupported provider.")
            };
            var review = new ReviewRequest(
                repo, 0, "local-working-tree", diff,
                "Review only added (+) lines in the supplied Git diff. " +
                "Use exact repository-relative file paths and new-file line numbers from the diff.");

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(110));
            ReviewResult result = await engine.ReviewAsync(model, review, cancellation.Token);
            if (result.Findings.Count > 100)
                throw new InvalidOperationException("AI provider returned too many findings.");

            Reply(new { success = true, summary = result.Summary, findings = result.Findings });
            return 0;
        }
        catch (ProviderHttpException ex)
        {
            // Expose only a safe structured error category, never the raw provider response.
            var category = string.IsNullOrWhiteSpace(ex.Category)
                ? "unknown"
                : ex.Category;

            if (category.Length > 80)
                category = "unknown";

            foreach (var ch in category)
            {
                if (!char.IsLetterOrDigit(ch) &&
                    ch != '_' && ch != '-' && ch != '.')
                {
                    category = "unknown";
                    break;
                }
            }

            Reply(new
            {
                success = false,
                error = $"AI provider returned HTTP {ex.UpstreamStatus} ({category}): {ex.SafeMessage}"
            });
            return 1;
        }
        catch (OperationCanceledException)
        {
            Reply(new { success = false, error = "AI request timed out or was cancelled." });
            return 1;
        }
        catch (HttpRequestException)
        {
            Reply(new { success = false, error = "Could not connect to the selected AI provider." });
            return 1;
        }
        catch (ArgumentException ex)
        {
            // These strings are fixed validation messages, not API credentials.
            Reply(new { success = false, error = ex.Message });
            return 2;
        }
        catch (Exception)
        {
            Reply(new { success = false, error = "The local AI review failed. Check your model and provider configuration." });
            return 1;
        }
    }
}
