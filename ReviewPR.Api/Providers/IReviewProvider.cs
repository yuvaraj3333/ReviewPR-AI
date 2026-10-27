namespace ReviewPR.Api.Providers;

public interface IReviewProvider
{
    string Id { get; }
    Task<ReviewResult> ReviewAsync(string model, ReviewRequest request, CancellationToken ct);
}
