# HydraHook Manager

[![Build](https://github.com/nefarius/HydraHookManager/actions/workflows/build.yml/badge.svg)](https://github.com/nefarius/HydraHookManager/actions/workflows/build.yml)

A Windows GUI application for injecting HydraHook DLLs (and other compatible DLLs) into running processes. Built with .NET 10 and WPF UI.

## Features

- **Process list** — Browse running processes with executable icons, names, PIDs, architecture (32/64-bit), and window titles
- **Filter** — Search processes by name (type and press Enter or click Refresh)
- **DLL injection** — Select one or more DLLs and inject them into a target process using [nefarius/Injector](https://github.com/nefarius/Injector)
- **Architecture matching** — Automatically uses the correct Injector binary (x86 or x64) based on the target process

## Prerequisites

- .NET 10 SDK
- Windows

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run --project src/HydraHookManager
```

Or run the built executable from `src/HydraHookManager/bin/Debug/net10.0-windows/` (or `Release`).

## Usage

1. **Select a process** — Click a process in the list (e.g. a game executable)
2. **Add DLLs** — Click "Add DLL" and select one or more `.dll` files (e.g. HydraHook-ImGui.dll from [HydraHook](https://github.com/nefarius/HydraHook))
3. **Inject** — Click "Inject" to inject the DLLs into the selected process

## Injector Binaries

This application bundles [Injector v1.4.0](https://github.com/nefarius/Injector/releases/tag/v1.4.0) (x86 and x64) from the nefarius/Injector project. The binaries are included in `src/HydraHookManager/Resources/Injector/` and are copied to the output directory on build.

## HydraHook

[HydraHook](https://github.com/nefarius/HydraHook) is a DirectX API-hooking framework for rendering overlays in games. Build the HydraHook-ImGui sample (or your own host library) and inject the resulting DLL using this manager.

> [!CAUTION]
> Do not inject into games protected by anti-cheat. DLL injection can result in permanent bans from online services.

## Credits

This project uses the following dependencies:

- **[WPF-UI](https://github.com/lepoco/wpfui)** — Fluent Design UI for WPF by [lepo.co](https://lepo.co/)
- **[CliWrap](https://github.com/Tyrrrz/CliWrap)** — Fluent CLI process execution by [Tyrrrz](https://github.com/Tyrrrz)
- **[CsWin32](https://github.com/microsoft/CsWin32)** — Source-generated Win32 bindings by Microsoft
- **[Injector](https://github.com/nefarius/Injector)** — DLL injection/ejection CLI utility by [Nefarius](https://github.com/nefarius) (bundled)

## License

MIT
