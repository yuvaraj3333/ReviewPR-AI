
namespace ReviewPR.Api;

public sealed record ReviewRequest(
    string RepositoryId,
    int PullRequestId,
    string CommitSha,
    string Diff,
    string Instructions
);

public sealed record ReviewFinding(
    string FilePath,
    int Line,
    string Severity,
    string Category,
    string Description,
    string SuggestedFix
);

public sealed record ReviewResult(
    string Summary,
    List<ReviewFinding> Findings
);

public sealed record SelectedReviewRequest(
    string Provider,
    string Model,
    ReviewRequest Review
);

public sealed record ProviderInfo(
    string Id,
    string DisplayName,
    bool SupportsModelDiscovery
);

// Customer configuration (no API keys stored in this model)
public sealed record CustomerAiConfiguration(
    string Provider,
    string Model,
    bool AutomaticReviewEnabled
);
