# ReviewPR AI

AI-powered Git code review for Visual Studio Code and Microsoft Visual Studio 2022.

## Features
- AI-powered Git diff review
- Manual Review with AI
- Bring Your Own API Key (BYOK)
- Gemini, Groq, OpenRouter, OpenAI and Anthropic
- Manual provider and model selection
- Secure API key storage
- Validation of findings against changed lines
- File paths, line numbers, severity and suggested fixes

## Supported IDEs
- Visual Studio Code
- Microsoft Visual Studio 2022

## Architecture
- VS Code extension
- Visual Studio extension
- Shared .NET IDE bridge

## Getting Started
1. Open a Git repository in your IDE.
2. Configure your AI provider and model.
3. Configure your own API key.
4. Run ReviewPR AI: Review with AI.

## Security
- Never commit API keys or credentials.
- Review AI-generated findings before applying suggested fixes.

## Scope
ReviewPR AI currently reviews local Git changes. It is not a hosted pull-request approval service.

## License
MIT License. See LICENSE.
