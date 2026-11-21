# ReviewPR AI for Visual Studio Code

AI-powered Git code review directly inside Visual Studio Code.

## Features
- Review local Git changes using AI
- Gemini, Groq, OpenRouter, OpenAI and Anthropic providers
- Bring Your Own API Key (BYOK)
- Manual provider and model selection
- Secure API key storage
- Validated findings with file paths and line numbers
- Severity, descriptions and suggested fixes

## Requirements
- Visual Studio Code version supported by this extension
- Windows x64 for the bundled .NET bridge
- Git installed and available in PATH

## Getting Started
1. Install ReviewPR AI.
2. Open a Git repository.
3. Run ReviewPR AI: Configure Provider & Model.
4. Select your provider and model.
5. Configure your API key.
6. Run ReviewPR AI: Review with AI.

## Security
API keys must not be committed to source control or exposed in logs.

## Scope
Reviews local Git changes. This is not a hosted pull-request approval service.

## License
MIT License. See the repository LICENSE file.
