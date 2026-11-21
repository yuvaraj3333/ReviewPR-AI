# ReviewPR AI for Microsoft Visual Studio 2022

AI-powered Git code review inside Microsoft Visual Studio 2022.

## Features
- AI-powered review of local Git changes
- Gemini, Groq, OpenRouter, OpenAI and Anthropic providers
- Bring Your Own API Key (BYOK)
- Manual provider and model selection
- Secure API key storage
- Validated findings against changed lines
- Severity, file paths, line numbers and suggested fixes

## Requirements
- Microsoft Visual Studio 2022
- Windows x64
- Git installed and available in PATH

## Getting Started
1. Install the ReviewPR AI VSIX.
2. Open a Git repository in Visual Studio.
3. Configure your AI provider and model.
4. Configure your API key.
5. Run Review with AI.

## Security
Never commit API keys or credentials. Review AI-generated findings before applying suggested changes.

## Scope
Reviews local Git changes. It is not a hosted pull-request approval service.

## License
MIT License. See the repository LICENSE file.
