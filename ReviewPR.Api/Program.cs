using Microsoft.AspNetCore.DataProtection;

using System.Text.RegularExpressions;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ReviewPR.Api;
using ReviewPR.Api.Providers;
using ReviewPR.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// --------------------------------------------------
// AI provider registration
// --------------------------------------------------

builder.Services.AddSingleton<MockProvider>();

// Disable automatic redirects when provider requests
// carry API credentials.
builder.Services.AddHttpClient<OpenAIProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
})
.ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

builder.Services.AddHttpClient<GeminiProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
})
.ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

builder.Services.AddHttpClient<OpenAICompatibleProvider>(
    client =>
    {
        client.Timeout = TimeSpan.FromSeconds(90);
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        });

builder.Services.AddHttpClient("ModelDiscovery", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
})
.ConfigurePrimaryHttpMessageHandler(() =>
    new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

builder.Services.AddScoped<ModelDiscoveryService>();

builder.Services.AddScoped<CustomerAiSettingsService>();


// --------------------------------------------------
// ReviewPR BYOK authentication
// Azure DevOps extension application token validation
// --------------------------------------------------

string extensionSecret =
    builder.Configuration["ExtensionSecret"]
    ?? throw new InvalidOperationException(
        "ExtensionSecret is not configured.");

if (Encoding.UTF8.GetByteCount(extensionSecret) < 32)
{
    throw new InvalidOperationException(
        "ExtensionSecret is too short for HS256 validation.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        // App-token JWT signature and lifetime validation are enforced below.
        // Do not log bearer tokens, JWT payloads, or signing secrets.
        // No event handlers are required for normal validation.

        options.TokenValidationParameters =
            new TokenValidationParameters
            {
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(extensionSecret)),

                ValidateIssuerSigningKey = true,
                // Verified extension-app-token prototype only. These flags do not
                // authorize an individual user, organization, or repository.
                // Complete issuer/audience binding and an independent Azure DevOps
                // user/repository authorization flow before enabling BYOK writes.
                ValidateIssuer = false,
                ValidateAudience = false,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ValidAlgorithms = new[] {
                    SecurityAlgorithms.HmacSha256
                },
                ClockSkew = TimeSpan.FromMinutes(1)
            };
    });

builder.Services.AddAuthorization();

// ReviewPR BYOK read-only authentication-test CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("ReviewPRByokTestCors", policy =>
    {
        policy.WithOrigins("https://reviewpr-ai.gallerycdn.vsassets.io")
              .WithMethods("GET", "PUT", "DELETE")
              .WithHeaders("Authorization", "X-ReviewPR-DevOps-Token", "Content-Type");
    });
});

builder.Services.AddHttpClient("AzureDevOpsIdentity", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
})
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
});

// REVIEWPR_3FC3_PERSONAL_BYOK: Private Windows development pilot, not a production vault.
if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("This pilot vault requires Windows DPAPI.");
string localKeys = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ReviewPR-AI", "BYOK-Pilot", "keys");
Directory.CreateDirectory(localKeys);
builder.Services.AddDataProtection()
    .SetApplicationName("ReviewPR-BYOK-Pilot-v1")
    .PersistKeysToFileSystem(new DirectoryInfo(localKeys))
    .ProtectKeysWithDpapi();
builder.Services.AddSingleton<PersonalByokSettingsStore>();
builder.Services.AddScoped<ByokPilotIdentityResolver>();
var app = builder.Build();

// --------------------------------------------------
// Public health endpoint
// --------------------------------------------------

app.MapGet("/health", () =>
    Results.Ok(new
    {
        name = "ReviewPR AI",
        status = "ok"
    }));

// --------------------------------------------------
// Local development endpoints ONLY
//
// Do NOT expose these on a public server.
// Production endpoints need authentication,
// customer authorization, tenant isolation,
// secret storage and request limits.
// --------------------------------------------------

