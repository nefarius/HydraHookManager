# AGENTS.md

Guidance for AI coding agents working in this repository.

## Branch and pull request workflow

Do not implement or commit work on `master`. Before editing, confirm the current branch is the requested work. If it is not, create a branch from up-to-date `master` (`feat/…`, `fix/…`, or `chore/…`) and do the work there. Open a pull request. Do not push commits directly to `master`.

When a reviewer (human or bot) leaves inline findings, verify each claim against the current code before changing it. Fix findings that are still valid. Leave pre-existing or out-of-scope items alone and say why. Do not merge on a green `build` check alone: every review thread for the current head commit must be resolved.

## Layout

- `src/HydraHookManager`: WPF app (.NET 10, `net10.0-windows`, WPF-UI, Generic Host, Serilog).
  - `Models/`: pure, unit-testable policies and data (`ProcessListPolicy`, `WindowPlacementPolicy`, `UpdateCheckPolicy`, `AppSettings`).
  - `Services/`: process enumeration (CsWin32), Injector wrapper (CliWrap), settings, elevation, update check.
  - `ViewModels/`: `MainWindowViewModel` (CommunityToolkit.Mvvm). Keep code-behind view-only.
- `tests/HydraHookManager.Tests`: xUnit tests. Behavior changes need a matching test.
- Solution is `HydraHookManager.slnx`. Package versions live in `Directory.Packages.props`. Warnings are errors.

## Build and test

```text
dotnet tool restore
dotnet restore HydraHookManager.slnx
dotnet build HydraHookManager.slnx -c Release --no-restore
dotnet test HydraHookManager.slnx -c Release --no-build
```

## Releases

Tag `vX.Y.Z` or `vX.Y.Z-prerelease`. `.github/workflows/build.yml` builds, tests, signs the app binaries through SignRelay, and publishes `HydraHookManager-vX.Y.Z-win-x64.zip` plus `checksums.txt`.

Repository configuration for releases:

- Secret `SIGN_RELAY_CI_TOKEN` (required for tags)
- Variable `SIGN_RELAY_SERVER` (optional, defaults to `https://signrelay.api.nefarius.systems/`)
- Secret `WEBHOOK_URL` (optional, Buildbot mirror)

## Source constraints

- New and edited source and config files are UTF-8 without BOM.
- The bundled `Injector.exe` binaries under `src/HydraHookManager/Resources/Injector/` are third-party, already signed, and must not be re-signed.
- README changes follow `.cursor/rules/readme-style.mdc`.
