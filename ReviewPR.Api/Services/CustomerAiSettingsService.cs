
using ReviewPR.Api;

namespace ReviewPR.Api.Services;

public sealed class CustomerAiSettingsService(
    IConfiguration configuration)
{
    public CustomerAiConfiguration GetSettings()
    {
        string provider =
            configuration["ReviewPR:Provider"]
            ?? "mock";

        string model =
            configuration["ReviewPR:Model"]
            ?? "mock";

        bool automaticReviewEnabled =
            bool.TryParse(
                configuration["ReviewPR:AutomaticReviewEnabled"],
                out bool enabled) && enabled;

        return new CustomerAiConfiguration(
            provider,
            model,
            automaticReviewEnabled
        );
    }

    public bool IsCredentialConfigured(string provider)
    {
        string? keyName = provider.ToLowerInvariant() switch
        {
            "openai" => "AI:OpenAI:ApiKey",
            "gemini" => "AI:Gemini:ApiKey",
            "compatible" => "AI:Compatible:ApiKey",
            _ => null
        };

        if (keyName is null)
            return false;

        return !string.IsNullOrWhiteSpace(
            configuration[keyName]);
    }
}
