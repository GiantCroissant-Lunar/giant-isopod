# Contributing

Thanks for your interest in contributing.

## Getting Started

1. Fork the repository
2. Clone your fork
3. Install pre-commit hooks: `task pre-commit:install`
4. Create a feature branch: `git checkout -b feature/my-feature`

## Commit Convention

This project uses [Conventional Commits](https://www.conventionalcommits.org/). Please format your commit messages as:

```
type(scope): description
```

Types: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `chore`, `ci`

Examples:
- `feat: add player movement system`
- `fix(physics): resolve collision detection bug`
- `docs: update README with setup instructions`

## Pull Requests

1. Keep changes focused and atomic
2. Ensure pre-commit hooks pass: `task pre-commit`
3. Update documentation if needed
4. Use a descriptive PR title following the commit convention
