using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ReviewPR.Api.Services;

// Local, single-machine Windows pilot storage. NOT a multi-tenant production vault.
public sealed record SavePersonalByokSettingsRequest(string? Provider, string? Model, string? ApiKey);

public sealed record PersonalByokSettingsResponse(
    string Provider,
    string Model,
    bool CredentialConfigured,
    DateTimeOffset? UpdatedUtc);

// Internal only. Never serialize this record in any HTTP result.
internal sealed record PersonalByokCredential(string Provider, string Model, string ApiKey);

public sealed class PersonalByokSettingsStore
{
    private sealed record StoredDocument(
        string Provider, string Model, string? ProtectedApiKey, DateTimeOffset UpdatedUtc);

    private readonly IDataProtector _protector;
    private readonly string _folder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PersonalByokSettingsStore(IDataProtectionProvider protectionProvider)
    {
        _protector = protectionProvider.CreateProtector("ReviewPR.PersonalBYOK.Settings.v1");
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReviewPR-AI", "BYOK-Pilot", "personal-settings");
        Directory.CreateDirectory(_folder);
    }

    private string PathFor(string organization, string userId)
    {
        // Caller identity is resolved by the server, never supplied by the request body.
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(organization + "\n" + userId));
        return Path.Combine(_folder, Convert.ToHexString(bytes) + ".protected");
    }

    private async Task<StoredDocument?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        string protectedText = await File.ReadAllTextAsync(path, cancellationToken);
        string json = _protector.Unprotect(protectedText);
        return JsonSerializer.Deserialize<StoredDocument>(json)
            ?? throw new InvalidDataException("Stored BYOK record is invalid.");
    }

    private static PersonalByokSettingsResponse PublicRecord(StoredDocument? record)
        => record is null
            ? new PersonalByokSettingsResponse("mock", "mock-positive-test", false, null)
            : new PersonalByokSettingsResponse(
                record.Provider,
                record.Model,
                !string.IsNullOrEmpty(record.ProtectedApiKey),
                record.UpdatedUtc);

    public async Task<PersonalByokSettingsResponse> GetAsync(
        string organization, string userId, CancellationToken cancellationToken)
    {
        var record = await ReadAsync(PathFor(organization, userId), cancellationToken);
        return PublicRecord(record); // Never returns the API key or protected key.
    }

    internal async Task<PersonalByokCredential?> GetCredentialAsync(
        string organization, string userId, CancellationToken cancellationToken)
    {
        // Called only AFTER app-token authentication and delegated-user authorization.
        StoredDocument? record = await ReadAsync(PathFor(organization, userId), cancellationToken);
        if (record is null || record.Provider == "mock" ||
            string.IsNullOrEmpty(record.ProtectedApiKey)) return null;
        return new PersonalByokCredential(record.Provider, record.Model,
            _protector.Unprotect(record.ProtectedApiKey));
    }

    public async Task<PersonalByokSettingsResponse> SaveAsync(
        string organization, string userId, SavePersonalByokSettingsRequest request,
        CancellationToken cancellationToken)
    {
        string provider = (request.Provider ?? "").Trim().ToLowerInvariant();
        string model = (request.Model ?? "").Trim();
        if (provider is not ("mock" or "openai" or "gemini" or "anthropic" or "groq" or "openrouter" or "compatible"))
            throw new ArgumentException("Unsupported provider.");
        if (model.Length is < 1 or > 100 ||
            !System.Text.RegularExpressions.Regex.IsMatch(model, @"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,99}$"))
            throw new ArgumentException("Invalid model name.");
        if ((request.ApiKey?.Length ?? 0) > 4096)
            throw new ArgumentException("API key is too long.");
        if (provider == "mock" && !string.IsNullOrEmpty(request.ApiKey))
            throw new ArgumentException("Mock provider does not require a credential.");

        string path = PathFor(organization, userId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = await ReadAsync(path, cancellationToken);
            string? protectedKey = null;
            if (provider != "mock")
            {
                if (!string.IsNullOrWhiteSpace(request.ApiKey))
                {
                    string key = request.ApiKey.Trim();
                    if (key.Length < 8)
                        throw new ArgumentException("Credential value is too short.");
                    protectedKey = _protector.Protect(key);
                }
                else if (existing?.Provider == provider)
                {
                    protectedKey = existing.ProtectedApiKey;
                }
                if (string.IsNullOrEmpty(protectedKey))
                    throw new ArgumentException("A key is required for a new provider.");
            }

            var next = new StoredDocument(provider, model, protectedKey, DateTimeOffset.UtcNow);
            string ciphertext = _protector.Protect(JsonSerializer.Serialize(next));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, ciphertext, cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            return PublicRecord(next);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string organization, string userId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string path = PathFor(organization, userId);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }
}
