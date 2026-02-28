# giant-isopod

A Godot 4.6 project.

## Prerequisites

- [Godot 4.6](https://godotengine.org/) with .NET support
- [Task](https://taskfile.dev/) for running project tasks
- [pre-commit](https://pre-commit.com/) for git hooks
- [GitVersion](https://gitversion.net/) for semantic versioning
- [git-cliff](https://git-cliff.org/) for changelog generation
- [Ruff](https://docs.astral.sh/ruff/) for Python linting/formatting

## Getting Started

```bash
# Install pre-commit hooks
task pre-commit:install

# List all available tasks
task
```

## Available Tasks

| Task | Description |
|------|-------------|
| `task version` | Show current version from GitVersion |
| `task changelog` | Generate CHANGELOG.md |
| `task changelog:preview` | Preview changelog in stdout |
| `task release` | Tag and release with auto-versioning |
| `task lint` | Run ruff linter |
| `task format` | Run ruff formatter |
| `task pre-commit` | Run all pre-commit hooks |
| `task secrets:scan` | Scan for secrets |

## License

[MIT](LICENSE)