if (app.Environment.IsDevelopment())
{
    // ----------------------------------------------
    // Available AI providers
    // ----------------------------------------------

    app.MapGet("/api/dev/providers", () =>
        Results.Ok(new[]
        {
            new ProviderInfo(
                "mock",
                "Mock (No API key required)",
                false
            ),
            new ProviderInfo(
                "openai",
                "OpenAI",
                true
            ),
            new ProviderInfo(
                "gemini",
                "Google Gemini",
                true
            ),
            new ProviderInfo(
                "compatible",
                "OpenAI-compatible API",
                false
            )
        }));

    // ----------------------------------------------
    // Model discovery
    // Currently supported by OpenAI and Gemini.
    // ----------------------------------------------

    app.MapGet("/api/dev/config",
    (CustomerAiSettingsService settings) =>
    {
        var config = settings.GetSettings();

        return Results.Ok(new
        {
            config.Provider,
            config.Model,
            config.AutomaticReviewEnabled,
            credentialConfigured =
                settings.IsCredentialConfigured(
                    config.Provider)
        });
    });

    app.MapGet("/api/dev/models/{provider}",
        async (
            string provider,
            ModelDiscoveryService discovery,
            CancellationToken ct) =>
        {
            provider = provider.ToLowerInvariant();

            if (provider != "openai" &&
                provider != "gemini")
            {
                return Results.BadRequest(new
                {
                    error =
                        "Dynamic model discovery is not " +
                        "supported for this provider."
                });
            }

            try
            {
                var models = await discovery.DiscoverAsync(
                    provider,
                    ct);

                return Results.Ok(new
                {
                    provider,
                    count = models.Count,
                    models
                });
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(
                    title: "Model discovery failed",
                    detail: ex.StatusCode.HasValue
                        ? $"Provider returned HTTP " +
                          $"{(int)ex.StatusCode.Value}."
                        : "Could not connect to AI provider.",
                    statusCode: 502
                );
            }
            catch (InvalidOperationException)
            {
                return Results.Problem(
                    title: "Model discovery configuration error",
                    detail:
                        "Check provider credentials and response.",
                    statusCode: 422
                );
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new
                {
                    error = "Invalid provider configuration."
                });
            }
        });

    // ----------------------------------------------
    // Development-only AI code review
    // Does NOT publish Azure DevOps comments.
    // ----------------------------------------------

    app.MapPost("/api/dev/review",
        async (
            SelectedReviewRequest input,
            MockProvider mock,
            OpenAIProvider openai,
            GeminiProvider gemini,
            OpenAICompatibleProvider compatible,
            CancellationToken ct) =>
        {
            if (input is null ||
                input.Review is null ||
                string.IsNullOrWhiteSpace(
                    input.Review.Diff) ||
                input.Review.Diff.Length > 30000 ||
                (input.Review.Instructions?.Length ?? 0)
                    > 2000 ||
                string.IsNullOrWhiteSpace(input.Model) ||
                input.Model.Length > 100 ||
                !Regex.IsMatch(
                    input.Model,
                    "^[A-Za-z0-9][A-Za-z0-9._-]*$"))
            {
                return Results.BadRequest(new
                {
                    error =
                        "Invalid review request or AI model."
                });
            }

            IReviewProvider? selectedProvider =
                input.Provider?.ToLowerInvariant() switch
                {
                    "mock" => mock,
                    "openai" => openai,
                    "gemini" => gemini,
                    "compatible" => compatible,
                    _ => null
                };

            if (selectedProvider is null)
            {
                return Results.BadRequest(new
                {
                    error = "Unsupported AI provider."
                });
            }

            try
            {
                ReviewResult result =
                    await selectedProvider.ReviewAsync(
                        input.Model,
                        input.Review,
                        ct);

                return Results.Ok(result);
            }
            catch (ProviderHttpException ex)
            {
                return Results.Problem(
                    title: "AI provider request failed",
                    detail:
                        $"Upstream HTTP {ex.UpstreamStatus}; " +
                        $"category: {ex.Category}",
                    statusCode: 502
                );
            }
            catch (HttpRequestException)
            {
                return Results.Problem(
                    title: "AI provider connection failed",
                    detail:
                        "Check provider availability and network.",
                    statusCode: 502
                );
            }
            catch (InvalidOperationException)
            {
                return Results.Problem(
                    title: "AI review could not be completed",
                    detail:
                        "Check credentials, model compatibility " +
                        "or provider response.",
                    statusCode: 422
                );
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new
                {
                    error = "Invalid AI review input."
                });
            }
        });

    // Milestone 2C: LOCAL development-only, authenticated, read-only
    // integration test. Never expose this endpoint on a public host.
    app.MapPost("/api/dev/run-verified-review", async (HttpContext context) =>
    {
        if (!System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress
                ?? System.Net.IPAddress.None))
            return Results.StatusCode(403);

        string expectedSecret = Environment.GetEnvironmentVariable("REVIEWPR_LOCAL_RUNNER_TOKEN") ?? "";
        string suppliedSecret = context.Request.Headers["X-ReviewPR-Local-Token"].ToString();
        if (expectedSecret.Length < 32 || suppliedSecret.Length != expectedSecret.Length ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expectedSecret),
                System.Text.Encoding.UTF8.GetBytes(suppliedSecret)))
            return Results.Unauthorized();

        // The job is fixed by the runner operator, not supplied by a caller.
        // The worker obtains Azure DevOps credentials and Git trust settings
        // from its own environment, and re-verifies the current PR snapshot.
        string jobFile = Environment.GetEnvironmentVariable("REVIEWPR_LOCAL_JOB_FILE") ?? "";
        string workerDll = Environment.GetEnvironmentVariable("REVIEWPR_LOCAL_WORKER_DLL") ?? "";
        if (!Path.IsPathFullyQualified(jobFile) || !File.Exists(jobFile) ||
            !Path.IsPathFullyQualified(workerDll) || !File.Exists(workerDll))
            return Results.Problem("Configure absolute local job and worker DLL paths.", statusCode: 503);

        string resultDir = Path.Combine(Path.GetTempPath(), "ReviewPR", "verified-results");
        Directory.CreateDirectory(resultDir);
        string resultFile = Path.Combine(resultDir, Guid.NewGuid().ToString("N") + ".json");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(workerDll)!
        };
        start.ArgumentList.Add(workerDll);
        start.ArgumentList.Add(jobFile);
        start.Environment["REVIEWPR_RESULT_JSON"] = resultFile;
        start.Environment["REVIEWPR_ENABLE_PUBLISH"] = "0";
        start.Environment.Remove("REVIEWPR_AZDO_WRITE_PAT");

        try
        {
            using var process = new System.Diagnostics.Process { StartInfo = start };
            if (!process.Start()) return Results.Problem("Worker could not start.", statusCode: 500);
            // Drain streams concurrently to avoid process pipe deadlocks.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return Results.Problem("Review timed out or was cancelled.", statusCode: 504);
            }
            await Task.WhenAll(stdout, stderr);
            // Never return process logs: they may contain sensitive diagnostics.
            if (process.ExitCode != 0 || !File.Exists(resultFile))
                return Results.Problem("Verified review failed. Inspect local worker logs.", statusCode: 422);
            string json = await File.ReadAllTextAsync(resultFile, context.RequestAborted);
            using var result = System.Text.Json.JsonDocument.Parse(json);
            var obj = result.RootElement;
            if (obj.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !obj.TryGetProperty("publishingEnabled", out var publishing) ||
                publishing.ValueKind != System.Text.Json.JsonValueKind.False ||
                !obj.TryGetProperty("status", out var state) || state.GetString() != "completed")
                return Results.Problem("Worker output failed validation.", statusCode: 422);
            return Results.Json(obj.Clone());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return Results.Problem("Local runner could not execute.", statusCode: 500);
        }
        finally
        {
            try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch (IOException) { }
        }
    });

}


