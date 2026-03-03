# Dogfood Checklist

Use this checklist before relying on a runtime for real code-edit tasks.

## Acceptance Bar

A runtime is dogfood-ready for the core development workflow only if all of these pass:

1. `basic` smoke:
   `dotnet run --project project\tools\RealCliSmoke\RealCliSmoke.csproj -- <runtime> 8 basic`
2. `review-pass` smoke:
   `dotnet run --project project\tools\RealCliSmoke\RealCliSmoke.csproj -- <runtime> 8 review-pass`
3. `review-fail` smoke:
   `dotnet run --project project\tools\RealCliSmoke\RealCliSmoke.csproj -- <runtime> 8 review-fail`
4. The runtime stays inside the task worktree on retries.
5. The final transcript contains a valid `<giant-isopod-result>` envelope.
6. `review-pass` merges exactly the expected file.
7. `review-fail` does not merge into `main`.

## Current Targets

- `pi`: required
- `kimi`: required

## Failure Buckets

Classify failures into one bucket before changing code:

- `launch`: runtime executable, args, env, or encoding failure
- `prompt`: runtime ignored or misread the task contract
- `envelope`: runtime edited files but did not return a parseable result envelope
- `workspace`: task or retry ran outside the assigned worktree
- `artifact`: edits happened but artifact detection/registration was wrong
- `review`: validator or revision loop behaved incorrectly
- `merge`: merge/release behavior was wrong after validation

## Current Priority

1. Make `kimi` pass all three smoke scenarios.
2. Re-run `pi` after any shared runtime or parser changes.
3. Only after both are green, use them for real dogfood tasks.
