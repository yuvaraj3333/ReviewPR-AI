using System.Net.Http.Headers;
using System.Text.Json;

namespace ReviewPR.Api.Services;

// Private pilot only. Do not treat this as multi-organization authorization.
// Both calls must succeed using the current user's delegated Azure DevOps token.
public sealed class ByokPilotIdentityResolver(IHttpClientFactory clients)
{
    public const string Organization = "reviewpr-ai-lab";
    public const string Project = "ReviewPR-AI-Test";
    public const string Repository = "ReviewPR-AI-Test";

    public async Task<string?> ResolveUserIdAsync(HttpContext httpContext, CancellationToken ct)
    {
        string delegatedToken = httpContext.Request.Headers["X-ReviewPR-DevOps-Token"].ToString();
        if (string.IsNullOrWhiteSpace(delegatedToken) || delegatedToken.Length > 8192 ||
            delegatedToken.Contains('\r') || delegatedToken.Contains('\n')) return null;

        try
        {
            HttpClient client = clients.CreateClient("AzureDevOpsIdentity");
            using var identityRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://dev.azure.com/{Organization}/_apis/connectionData?api-version=7.1-preview.1");
            identityRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegatedToken);
            using var identityResponse = await client.SendAsync(identityRequest, ct);
            if (!identityResponse.IsSuccessStatusCode) return null;

            using var identity = await JsonDocument.ParseAsync(
                await identityResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!identity.RootElement.TryGetProperty("authenticatedUser", out var user) ||
                !user.TryGetProperty("id", out var userIdJson) ||
                userIdJson.ValueKind != JsonValueKind.String) return null;
            string? userId = userIdJson.GetString();
            if (string.IsNullOrWhiteSpace(userId)) return null;

            // Pilot scope restriction. GET proves readable repository, not settings-write permission.
            using var repoRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://dev.azure.com/{Organization}/{Project}/_apis/git/repositories/{Repository}?api-version=7.1");
            repoRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", delegatedToken);
            using var repoResponse = await client.SendAsync(repoRequest, ct);
            if (!repoResponse.IsSuccessStatusCode) return null;
            using var repo = await JsonDocument.ParseAsync(
                await repoResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!repo.RootElement.TryGetProperty("id", out var repositoryId) ||
                repositoryId.ValueKind != JsonValueKind.String ||
                !repo.RootElement.TryGetProperty("name", out var repositoryName) ||
                repositoryName.ValueKind != JsonValueKind.String ||
                !string.Equals(repositoryName.GetString(), Repository, StringComparison.OrdinalIgnoreCase))
                return null;
            return userId;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
}
