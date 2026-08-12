# Privacy Statement

AI Code Generator does not intentionally collect telemetry, analytics, or usage data.

## Data processed by the extension

To perform a requested development workflow, the extension may read project files inside the selected workspace and send a bounded selection of relevant source context, the user's goal, workflow prompts, and diagnostic output to the language-model provider selected by the user.

The destination and data handling therefore depend on the configured provider. A local LM Studio or Ollama endpoint can keep model requests on the user's machine. Remote providers receive requests according to their own terms and privacy policies. Users should review those policies before sending confidential source code.

## Credentials

Provider API keys entered in the extension are protected with Windows Data Protection API (DPAPI) for the current Windows user. Credentials are not stored in the project workspace or intentionally written to workflow logs.

## Local data

Developer sessions, workflow history, settings, and transaction metadata are stored locally for workflow continuation, inspection, and rollback. Users can remove these local records through the product or their Windows user profile.

## External tools

Optional Git push and GitHub pull-request operations run only after explicit user actions. Language validators and build tools may execute installed local runtimes such as .NET, Python, PHP, Node.js, or TypeScript.

## Contact

Questions and privacy reports can be submitted through the project's [GitHub issue tracker](https://github.com/Mastaaa1987/ai-code-generator-visualstudio/issues).
