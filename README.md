# LocalAI Developer for Visual Studio 2022

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.txt)

LocalAI Developer is a Visual Studio 2022 extension for AI-assisted software development with local or remote language models. It turns a development goal into a reviewable plan, generates one patch at a time, validates C# with Roslyn, builds the solution once at the end of the workflow, and can request bounded repair patches when compilation fails.

The extension supports English and German and follows the active Visual Studio color theme.

## Features

- Structured development plans with step and plan status
- Side-by-side review workflow with unified, colorized diffs
- Manual approval, skip, cancel, and rollback actions
- Atomic transaction history, including rollback of completed workflows
- Roslyn syntax validation before patch approval
- Deferred solution compilation after the final relevant step
- Bounded automatic repair and recompilation
- Persistent developer sessions and structured history
- Token and character budget estimates per provider/model profile
- Optional Git branch, commit, push, and GitHub pull-request workflow
- Automatic workspace selection from the open Visual Studio solution
- English and German UI
- Dark, Light, and Blue Visual Studio theme support

## Providers

- LM Studio (OpenAI-compatible API)
- Ollama (native API)
- Mistral
- OpenAI

The provider interface is intentionally transport-agnostic. Custom OpenAI-compatible endpoints can be added directly under **Tools > LocalAI Developer Settings** without changing the workflow coordinators.

## Architecture

```text
Visual Studio 2022 VSIX (.NET Framework 4.7.2 / WPF)
        |
        | JSON-RPC over stdin/stdout
        v
LocalAI Developer Backend (.NET 8)
        |
        +-- Planning and plan validation
        +-- Patch preparation and approval policy
        +-- Transactional apply and rollback
        +-- Roslyn syntax/build validation
        +-- Repair coordination
        +-- Session and history persistence
        +-- Optional Git/GitHub workflow
```

The Visual Studio extension is a UI host. Workflow state and filesystem mutations live in the backend so approval, execution, compilation, repair, and persistence remain separated.

## Repository Layout

```text
src/
  LocalAI.Developer.VisualStudio/   Visual Studio 2022 VSIX and WPF UI
  LocalAI.Developer.Backend/        .NET 8 workflow backend
tests/
  LocalAI.Developer.Backend.Tests/  End-to-end and component tests
LocalAI.Developer.sln
```

## Requirements

- Visual Studio 2022 17.x
- Visual Studio extension development workload
- .NET 8 SDK/runtime
- A supported LLM endpoint
- Git (optional)
- GitHub CLI `gh` (optional, only for pull-request creation)

## Build

1. Clone the repository.
2. Open `LocalAI.Developer.sln` in Visual Studio 2022.
3. Build `LocalAI.Developer.VisualStudio` in Debug or Release.

The VSIX build publishes the .NET 8 backend and embeds it under `Backend/` inside the extension package.

Command-line build:

```powershell
& "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" `
  .\src\LocalAI.Developer.VisualStudio\LocalAI.Developer.VisualStudio.csproj `
  /t:Rebuild /p:Configuration=Release
```

## Test

```powershell
dotnet run --project .\tests\LocalAI.Developer.Backend.Tests\LocalAI.Developer.Backend.Tests.csproj -c Release
```

## Run in the Experimental Instance

1. Set `LocalAI.Developer.VisualStudio` as the startup project.
2. Press `F5`.
3. Open a solution in the Visual Studio Experimental Instance.
4. Choose **Tools > LocalAI Developer**.

## Install

Build the VSIX, close all Visual Studio instances, and open:

```text
src/LocalAI.Developer.VisualStudio/bin/Release/LocalAI.Developer.VisualStudio.vsix
```

Restart Visual Studio and open **Tools > LocalAI Developer**.

## Configuration

Open **Tools > LocalAI Developer Settings** to configure provider URLs, models, API keys, language, approval mode, timeouts, and Git policy. Use **Load models** beside the editable model dropdown to discover models from the selected provider. The same dialog lets you add, rename, and remove custom providers. API keys are protected with Windows DPAPI for the current user.

The main **LocalAI Developer** window deliberately exposes only the active provider selector. Selecting another provider stores it as the active profile; connection details remain centralized in the settings dialog.

Environment variables:

- `LOCALAI_LMSTUDIO_API_KEY`
- `LOCALAI_MISTRAL_API_KEY`
- `LOCALAI_OPENAI_API_KEY`
- `LOCALAI_API_KEY`
- `LOCALAI_WORKSPACE` (fallback only when no solution is open)

## Safety Model

- Generated paths must remain inside the selected workspace.
- Patches are validated before approval and application.
- Risk-based approval can require explicit review for every patch.
- Compilation failures retain applied files for inspection until the user requests rollback.
- Git push and pull-request creation are always separate user actions.
- Repair attempts and patch regeneration attempts are bounded.

## Localization

English and German are included. Select the language under **Tools > LocalAI Developer Settings**. Compiler output and provider error payloads remain unchanged so diagnostic text is not corrupted by translation.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the development workflow and pull-request checklist.

## License

LocalAI Developer is licensed under the [MIT License](LICENSE.txt).
Third-party components retain their respective licenses; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
