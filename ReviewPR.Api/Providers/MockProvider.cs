using System.Text.RegularExpressions;

namespace ReviewPR.Api.Providers;

// Development-only fixture for testing ReviewPR's positive-finding pipeline.
// Never enabled for other repositories, PRs or model selections.
public sealed class MockProvider : IReviewProvider
{
    private const string LabRepository =
        "reviewpr-ai-lab/ReviewPR-AI-Test/ReviewPR-AI-Test";

    private static readonly Regex HunkHeader = new(
        @"^@@ -\d+(?:,\d+)? \+(?<line>\d+)(?:,\d+)? @@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FaultyReturn = new(
        @"\breturn\s+price\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Id => "mock";

    public Task<ReviewResult> ReviewAsync(
        string model,
        ReviewRequest request,
        CancellationToken ct)
    {
        var findings = new List<ReviewFinding>();

        // Default mock behaviour remains unchanged: zero findings.
        // The explicit positive-test model is limited to the known lab PR.
        if (model != "mock-positive-test" ||
            request.PullRequestId != 4 ||
            !string.Equals(
                request.RepositoryId,
                LabRepository,
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ReviewResult(
                "Mock development review completed.", findings));
        }

        string? filePath = null;
        int newLine = 0;
        bool inHunk = false;

        using var reader = new StringReader(request.Diff);
        while (reader.ReadLine() is { } raw)
        {
            ct.ThrowIfCancellationRequested();
            string line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                filePath = null;
                inHunk = false;
                continue;
            }

            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                filePath = "/" + line[6..];
                inHunk = false;
                continue;
            }

            Match header = HunkHeader.Match(line);
            if (header.Success)
            {
                inHunk = true;
                newLine = int.Parse(header.Groups["line"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            if (!inHunk || filePath is null)
                continue;

            if (line.StartsWith("+", StringComparison.Ordinal))
            {
                if (FaultyReturn.IsMatch(line[1..]))
                {
                    findings.Add(new ReviewFinding(
                        filePath,
                        newLine,
                        "high",
                        "correctness",
                        "The added return statement returns price without accounting for quantity.",
                        "Return price * quantity after validating the method's expected behaviour."));
                    break;
                }
                newLine++;
            }
            else if (line.StartsWith(" ", StringComparison.Ordinal))
            {
                newLine++;
            }
            else if (line.StartsWith("\\", StringComparison.Ordinal) ||
                     line.StartsWith("-", StringComparison.Ordinal))
            {
                // Deletions and 'No newline at end of file' do not advance
                // the new-file line number.
            }
            else
            {
                inHunk = false;
            }
        }

        return Task.FromResult(new ReviewResult(
            findings.Count > 0
                ? "Controlled positive-finding test completed."
                : "No matching added-line fixture found; no findings generated.",
            findings));
    }
}
