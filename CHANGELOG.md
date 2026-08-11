# Changelog

All notable changes to LocalAI Developer are documented in this file.

## 1.4.0 - 2026-08-11

- Prepared the extension for Visual Studio Marketplace distribution.
- Added Marketplace metadata, repository links, overview, and privacy documentation.
- Improved project-aware context and validation for C#, Python, PHP, JavaScript, TypeScript, and web/configuration files.
- Added project-local Python environment discovery and deterministic fallback validation.
- Excluded virtual environments and dependency caches from AI context budgets.
- Improved completed-session transaction history and rollback visibility.
- Skipped Roslyn builds for solutions that contain no C# projects.

## 1.3.6 - 2026-08-11

- Added transaction details for completed developer sessions.
- Improved Python interpreter resolution and validation behavior.

Earlier versions were development previews distributed as local VSIX packages.
