# Contributing

Thank you for contributing to LocalAI Developer.

## Development Workflow

1. Create a focused branch.
2. Keep UI, workflow, and filesystem responsibilities separated.
3. Add or update tests for behavioral changes.
4. Run the backend test suite.
5. Build the VSIX project with zero errors.
6. Test the extension in the Visual Studio Experimental Instance.

## Pull Requests

- Explain the user-facing problem and the chosen solution.
- Keep patches small and reviewable.
- Include test evidence.
- Do not commit API keys, generated sessions, build output, or user-specific settings.
- Preserve English and German localization for new UI text.

## Architecture Rules

- The WPF tool window should remain UI-focused.
- Workflow state belongs in the .NET 8 backend.
- Filesystem changes must be transactional and reversible.
- LLM output must be parsed and validated before use.
- External commands must use registered, constrained operations rather than model-supplied shell commands.
