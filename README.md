# <img src="assets/HydraHook_128x128.png" align="left" /> HydraHook Manager

[![Build](https://github.com/nefarius/HydraHookManager/actions/workflows/build.yml/badge.svg)](https://github.com/nefarius/HydraHookManager/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/nefarius/HydraHookManager)](https://github.com/nefarius/HydraHookManager/releases/latest)
![Made with AI](https://img.shields.io/badge/Made%20with%20AI-%F0%9F%A4%96-lightgrey)

Windows GUI for injecting DLLs into, and ejecting them from, running processes. Companion tool for [HydraHook](https://github.com/nefarius/HydraHook).

## Support scope

- OS: Windows 10 version 1809 (build 17763) or later, Windows 11.
- Architecture: x64 host application. Targets can be 32-bit or 64-bit processes.
- Runtime: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64). The release archive is framework-dependent and does not bundle the runtime.
- Not supported: Windows 7/8.x, ARM64 hosts, Linux, macOS.

## Features

- Process list with icon, name, PID, architecture (32/64-bit) and window title. The filter matches name, window title, PID, or executable path. Columns are sortable.
- Flat list or parent/child tree. A process is nested under a parent that is still in the list. When both creation times are known, the parent must have been created at or before the child, which rejects a reused parent PID. If either creation time is missing, the parent link is best-effort.
- Windowed-only filter. In the tree, a windowed process stays attached to its ancestor chain. A text match without a window is included only when it has a windowed descendant.
- Auto-refresh about every 1.5 seconds while the window is open and not minimized. It pauses while the window is minimized and runs again when the window is activated or restored. The Refresh button and F5 still refresh immediately.
- If the selected process exits and a newer instance of the same executable starts before another process is selected, that instance is selected. This follows a process only when its creation time was known.
- Inject and eject one or more DLLs via the bundled [nefarius/Injector](https://github.com/nefarius/Injector) v1.5.0; the matching x86 or x64 binary is picked from the target architecture.
- DLL list is persisted between sessions; add DLLs with a dialog or drag and drop.
- Window size, position, state, tree mode, auto-refresh, and the windowed filter are persisted.
- "Restart as Administrator" button when injection fails for access reasons.
- Update check against GitHub Releases (at most once per day; can be disabled in `settings.json`) and a manual "Check for updates" button.
- Rolling log files (14 days) with a button that opens the log folder.
- Release binaries are Authenticode-signed.

## Limitations

- Injection into protected or elevated processes requires running the manager elevated.
- The injector does not bypass anti-cheat, protected processes or PPL.
- The process tree follows parent PIDs present in the current snapshot. A parent that has already exited is omitted. Auto-refresh polls that snapshot.
- Multi-monitor window placement is validated against the virtual screen bounds only.
- Update check only notifies; it does not download or install anything.

> [!CAUTION]
> Do not inject into games protected by anti-cheat. DLL injection can result in permanent bans from online services.

## Install and use

1. Install the .NET 10 Desktop Runtime (x64).
2. Download `HydraHookManager-vX.Y.Z-win-x64.zip` from [Releases](https://github.com/nefarius/HydraHookManager/releases/latest).
3. Verify the archive against `checksums.txt` (SHA-256).
4. Extract to a writable folder and run `HydraHookManager.exe`.
5. Select a process, add DLLs, click **Inject** (or **Eject** to unload them).

Data locations (`%LocalAppData%\HydraHookManager`):

- `settings.json` - DLL list, sort order, tree mode, auto-refresh, windowed filter, window placement, update-check settings.
- `Logs\` - Serilog rolling log files.

## Build (contributors)

Prerequisites:

- Windows 10/11 x64
- .NET SDK 10.0.100 or later feature band (pinned in `global.json`, `rollForward: latestFeature`)
- Git

Steps:

```powershell
git clone https://github.com/nefarius/HydraHookManager.git
cd HydraHookManager
dotnet tool restore
dotnet restore HydraHookManager.slnx
dotnet build HydraHookManager.slnx -c Release --no-restore
dotnet test HydraHookManager.slnx -c Release --no-build
dotnet publish src/HydraHookManager/HydraHookManager.csproj -c Release -r win-x64 --no-self-contained -o artifacts/publish
```

Notes:

- Versioning is derived from git tags (`vX.Y.Z`, `vX.Y.Z-preN`) by [MinVer](https://github.com/adamralph/minver). Untagged builds get a pre-release version.
- Packages are centrally managed in `Directory.Packages.props`. Warnings are errors.
- Unit tests (xUnit) cover view-model logic, injector argument handling, settings persistence, window placement and update-check policy.

## Releases and CI

`.github/workflows/build.yml` runs on pushes and pull requests to `master` and on `v*` tags:

- Gitleaks and Semgrep scans.
- Restore, build, test, NuGet vulnerability scan, publish.
- On tags only: verify the tagged commit is on `master` and matches the MinVer version, sign `HydraHookManager.exe` and `HydraHookManager.dll` with [SignRelay](https://github.com/nefarius/SignRelay), package the ZIP and `checksums.txt`, mirror to Buildbot, publish a GitHub Release.

Repository configuration for tagged releases:

- Secret `SIGN_RELAY_CI_TOKEN` (required).
- Variable `SIGN_RELAY_SERVER` (optional; defaults to `https://signrelay.api.nefarius.systems/`).
- Secret `WEBHOOK_URL` (optional; enables the Buildbot mirror).

The bundled Injector binaries are already signed upstream and are not re-signed.

## Support policy

- Bugs: open a [GitHub issue](https://github.com/nefarius/HydraHookManager/issues) with the log file attached.
- Only the latest release is supported.
- No support for anti-cheat bypass or use against online services.

## Injector binaries

Bundles [Injector v1.5.0](https://github.com/nefarius/Injector/releases/tag/v1.5.0) (x86 and x64) in `src/HydraHookManager/Resources/Injector/`, copied to the output directory on build.

## HydraHook

[HydraHook](https://github.com/nefarius/HydraHook) is a DirectX API-hooking framework for rendering overlays in games. Build the HydraHook-ImGui sample (or your own host library) and inject the resulting DLL with this manager.

## License

[MIT](LICENSE). Copyright (c) 2026 Benjamin Hoeglinger-Stelzer.

## Credits

- [WPF-UI](https://github.com/lepoco/wpfui) - Fluent UI for WPF by [lepo.co](https://lepo.co/)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - MVVM helpers by Microsoft
- [CliWrap](https://github.com/Tyrrrz/CliWrap) - CLI process execution by [Tyrrrz](https://github.com/Tyrrrz)
- [CsWin32](https://github.com/microsoft/CsWin32) - Source-generated Win32 bindings by Microsoft
- [Serilog](https://serilog.net/) - structured logging
- [MinVer](https://github.com/adamralph/minver) - git-tag versioning
- [Injector](https://github.com/nefarius/Injector) - DLL injection/ejection CLI by [Nefarius](https://github.com/nefarius) (bundled)
