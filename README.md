# RYN Executor Suite 1.2.0

A Windows x64 desktop application using your supplied HTML interface and the [Quorum External Module API](https://github.com/fguy06790-code/External). It provides a Lua editor, per-client or all-client targeting, attach/detach, script dispatch, file open/save, client refresh, and an activity log.

The **Classic** and **Pro** options change the layout. Both use the same public Quorum API and capabilities; Pro does not unlock premium API functions. The script library contains bundled community ScriptBlox examples with local filtering. **Load** places a snippet in the editor for review; it does not execute it or query a live ScriptBlox database.

## Run

1. Install the [Microsoft Edge WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section) and the [Microsoft Visual C++ v14 x64 Redistributable](https://aka.ms/vc14/vc_redist.x64.exe) if they are not already installed. The pinned native module imports the Visual C++ runtime.
2. Extract the entire Windows release ZIP to a new folder and run `ExternalExecutor.exe`. The upstream API requires administrator privileges, so Windows displays a UAC prompt.
3. Open Roblox and join a game. Choose a layout, refresh the clients, select one client or all clients, then attach.
4. Enter or open a Lua script and execute it.

Keep the extracted files together, including `Assets/`, the WebView2 libraries, and `Quorum-External-Module.dll` beside the executable. The self-contained release includes the .NET runtime and does not require a separate .NET installation. The WebView2 browser runtime is a separate requirement.

For an upgrade, close the previous version and extract 1.2.0 into a new folder. Preferences and workspace recovery remain in the existing WebView2 profile at `%LOCALAPPDATA%\RYN\WebView2`, so changing the application folder preserves that profile.

The HTML, CSS, fonts, and icons are bundled locally, so rendering the interface does not require a CDN.

## Settings

Choose **Midnight**, **Graphite**, or **Paper** for a pure grayscale theme. Editor settings control font size, line numbers, word wrap, and tab width. Settings also controls motion and workspace recovery. Preferences and recovered scripts are stored locally in the WebView2 profile. **Reset settings** restores the defaults while keeping open scripts.

Under **Connection**, **Auto-attach new clients** is off by default. When enabled, the native host attempts to attach newly discovered Roblox clients. **Automatic refresh** is on by default and checks clients every four seconds. With automatic refresh disabled, use **Refresh** to discover new clients, including clients eligible for auto-attach.

| Shortcut | Action |
| --- | --- |
| Ctrl+N | New script |
| Ctrl+O | Open script |
| Ctrl+S | Save script |
| Ctrl+Enter | Execute |
| F5 | Refresh clients |

The desktop app connects to the actual Quorum API. Its activity log reports API results; a successful dispatch means the API accepted the script. It does not confirm execution completed or display Roblox script output. The API receives ANSI strings, so characters outside the Windows system code page may not be preserved.

## Improved API SDK

The desktop app now uses the reusable **RYN Executor API** managed SDK. It adds a process-wide asynchronous queue, typed errors, immutable client snapshots, per-client batch results, partial-failure reporting, queued cancellation, and attachment waits. A successful script dispatch is preserved even if a later status refresh fails; stale client state is cleared from the interface. Execution is never retried automatically.

Choose **Settings → Connection → Check API files** for an offline inspection of the DLL's architecture, required exports, and SHA256. Inspection does not load the module or test attachment.

The standalone SDK download contains a local NuGet package, assemblies, the pinned native DLL, and a console example. See [SDK.md](SDK.md) for integration and error handling. The upstream repository includes a closed-source DLL and guide, so the SDK's execution capabilities and Roblox compatibility still depend on that native engine.

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run from PowerShell:

```powershell
./scripts/Fetch-Module.ps1
./scripts/Publish.ps1
```

`Fetch-Module.ps1` downloads the pinned upstream DLL and checks its SHA256. `Publish.ps1` runs the core, SDK, and static module checks and produces the Windows build plus `artifacts/ExternalExecutor-win-x64.zip` and `artifacts/RYN-Executor-API-SDK.zip`. Publishing includes the local frontend assets and WebView2 host libraries; it does not install or bundle the Evergreen browser runtime. To build only the SDK download, use `./scripts/Build-Sdk.ps1`.

For a development build:

```powershell
dotnet run --project tests/ExternalExecutor.Core.Checks/ExternalExecutor.Core.Checks.csproj -c Release
dotnet build src/ExternalExecutor.App/ExternalExecutor.App.csproj -c Release
```

The Windows app hosts `src/ExternalExecutor.App/Assets/index.html` in WebView2. A message bridge connects frontend controls to the managed host, which serializes calls to the native module.

The generated frontend CSS, fonts, and icons are checked in. After changing utility classes, rebuild CSS from `tools/ui-build/` with `npm ci` followed by `npm run css`. Browser interaction checks run with `npm test`; they need Chromium at `/usr/bin/chromium` or a path provided through `CHROMIUM_PATH`. They use a simulated desktop bridge and never load the DLL.

## API and verification

The native DLL is a closed-source third-party component, pinned to upstream commit `b2124f20ada06b595d1062064ef4c5af2d42add9`. Its source and checksum are recorded in [native/PROVENANCE.md](native/PROVENANCE.md); the original API guide is in `native/Tutorial.txt`.

The app uses the public API functions and does not require premium functions. **Update API** runs the module's own updater only when clicked; an update may replace the pinned version. Restart the application after updating. Bundled community snippets are separate from the Quorum API and may depend on individual Roblox experiences.

Cross-compilation and managed checks on Linux cannot verify attachment or execution in Roblox. Validate those functions on a Windows x64 machine with Roblox running. If attachment fails, verify the game is open and the app has administrator privileges. If the module cannot load despite being beside the executable, repair or install the Visual C++ x64 runtime.

