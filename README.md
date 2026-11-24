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

- Process list with icon, name, PID, architecture (32/64-bit), graphics API, and window title. The filter matches name, window title, PID, or executable path. Columns are sortable.
- Flat list or parent/child tree. A process is nested under a parent that is still in the list. When both creation times are known, the parent must have been created at or before the child, which rejects a reused parent PID. If either creation time is missing, the parent link is best-effort.
- Windowed, Direct3D, and Injected filters. The chips combine. In the tree, a matching process stays attached to its ancestor chain. A text match that fails the active chips is included only when a matching descendant hangs off it.
- Access marks on the name: a shield when the process needs an administrator, a lock when it stays unreadable even when elevated.
- Injection marks that survive a manager restart. A verified mark means the DLL was seen loaded. A tracked mark means it is recorded in the ledger but the module list could not be read. Right-click a process to inject, eject, watch that executable, inspect loaded modules, bring its window forward, open the executable location, or copy the PID or path.
- Named profiles store a DLL list, the check boxes, and an optional watch target. The manager loads the active profile on startup.
- Watch & Auto-Inject follows one executable. The trigger is on launch, when a window opens, or when Direct3D loads, plus an optional delay from 0 to 30000 ms. Processes already running when you arm it are left alone. Each new instance, identified by PID and creation time, is injected at most once. The watch stays armed for the next launch.
- Auto-refresh about every 1.5 seconds while the window is open and not minimized. It pauses while the window is minimized and runs again when the window is activated or restored. While a watch is armed, refresh continues about every 0.75 seconds, including while the window is minimized. The Refresh button and F5 still refresh immediately. The manager always starts with the watch disarmed.
- If the selected process exits and a newer instance of the same executable starts before another process is selected, that instance is selected. This follows a process only when its creation time was known.
- Inject and eject one or more DLLs via the bundled [nefarius/Injector](https://github.com/nefarius/Injector) v1.5.0; the matching x86 or x64 binary is picked from the target architecture.
- DLL list is persisted between sessions, including which rows are checked. Add DLLs with a dialog or drag and drop. Each row shows the PE architecture, a warning when the file is missing or not a valid PE, and a Loaded mark when that DLL is loaded in the selected process.
- Inject and eject use the checked DLLs that match the selected process architecture. An x86 DLL and an x64 DLL can both stay checked; the other architecture is skipped for that process. Eject still sends a checked DLL that is no longer on disk.
- The process list and DLL list split can be dragged. The DLL pane height is saved with the window.
- Window size, position, state, DLL pane height, tree mode, auto-refresh, and the windowed, Direct3D, and injected filters are persisted.
- "Restart as Administrator" button when injection fails for access reasons.
- Update check against GitHub Releases (at most once per day; can be disabled in `settings.json`) and a manual "Check for updates" button.
- Rolling log files (14 days) with a button that opens the log folder.
- Release binaries are Authenticode-signed.

## Limitations

- Injection into protected or elevated processes requires running the manager elevated.
- The injector does not bypass anti-cheat, protected processes or PPL.
- Loaded-module scans use `PROCESS_VM_READ` and run for windowed, selected, and ledger-tracked processes. The Direct3D filter scans every process whose creation time is known. A watch set to wait for Direct3D also scans that executable when it has no window. Protected processes are not scanned. A non-elevated manager skips processes that already denied a limited query. A module scan can lag one refresh behind the process list.
- Watch does not inject a process that is already running when you arm it, and it does not retry an instance it already tried. Arming is not restored after a restart.
- The process tree follows parent PIDs present in the current snapshot. A parent that has already exited is omitted. Auto-refresh polls that snapshot.
- DLL architecture comes from the PE machine type (`x86`, `x64`, `ARM64`). A DLL whose machine type is something else is not sent to a process whose architecture is known. ARM64 processes are not a supported injection target; the host and bundled injector are x86 and x64 only.
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
6. Save a named profile to keep that DLL list. **Watch...** opens setup. Check at least one DLL, choose the executable and trigger, then click **Arm Watch**. Use **Stop** on the banner to disarm it.

Data locations (`%LocalAppData%\HydraHookManager`):

- `settings.json` - DLL list and check boxes, named profiles, the active profile, sort order, tree mode, auto-refresh, windowed, Direct3D, and injected filters, window placement, DLL pane height, update-check settings. A profile can store a watch target. Whether the watch is armed is not saved. Older files that only have `DllPaths` load with every DLL checked.
- `state.json` - injected DLL ledger keyed by process id and creation time. It is separate from settings and is pruned when the process exits or the DLL is no longer loaded.
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
