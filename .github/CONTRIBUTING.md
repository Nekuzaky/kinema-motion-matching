# Contributing to Kinema

Thanks for your interest in contributing! This project is a Unity motion matching toolkit, and contributions of all kinds are welcome: bug reports, bug fixes, new features, documentation, and tests.

## Getting started

1. Fork the repository and clone it locally.
2. Open the project with Unity 6000.3 or later (the package itself supports Unity 2021.3 LTS and later).
3. Install the "Locomotion Demo" sample if you want to try the Demo Scene (Tools > Kinema > Demo Scene).

## Development workflow

- Create a branch for your change: `git checkout -b feature/short-description`.
- Keep pull requests focused on a single change when possible.
- Follow the existing code style used in `Packages/com.nekuzaky.kinema`.
- Add or update EditMode tests in `Packages/com.nekuzaky.kinema/Tests/Editor` when you change matching, database, or trajectory logic.

## Running tests

Open Window > General > Test Runner (EditMode tab) in the Unity Editor, or run headless:

```
Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results.xml
```

## Submitting a pull request

1. Make sure existing tests pass and add new ones for behavior changes.
2. Describe what the change does and why in the PR description.
3. Link any related issues.
4. Be ready to discuss and iterate on feedback during review.

## Reporting bugs

Please open an issue with steps to reproduce, the Unity version you used, and any relevant logs or screenshots.

## Code of conduct

This project follows the [Code of Conduct](CODE_OF_CONDUCT.md). Please be respectful and constructive in all interactions.