// Enable authentication and authorization.
app.UseCors("ReviewPRByokTestCors");
app.UseAuthentication();
app.UseAuthorization();

// Verify that Azure DevOps extension authentication works.
// This endpoint does not grant access to customer credentials.
app.MapGet("/api/ai/auth-check", () =>
    Results.Ok(new
    {
        authenticated = true,
        service = "ReviewPR BYOK"
    }))
    .RequireAuthorization();

// Milestone 3F-C1 boundary: this endpoint identifies the *authentication
// mechanism*, not an individual Azure DevOps user. App tokens alone must not
// grant permission to read/write customer credentials or repository settings.
app.MapGet("/api/ai/me", () =>
    Results.Ok(new
    {
        authenticated = true,
        principalType = "azure-devops-extension-app",
        userIdentityVerified = false,
        repositoryAccessVerified = false,
        credentialSettingsAccess = false,
        message = "Extension token accepted; user and repository authorization not configured."
    }))
    .RequireAuthorization();

// --------------------------------------------------
// Milestone 3F-C2 — Azure DevOps delegated identity +
// pilot repository READ authorization.
//
// This is intentionally read-only: it does not authorize
// repository settings writes or access to customer API keys.
// Both bearer tokens are never logged or returned.
// --------------------------------------------------
app.MapGet("/api/ai/identity-check", async (
    HttpContext context,
    IHttpClientFactory clients,
    CancellationToken ct) =>
{
    // Fixed private pilot target. Multi-tenant repository authorization
    // requires validated tenant/repository IDs and separate write checks.
    const string organization = "reviewpr-ai-lab";
    const string project = "ReviewPR-AI-Test";
    const string repository = "ReviewPR-AI-Test";

    context.Response.Headers["Cache-Control"] = "no-store";

    string userToken = context.Request.Headers[
        "X-ReviewPR-DevOps-Token"].ToString();

    if (string.IsNullOrWhiteSpace(userToken) ||
        userToken.Length > 8192 ||
        userToken.Contains('\r') ||
        userToken.Contains('\n'))
    {
        return Results.Unauthorized();
    }

    // The endpoint is also guarded by RequireAuthorization(), which
    // validates the extension's signed, unexpired application token.
    // The delegated token is independently verified by Azure DevOps.
    var client = clients.CreateClient("AzureDevOpsIdentity");

    try
    {
        using var identityRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://dev.azure.com/{organization}/" +
            "_apis/connectionData?api-version=7.1-preview.1");

        identityRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", userToken);

        using var identityResponse = await client.SendAsync(
            identityRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!identityResponse.IsSuccessStatusCode)
            return Results.StatusCode(403);

        await using var identityStream =
            await identityResponse.Content.ReadAsStreamAsync(ct);

        using var identityJson = await System.Text.Json.JsonDocument.ParseAsync(
            identityStream, cancellationToken: ct);

        if (!identityJson.RootElement.TryGetProperty(
                "authenticatedUser", out var user) ||
            user.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !user.TryGetProperty("id", out var userIdElement) ||
            userIdElement.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            return Results.StatusCode(502);
        }

        string? userId = userIdElement.GetString();

        if (string.IsNullOrWhiteSpace(userId))
            return Results.StatusCode(403);

        // A repository GET proves delegated-token read access to this
        // pilot repository; it does NOT confer repository administration.
        using var repositoryRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://dev.azure.com/{organization}/{project}/" +
            $"_apis/git/repositories/{repository}?api-version=7.1");

        repositoryRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", userToken);

        using var repositoryResponse = await client.SendAsync(
            repositoryRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        if (!repositoryResponse.IsSuccessStatusCode)
            return Results.StatusCode(403);

        await using var repositoryStream =
            await repositoryResponse.Content.ReadAsStreamAsync(ct);

        using var repositoryJson = await System.Text.Json.JsonDocument.ParseAsync(
            repositoryStream, cancellationToken: ct);

        var repo = repositoryJson.RootElement;
        if (repo.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !repo.TryGetProperty("id", out var repositoryIdElement) ||
            repositoryIdElement.ValueKind != System.Text.Json.JsonValueKind.String ||
            !repo.TryGetProperty("name", out var repositoryNameElement) ||
            repositoryNameElement.ValueKind != System.Text.Json.JsonValueKind.String ||
            !string.Equals(repositoryNameElement.GetString(), repository,
                StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(403);
        }

        string? repositoryId = repositoryIdElement.GetString();
        if (string.IsNullOrWhiteSpace(repositoryId))
            return Results.StatusCode(403);

        // Preserve the exact response contract consumed by the
        // installed private test extension (identityVerifiedBy).
        return Results.Ok(new
        {
            authenticated = true,
            organization,
            userId,
            repository,
            repositoryId,
            identityVerifiedBy = "Azure DevOps",
            identityVerified = true,
            repositoryReadVerified = true,
            repositorySettingsWriteAuthorized = false,
            credentialSettingsEnabled = false
        });
    }
    catch (HttpRequestException)
    {
        // Avoid leaking upstream request details or delegated tokens.
        return Results.Problem(
            title: "Azure DevOps identity service unavailable",
            statusCode: 502);
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem(
            title: "Azure DevOps identity service timed out",
            statusCode: 504);
    }
    catch (System.Text.Json.JsonException)
    {
        return Results.Problem(
            title: "Unexpected Azure DevOps identity response",
            statusCode: 502);
    }
})
.RequireAuthorization();

