# LocalAI Developer for Visual Studio 2022

![LocalAI Developer logo](images/localai-developer-logo.png)

LocalAI Developer brings reviewable AI-assisted development workflows directly into Visual Studio 2022. It can work with local providers such as LM Studio and Ollama as well as configured remote providers, while keeping patch approval, validation, rollback, and session history under your control.

## Highlights

- Turn a development goal into a structured, persistent plan.
- Review every generated change as a colorized diff before applying it.
- Apply or skip individual steps and roll back completed transactions.
- Validate C# with Roslyn and run language-aware checks for Python, PHP, JavaScript, TypeScript, JSON, XML/XAML, HTML, and CSS.
- Compile once after the relevant plan steps instead of after every changed file.
- Retain failed changes for inspection and trigger rollback manually.
- Use bounded repair attempts after validation or compilation failures.
- Resolve relevant project context within provider-specific token and character budgets.
- Create optional Git branches, commits, pushes, and GitHub pull requests.
- Use an English or German interface that follows the active Visual Studio theme.

## Supported providers

- LM Studio
- Ollama
- Mistral
- OpenAI
- Custom OpenAI-compatible endpoints

Provider URLs, models, API keys, timeouts, approval behavior, and custom provider profiles are configured under **Tools > LocalAI Developer Settings**. The main developer window only exposes the active provider selector.

## Safety and privacy

Generated changes are confined to the selected workspace and validated before application. Approval can be required for every patch. Applied changes are stored as transactions and remain available for inspection and manual rollback.

Source context and prompts are sent only to the provider endpoint selected by the user. API keys are protected with Windows DPAPI for the current Windows user. The extension does not intentionally collect telemetry. See the [privacy statement](https://github.com/Mastaaa1987/localai-developer-visualstudio/blob/main/PRIVACY.md) for details.

## Requirements

- Visual Studio 2022 17.x (Community, Professional, or Enterprise)
- .NET 8 runtime
- Access to at least one supported LLM endpoint

Optional language runtimes such as Python, PHP, Node.js, or TypeScript are used for their respective validation backends when available.

## Getting started

1. Install the extension and restart Visual Studio.
2. Open **Tools > LocalAI Developer Settings**.
3. Configure a provider and use **Load models** to discover its models.
4. Open a solution or project.
5. Choose **Tools > LocalAI Developer**, enter a goal, and create a plan.
6. Review each proposed patch before applying it.

Documentation, source code, and issue tracking are available in the [GitHub repository](https://github.com/Mastaaa1987/localai-developer-visualstudio).

## License

LocalAI Developer is open source under the [MIT License](https://github.com/Mastaaa1987/localai-developer-visualstudio/blob/main/LICENSE.txt).
