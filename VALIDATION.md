# Validation — 1.2.0

Validated in the Linux build environment:

- Windows x64 self-contained publish completed with .NET SDK 10.0.403, with no build warnings or errors after removing the unused WebView2 WPF adapter reference.
- 16 managed core checks passed: client-list parsing, invalid inputs, platform handling, and auto-attach tracking for new/known/disappeared clients, disabled discovery, duplicates, and failures. These checks never load the native DLL.
- 23 SDK checks passed with fake backends, covering cross-instance native-call serialization, queued cancellation, post-dispatch cancellation, targets, per-client and partial results, stale follow-up snapshots, auto-attach, attachment waits/deadlines, disposal, and multi-instance false-state handling. No native DLL was loaded.
- 18 offline module checks passed, including the actual pinned DLL's SHA256 and all 14 exports, synthetic valid/invalid images, architecture and DLL flags, missing exports, truncated/overflowing tables, invalid ordinals, strings, function RVAs, and forwarders. Inspection never loads native code.
- 12 settings checks passed: preference validation and persistence, editor controls, workspace recovery, reset preserving scripts, storage failures, indentation, and native ready/configure messages.
- 51 Chromium UI checks passed using a simulated `chrome.webview` bridge. They cover the original editor, client selection, error reporting, files, tabs, layout modes, close controls, Settings, saved preferences, and API file diagnostics while the module is unavailable.
- All 143 checked runtime color values are grayscale; settings markup and the absence of a loaded demo bridge were checked.
- The pinned native DLL was statically identified as PE x64 and its public exports were checked against the bindings. Its SHA256 matches `native/PROVENANCE.md`.
- The standalone SDK example restored the bundled `Ryn.Executor.Api.1.2.0.nupkg`, compiled, and ran its default offline diagnostics successfully. It identified the pinned module as statically compatible and the Linux host as unsupported for native operations.

Not validated here: Windows window management/UAC, WebView2-to-native integration on Windows, PowerShell script execution, the DLL's updater, live Roblox attachment, or live script execution. The provided DLL is a closed-source third-party component. A successful API return reports dispatch, not completion of a script.

The preview images use simulated client state and are not evidence of a live Roblox attachment. Native polling and auto-attachment still require verification with Roblox on Windows.
