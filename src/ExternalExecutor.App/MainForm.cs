using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ExternalExecutor.Core;
using Ryn.Executor;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ExternalExecutor.App;

internal sealed class MainForm : Form
{
    private const string AppOrigin = "https://ryn.local";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(9, 9, 9) };
    private readonly ExecutorClient _executor;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };
    private readonly HashSet<string> _approvedFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RobloxClient> _clients = [];
    private bool _busy;
    private bool _frontendReady;
    private bool _moduleAvailable;
    private bool _autoAttach;
    private bool _autoRefresh = true;
    private bool _pollDisabled;
    private bool _allowClose;
    private bool _askingToClose;
    private bool _stopped;
    private string? _lastPollError;

    public MainForm(ExecutorClient executor)
    {
        _executor = executor;
        Text = "RYN Executor Suite";
        BackColor = Color.FromArgb(9, 9, 9);
        ClientSize = new Size(1100, 800);
        MinimumSize = new Size(900, 650);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(_view);
        Shown += async (_, _) => await InitializeBrowserAsync();
        _timer.Tick += async (_, _) =>
        {
            if (!_busy && !_pollDisabled && _frontendReady && _autoRefresh)
            {
                try { await RunNativeAsync("refresh", default, true); }
                catch (Exception ex)
                {
                    if (_lastPollError != ex.Message) SendLog(Explain(ex), "error");
                    _lastPollError = ex.Message;
                }
            }
        };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => { _stopped = true; _timer.Dispose(); _executor.Dispose(); };
    }

    // Preserve native resize borders while using the titlebar supplied in the HTML.
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~0x00C00000; // WS_CAPTION
            return parameters;
        }
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            if (!File.Exists(Path.Combine(assets, "index.html")))
                throw new FileNotFoundException("The Assets folder is missing. Extract the complete release archive before launching the app.");
            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RYN", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData);
            if (_stopped) return;
            await _view.EnsureCoreWebView2Async(environment);
            var browser = _view.CoreWebView2;
            browser.Settings.AreHostObjectsAllowed = false;
            browser.Settings.AreDevToolsEnabled = false;
            browser.Settings.AreDefaultContextMenusEnabled = false;
            browser.Settings.AreBrowserAcceleratorKeysEnabled = false;
            browser.Settings.IsStatusBarEnabled = false;
            browser.Settings.IsWebMessageEnabled = true;
            browser.SetVirtualHostNameToFolderMapping("ryn.local", assets, CoreWebView2HostResourceAccessKind.DenyCors);
            browser.NavigationStarting += (_, e) =>
            {
                if (!IsAppPage(e.Uri)) e.Cancel = true;
            };
            browser.NewWindowRequested += (_, e) => e.Handled = true;
            browser.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.ProcessFailed += (_, _) =>
            {
                DisableBridge();
                if (!_stopped)
                    MessageBox.Show(this, "The WebView2 browser stopped. Close and restart RYN to recover your workspace.", "RYN browser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            browser.WebMessageReceived += OnWebMessageReceived;
            browser.Navigate(AppOrigin + "/index.html");
        }
        catch (Exception ex)
        {
            _allowClose = true;
            var message = ex is WebView2RuntimeNotFoundException
                ? "Install the Microsoft Edge WebView2 Evergreen Runtime, then launch RYN again.\n\nhttps://developer.microsoft.com/microsoft-edge/webview2/"
                : "Could not start RYN:\n\n" + ex.Message;
            MessageBox.Show(this, message, "RYN startup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private static bool IsAppPage(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == "ryn.local" && uri.IsDefaultPort
        && uri.AbsolutePath == "/index.html";

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_stopped || !IsAppPage(e.Source)) return;
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 100) throw new ArgumentException("Missing request ID.");
            var command = root.GetProperty("command").GetString() ?? "";
            var payload = root.TryGetProperty("payload", out var value) ? value.Clone() : default;
            object? result;
            switch (command)
            {
                case "ready":
                    _frontendReady = true;
                    ConfigureConnectionSettings(payload, requireAll: false);
                    PushState();
                    SendLog("RYN ready. Open a Roblox experience, select a client, and attach.");
                    SendLog("The API reports script dispatch only; check Roblox's client log for script output.");
                    result = await RunNativeAsync("refresh", payload);
                    break;
                case "configure":
                    result = ConfigureConnectionSettings(payload, requireAll: true);
                    PushState();
                    break;
                case "diagnostics":
                    result = await InspectModuleAsync();
                    break;
                case "refresh": case "attach": case "detach": case "execute": case "openRoblox": case "updateApi":
                    result = await RunNativeAsync(command, payload);
                    break;
                case "openFile": result = OpenFile(); break;
                case "saveFile": result = SaveFile(payload); break;
                case "minimize": WindowState = FormWindowState.Minimized; result = new { }; break;
                case "maximize":
                    MaximizedBounds = Screen.FromControl(this).WorkingArea;
                    WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                    result = new { }; break;
                case "drag":
                    if (WindowState != FormWindowState.Maximized)
                    {
                        ReleaseCapture();
                        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); // WM_NCLBUTTONDOWN, HTCAPTION
                    }
                    result = new { }; break;
                case "close": Close(); result = new { }; break;
                case "confirmClose":
                    _askingToClose = false;
                    if (payload.TryGetProperty("allow", out var allow) && allow.ValueKind == JsonValueKind.True)
                    {
                        _allowClose = true;
                        Close();
                    }
                    result = new { }; break;
                default: throw new ArgumentException("Unknown application command.");
            }
            Send(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            var error = Explain(ex);
            SendLog(error, "error");
            if (id is not null) Send(new { id, ok = false, error, errorCode = ex is ApiFailureException failure ? failure.Error.Code.ToString() : "InvalidRequest" });
        }
    }

    private async Task<object> RunNativeAsync(string command, JsonElement payload, bool quiet = false)
    {
        if (_busy) throw new InvalidOperationException("An API operation is already running. Wait for it to finish.");
        _busy = true;
        PushState();
        try
        {
            string message;
            if (command == "refresh")
            {
                var refresh = await _executor.RefreshAsync(_autoAttach);
                ApplySnapshot(refresh.Value ?? _executor.LastSnapshot);
                if (!refresh.Success) throw Failure(refresh.Error);
                foreach (var attempt in _executor.LastSnapshot.AutoAttachAttempts)
                    SendLog(attempt.Success
                        ? $"Auto-attach accepted for PID {attempt.ProcessId}."
                        : $"Auto-attach failed for PID {attempt.ProcessId}: {attempt.Error}",
                        attempt.Success ? "info" : "error");
                message = _executor.LastSnapshot.IsStale
                    ? "Attach requests returned; current client status is unavailable."
                    : "Client list refreshed.";
                if (_executor.LastSnapshot.IsStale)
                    SendLog("Client refresh failed: " + _executor.LastSnapshot.Error?.Message, "error");
                if (!quiet) SendLog(message);
            }
            else if (command is "attach" or "detach" or "execute")
            {
                var pid = ReadTarget(payload);
                var target = pid.HasValue ? ClientTarget.ForProcess(pid.Value) : ClientTarget.All;
                ExecutorResult<ClientBatchReport> result = command switch
                {
                    "attach" => await _executor.AttachAsync(target),
                    "detach" => await _executor.DetachAsync(target),
                    _ => await _executor.ExecuteAsync(payload.GetProperty("script").GetString() ?? "", target)
                };
                message = CompleteBatch(result);
            }
            else
            {
                var result = command switch
                {
                    "openRoblox" => await _executor.OpenRobloxAsync(),
                    "updateApi" => await _executor.UpdateApiAsync(),
                    _ => throw new ArgumentException("Unknown native command.")
                };
                if (!result.Success) throw Failure(result.Error);
                message = result.Value ?? "API operation accepted.";
                SendLog(message);
                // Refresh failure must not relabel a successful launch or updater call.
                var refresh = await _executor.RefreshAsync(_autoAttach);
                ApplySnapshot(refresh.Value ?? _executor.LastSnapshot);
                if (!refresh.Success || _executor.LastSnapshot.IsStale)
                    SendLog("Operation accepted; client refresh failed: " + (refresh.Error ?? _executor.LastSnapshot.Error)?.Message, "error");
            }
            return new { message };
        }
        catch (ApiFailureException ex)
        {
            ApplySnapshot(_executor.LastSnapshot);
            if (IsBindingFailure(ex.Error.Code)) { _moduleAvailable = false; _pollDisabled = true; }
            throw;
        }
        finally
        {
            _busy = false;
            PushState();
        }
    }

    private string CompleteBatch(ExecutorResult<ClientBatchReport> result)
    {
        var report = result.Value;
        ApplySnapshot(report?.Snapshot ?? _executor.LastSnapshot);
        if (report is not null)
        {
            foreach (var client in report.Clients)
                SendLog($"PID {client.ProcessId}: {client.Message}", client.Success ? "info" : "error");
            if (report.Snapshot.IsStale)
                SendLog("Client status is unavailable: " + report.Snapshot.Error?.Message, "error");
        }
        if (!result.Success) throw Failure(result.Error);
        return report?.Summary ?? "API operation accepted.";
    }

    private void ApplySnapshot(ClientSnapshot snapshot)
    {
        _clients = snapshot.IsStale ? [] : snapshot.Clients;
        if (!snapshot.IsStale) { _moduleAvailable = true; _pollDisabled = false; }
        else if (snapshot.Error is not null && IsBindingFailure(snapshot.Error.Code))
        {
            _moduleAvailable = false;
            _pollDisabled = true;
        }
        if (!snapshot.IsStale) _lastPollError = null;
    }

    private static bool IsBindingFailure(ExecutorErrorCode code) => code is
        ExecutorErrorCode.ModuleMissing or ExecutorErrorCode.DependencyMissing or
        ExecutorErrorCode.WrongArchitecture or ExecutorErrorCode.MissingExport or ExecutorErrorCode.UnsupportedPlatform;

    private static ApiFailureException Failure(ExecutorError? error) => new(error ??
        new ExecutorError(ExecutorErrorCode.NativeFailure, "The API operation failed without an error description."));

    private async Task<object> InspectModuleAsync()
    {
        if (_busy) throw new InvalidOperationException("Wait for the current API operation to finish before checking files.");
        _busy = true;
        PushState();
        try
        {
            var report = await Task.Run(() => ModuleInspector.Inspect());
            SendLog("RYN API SDK " + typeof(ExecutorClient).Assembly.GetName().Version + " — " + Path.GetFileName(report.ModulePath));
            if (report.IsCompatible)
                SendLog($"Static file check passed: Windows x64 DLL, {report.Exports.Count} exported functions.");
            foreach (var issue in report.Issues) SendLog(issue, "error");
            if (report.Sha256 is not null) SendLog("SHA256: " + report.Sha256);
            SendLog("File inspection does not load the DLL or test a Roblox connection.");
            return report;
        }
        finally
        {
            _busy = false;
            PushState();
        }
    }

    private object ConfigureConnectionSettings(JsonElement payload, bool requireAll)
    {
        // Validate the complete payload before changing either preference.
        var autoAttach = ReadBooleanSetting(payload, "autoAttach", _autoAttach, requireAll);
        var autoRefresh = ReadBooleanSetting(payload, "autoRefresh", _autoRefresh, requireAll);
        _autoAttach = autoAttach;
        _autoRefresh = autoRefresh;
        if (_frontendReady && _autoRefresh) _timer.Start(); else _timer.Stop();
        return new { autoAttach = _autoAttach, autoRefresh = _autoRefresh };
    }

    private static bool ReadBooleanSetting(JsonElement payload, string name, bool fallback, bool required)
    {
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var setting))
        {
            if (setting.ValueKind is JsonValueKind.True or JsonValueKind.False) return setting.GetBoolean();
            throw new ArgumentException($"The {name} preference must be true or false.");
        }
        if (required) throw new ArgumentException($"Missing {name} preference.");
        return fallback;
    }

    private static uint? ReadTarget(JsonElement payload)
    {
        if (!payload.TryGetProperty("pid", out var target)) throw new ArgumentException("Select a target client.");
        if (target.ValueKind == JsonValueKind.Null) return null;
        if (target.ValueKind == JsonValueKind.Number && target.TryGetUInt32(out var pid) && pid != 0) return pid;
        throw new ArgumentException("Select a valid Roblox process.");
    }

    private void PushState() => Send(new { type = "state", clients = _clients, busy = _busy, moduleAvailable = _moduleAvailable });
    private void SendLog(string message, string level = "info") => Send(new { type = "log", level, message });

    private void Send(object message)
    {
        if (!_stopped && _frontendReady && !IsDisposed && _view.CoreWebView2 is not null)
        {
            try { _view.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, JsonOptions)); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                DisableBridge();
            }
        }
    }

    private void DisableBridge()
    {
        _frontendReady = false;
        _askingToClose = false;
        _timer.Stop();
    }

    private object OpenFile()
    {
        using var dialog = new OpenFileDialog { Filter = "Lua scripts|*.lua;*.luau;*.txt|All files|*.*", Title = "Open a script" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return new { cancelled = true };
        var content = File.ReadAllText(dialog.FileName, Encoding.UTF8);
        _approvedFilePaths.Add(dialog.FileName);
        return new { cancelled = false, path = dialog.FileName, content };
    }

    private object SaveFile(JsonElement payload)
    {
        var content = payload.GetProperty("content").GetString() ?? "";
        var path = payload.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var saveAs = payload.TryGetProperty("saveAs", out var s) && s.ValueKind == JsonValueKind.True;
        if (saveAs || path is null || !_approvedFilePaths.Contains(path))
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Lua script|*.lua|Luau script|*.luau|Text file|*.txt|All files|*.*",
                DefaultExt = "lua", AddExtension = true, Title = "Save script",
                FileName = path is null ? "ryn_script.lua" : Path.GetFileName(path)
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return new { cancelled = true };
            path = dialog.FileName;
        }
        File.WriteAllText(path, content, new UTF8Encoding(false));
        _approvedFilePaths.Add(path);
        return new { cancelled = false, path };
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || !_frontendReady || _stopped || e.CloseReason == CloseReason.WindowsShutDown) return;
        e.Cancel = true;
        if (_askingToClose) return;
        _askingToClose = true;
        Send(new { type = "closing" });
    }

    private static string Explain(Exception ex) => ex switch
    {
        DllNotFoundException => "Cannot load Quorum-External-Module.dll. Keep the supplied DLL beside ExternalExecutor.exe; a native dependency may also be missing.",
        EntryPointNotFoundException => "The DLL does not export the expected API. Restore the pinned module version.",
        BadImageFormatException => "This API requires a valid Windows x64 DLL and the win-x64 application build.",
        _ => ex.Message
    };

    private sealed class ApiFailureException(ExecutorError error) : Exception(error.Message)
    {
        public ExecutorError Error { get; } = error;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
