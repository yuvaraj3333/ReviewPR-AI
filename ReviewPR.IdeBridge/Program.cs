
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using ReviewPR.Api;
using ReviewPR.Api.Providers;
using ReviewPR.Api.Services;

// One local invocation: one JSON request on stdin, one response on stdout.
// Credentials are never written to disk, command-line arguments or logs.
internal sealed record BridgeRequest(
    string? Operation,
    string? Provider,
    string? Model,
    string? ApiKey,
    string? RepositoryId,
    string? Diff);

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly Regex ModelPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$",
        RegexOptions.CultureInvariant);

    // Review batching limits.
    private const int MaxReviewBatchChars = 40_000;
    private const int MaxFilesPerReviewBatch = 3;
    private const int MaxReviewBatches = 8;

    private static void Reply(object payload) =>
        Console.Out.WriteLine(
            JsonSerializer.Serialize(payload, JsonOptions));

    private static bool ValidKey(string key) =>
        key.Length is >= 8 and <= 4096 &&
        !key.Contains('\n') &&
        !key.Contains('\r');

    // Split the original Git diff into complete file sections.
    // Original Git hunk headers and line numbers are preserved.
    private static List<string> SplitDiffIntoFileSections(string diff)
    {
        MatchCollection matches =
            Regex.Matches(diff, @"(?m)^diff --git ");

        if (matches.Count == 0 || matches[0].Index != 0)
        {
            throw new ArgumentException(
                "Git diff does not start with a valid file section.");
        }

        var sections = new List<string>(matches.Count);

        for (int i = 0; i < matches.Count; i++)
        {
            int start = matches[i].Index;

            int end = i + 1 < matches.Count
                ? matches[i + 1].Index
                : diff.Length;

            sections.Add(diff[start..end]);
        }

        return sections;
    }

    // Group complete file sections into review batches.
    // A large individual file is kept intact rather than having its
    // Git hunks split and potentially changing line-number context.
    private static List<string> BuildReviewBatches(
        string diff,
        out int fileCount)
    {
        List<string> fileSections =
            SplitDiffIntoFileSections(diff);

        fileCount = fileSections.Count;

        var batches = new List<string>();

        var currentBatch = new StringBuilder();

        int currentFileCount = 0;

        void FlushCurrentBatch()
        {
            if (currentBatch.Length == 0)
                return;

            batches.Add(currentBatch.ToString());

            currentBatch.Clear();

            currentFileCount = 0;
        }

        foreach (string fileSection in fileSections)
        {
            // Keep oversized files intact in their own batch.
            if (fileSection.Length > MaxReviewBatchChars)
            {
                FlushCurrentBatch();

                batches.Add(fileSection);
            }
            else
            {
                bool exceedsCharacterLimit =
                    currentBatch.Length + fileSection.Length >
                    MaxReviewBatchChars;

                bool exceedsFileLimit =
                    currentFileCount >= MaxFilesPerReviewBatch;

                if (currentFileCount > 0 &&
                    (exceedsCharacterLimit || exceedsFileLimit))
                {
                    FlushCurrentBatch();
                }

                currentBatch.Append(fileSection);

                currentFileCount++;
            }

            if (batches.Count > MaxReviewBatches)
            {
                throw new ArgumentException(
                    $"This change set requires more than {MaxReviewBatches} " +
                    "AI review batches. Review fewer changed files at a time.");
            }
        }

        FlushCurrentBatch();

        if (batches.Count == 0 ||
            batches.Count > MaxReviewBatches)
        {
            throw new ArgumentException(
                "Unable to create valid AI review batches.");
        }

        return batches;
    }

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Reply(new
            {
                success = false,
                error = "This bridge accepts input on stdin only."
            });

            return 2;
        }

        try
        {
            string json = await Console.In.ReadToEndAsync();

            if (json.Length is < 2 or > 400_000)
            {
                throw new ArgumentException(
                    "Request size is outside the allowed range.");
            }

            BridgeRequest input =
                JsonSerializer.Deserialize<BridgeRequest>(
                    json, JsonOptions)
                ?? throw new ArgumentException("Request is missing.");

            string operation =
                (input.Operation ?? "review").Trim().ToLowerInvariant();

            string provider =
                (input.Provider ?? "").Trim().ToLowerInvariant();

            string key = input.ApiKey ?? "";

            // Provider configuration and model discovery.
            if (operation == "configure")
            {
                if (!ByokProviderModelsService.IsSupported(provider))
                {
                    throw new ArgumentException(
                        "Unsupported AI provider.");
                }

                if (!ValidKey(key))
                {
                    throw new ArgumentException(
                        "Missing or invalid provider API key.");
                }

                using var configureHandler =
                    new HttpClientHandler
                    {
                        AllowAutoRedirect = false
                    };

                using var configureClient =
                    new HttpClient(configureHandler)
                    {
                        Timeout = TimeSpan.FromSeconds(25)
                    };

                using var configureCancellation =
                    new CancellationTokenSource(
                        TimeSpan.FromSeconds(25));

                var catalog =
                    await ByokProviderModelsService.ListAsync(
                        configureClient,
                        provider,
                        key,
                        configureCancellation.Token);

                if (!catalog.Success)
                {
                    string error = catalog.UpstreamStatus switch
                    {
                        401 or 403 =>
                            "Provider rejected the API key or its permissions.",

                        429 =>
                            "Provider rate-limited model discovery. Try again later.",

                        _ =>
                            $"Provider model discovery failed (HTTP {catalog.UpstreamStatus})."
                    };

                    Reply(new
                    {
                        success = false,
                        error
                    });

                    return 1;
                }

                if (catalog.Models.Length == 0)
                {
                    Reply(new
                    {
                        success = false,
                        error = "No compatible models were returned by the provider."
                    });

                    return 1;
                }

                Reply(new
                {
                    success = true,
                    provider,
                    providerName =
                        ByokProviderModelsService.ProviderName(provider),
                    models = catalog.Models,
                    truncated = catalog.Truncated
                });

                return 0;
            }

            // Local Git review operation.
            if (operation != "review")
            {
                throw new ArgumentException(
                    "Unsupported bridge operation.");
            }

            if (!ByokProviderModelsService.IsSupported(provider))
            {
                throw new ArgumentException(
                    "Unsupported AI provider.");
            }

            string model = (input.Model ?? "").Trim();

            string diff = input.Diff ?? "";

            string repo = (input.RepositoryId ?? "").Trim();

            if (!ModelPattern.IsMatch(model))
            {
                throw new ArgumentException(
                    "Invalid AI model identifier.");
            }

            if (!ValidKey(key))
            {
                throw new ArgumentException(
                    "Missing or invalid provider API key.");
            }

            if (diff.Length is < 10 or > 160_000 ||
                !diff.Contains("diff --git ", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Git diff is missing or too large.");
            }

            if (repo.Length is < 1 or > 200 ||
                repo.Contains('\n') ||
                repo.Contains('\r'))
            {
                throw new ArgumentException(
                    "Invalid repository name.");
            }

            // Configure the selected provider.
            var settings = new Dictionary<string, string?>
            {
                ["AI:OpenAI:ApiKey"] =
                    provider == "openai" ? key : null,

                ["AI:Gemini:ApiKey"] =
                    provider == "gemini" ? key : null,

                ["AI:Anthropic:ApiKey"] =
                    provider == "anthropic" ? key : null
            };

            if (provider is "groq" or "openrouter")
            {
                settings["AI:Compatible:ApiKey"] = key;

                settings["AI:Compatible:BaseUrl"] =
                    ByokProviderModelsService.ReviewBaseUrl(provider);
            }

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(settings)
                .Build();

            using var handler =
                new HttpClientHandler
                {
                    AllowAutoRedirect = false
                };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(100)
            };

            IReviewProvider engine = provider switch
            {
                "openai" =>
                    new OpenAIProvider(client, config),

                "gemini" =>
                    new GeminiProvider(client, config),

                "anthropic" =>
                    new AnthropicProvider(client, config),

                "groq" or "openrouter" =>
                    new OpenAICompatibleProvider(client, config),

                _ =>
                    throw new InvalidOperationException(
                        "Unsupported provider.")
            };

            // Split the complete Git diff into file-aware batches.
            List<string> batches = BuildReviewBatches(
                diff,
                out int changedFileCount);

            // Bounded overall timeout based on the number of review batches.
            int reviewTimeoutSeconds = Math.Min(
                820,
                20 + (batches.Count * 100));

            using var cancellation =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(reviewTimeoutSeconds));

            var combinedFindings =
                new List<ReviewFinding>();

            var findingKeys = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            var batchSummaries = new List<string>();

            // Process batches sequentially to avoid unnecessary
            // concurrent provider requests and rate-limit pressure.
            foreach (string batchDiff in batches)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                var review = new ReviewRequest(
                    repo,
                    0,
                    "local-working-tree",
                    batchDiff,
                    "Review only added (+) lines in the supplied Git diff. " +
                    "Use exact repository-relative file paths and new-file line numbers from the diff. " +
                    "Do not report issues on unchanged context lines.");

                ReviewResult batchResult =
                    await engine.ReviewAsync(
                        model,
                        review,
                        cancellation.Token);

                if (batchResult is null ||
                    batchResult.Findings is null)
                {
                    throw new InvalidOperationException(
                        "AI provider returned an invalid review payload.");
                }

                if (batchResult.Findings.Count > 100)
                {
                    throw new InvalidOperationException(
                        "AI provider returned too many findings.");
                }

                if (!string.IsNullOrWhiteSpace(batchResult.Summary))
                {
                    batchSummaries.Add(
                        batchResult.Summary.Trim());
                }

                // Aggregate findings and remove duplicates.
                foreach (ReviewFinding finding in batchResult.Findings)
                {
                    if (finding is null)
                    {
                        throw new InvalidOperationException(
                            "AI provider returned an invalid finding.");
                    }

                    string normalizedDescription = Regex.Replace(
                        finding.Description ?? "",
                        @"\s+",
                        " ").Trim();

                    string findingKey = string.Join(
                        "\u001F",
                        finding.FilePath ?? "",
                        finding.Line.ToString(),
                        finding.Category ?? "",
                        normalizedDescription);

                    if (!findingKeys.Add(findingKey))
                    {
                        continue;
                    }

                    combinedFindings.Add(finding);

                    if (combinedFindings.Count > 100)
                    {
                        throw new InvalidOperationException(
                            "AI provider returned too many findings.");
                    }
                }
            }

            // Create the final review summary.
            string summary;

            if (batches.Count == 1)
            {
                summary =
                    batchSummaries.FirstOrDefault()
                    ?? "Review completed.";
            }
            else
            {
                string providerSummary = string.Join(
                    " ",
                    batchSummaries
                        .Distinct(StringComparer.OrdinalIgnoreCase));

                summary =
                    $"Reviewed {changedFileCount} changed file(s) " +
                    $"in {batches.Count} batches.";

                if (!string.IsNullOrWhiteSpace(providerSummary))
                {
                    summary += " " + providerSummary;
                }
            }

            if (summary.Length > 2000)
            {
                summary = summary[..2000];
            }

            // Return one combined response to the VS Code extension.
            Reply(new
            {
                success = true,
                summary,
                findings = combinedFindings
            });

            return 0;
        }
        catch (ArgumentException ex)
        {
            // Return only allowlisted validation messages. Never expose request
            // contents, provider response bodies, credentials, or raw exceptions.
            string safeError = ex.Message switch
            {
                "Unsupported AI provider." =>
                    "Unsupported AI provider.",

                "Invalid AI model identifier." =>
                    "Invalid AI model identifier.",

                "Missing or invalid provider API key." =>
                    "Missing or invalid provider API key.",

                "Git diff is missing or too large." =>
                    "Git diff is missing or too large.",

                "Invalid repository name." =>
                    "Invalid repository name.",

                "Git diff does not start with a valid file section." =>
                    "Git diff does not start with a valid file section.",

                "Unable to create valid AI review batches." =>
                    "Unable to create valid AI review batches.",

                _ => "Invalid bridge request."
            };

            Reply(new
            {
                success = false,
                error = safeError
            });

            return 2;
        }
        catch (OperationCanceledException)
        {
            Reply(new
            {
                success = false,
                error = "AI request timed out or was cancelled."
            });

            return 1;
        }
        catch (HttpRequestException)
        {
            Reply(new
            {
                success = false,
                error = "Could not connect to the selected AI provider."
            });

            return 1;
        }
        catch (Exception)
        {
            // Fixed error response: no exception details or secrets.
            Reply(new
            {
                success = false,
                error = "An unexpected error occurred while processing the AI review. No diagnostic details were exposed."
            });

            return 1;
        }
    }
}