// REVIEWPR_3FC3_PERSONAL_BYOK: Personal settings only. No repository-shared writes.
app.MapGet("/api/ai/settings/me", async (
    HttpContext ctx, ByokPilotIdentityResolver identity,
    PersonalByokSettingsStore store, CancellationToken ct) =>
{
    string? userId = await identity.ResolveUserIdAsync(ctx, ct);
    if (userId is null) return Results.StatusCode(403);
    return Results.Ok(await store.GetAsync(ByokPilotIdentityResolver.Organization, userId, ct));
}).RequireAuthorization();

app.MapPut("/api/ai/settings/me", async (
    HttpContext ctx, SavePersonalByokSettingsRequest request,
    ByokPilotIdentityResolver identity, PersonalByokSettingsStore store,
    IConfiguration configuration, CancellationToken ct) =>
{
    // Opt-in is disabled by default, and must not be enabled with the leaked test signing secret.
    if (!configuration.GetValue<bool>("BYOK:EnablePersonalSettingsWrites"))
        return Results.StatusCode(403);
    string? userId = await identity.ResolveUserIdAsync(ctx, ct);
    if (userId is null) return Results.StatusCode(403);
    try
    {
        return Results.Ok(await store.SaveAsync(
            ByokPilotIdentityResolver.Organization, userId, request, ct));
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid BYOK settings." }); }
}).RequireAuthorization();

app.MapDelete("/api/ai/settings/me", async (
    HttpContext ctx, ByokPilotIdentityResolver identity,
    PersonalByokSettingsStore store, IConfiguration configuration, CancellationToken ct) =>
{
    if (!configuration.GetValue<bool>("BYOK:EnablePersonalSettingsWrites"))
        return Results.StatusCode(403);
    string? userId = await identity.ResolveUserIdAsync(ctx, ct);
    if (userId is null) return Results.StatusCode(403);
    await store.DeleteAsync(ByokPilotIdentityResolver.Organization, userId, ct);
    return Results.NoContent();
}).RequireAuthorization();
app.Run();

