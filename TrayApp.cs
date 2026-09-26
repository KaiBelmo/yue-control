using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Spotikey;

/// <summary>Tray icon, menu, hotkey wiring, extension bridge and autostart. Lives for the whole process.</summary>
public sealed class TrayApp : ApplicationContext
{
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValueName = "Spotikey";
    static readonly TimeSpan BridgeTimeout = TimeSpan.FromSeconds(3);

    Config _cfg;
    HotkeyManager _hotkeys;
    readonly MediaController _media = new();
    readonly NowPlayingPopup _popup = new();
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _autostartItem;
    readonly ToolStripMenuItem _bridgeStatusItem;
    readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 300 };
    BridgeServer? _bridge;

    bool _showBusy;
    bool _showPending;
    bool _lastShowQueue; // refreshes of a visible popup keep the mode it was opened in

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;
    static string ExtensionDir => Path.Combine(AppContext.BaseDirectory, "extension");
    static string LogPath => Path.Combine(Config.Dir, "bridge.log");

    /// <summary>Appends one line to bridge.log (last ~200 KB kept). Used to debug the extension side.</summary>
    static void Log(string what, JsonObject? payload = null)
    {
        try
        {
            string json = payload?.ToJsonString() ?? "";
            if (json.Length > 6000) json = json[..6000] + "...";
            Directory.CreateDirectory(Config.Dir);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200_000) File.Delete(LogPath);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {what} {json}{Environment.NewLine}");
        }
        catch { /* logging must never break the app */ }
    }

    public TrayApp()
    {
        // Creating the popup form above installed the WinForms SynchronizationContext on this thread,
        // so awaits below resume on the UI thread. Force its handle so BeginInvoke works before first Show().
        _ = _popup.Handle;

        _cfg = Config.Load();
        _hotkeys = RegisterHotkeys(_cfg);

        _autostartItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = IsAutostartEnabled() };
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);

        _bridgeStatusItem = new ToolStripMenuItem("Chrome extension: not connected") { Enabled = false };

        _tray = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = "Spotikey",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => _ = ShowNowPlayingAsync(fresh: true, queue: true);
        _popup.QueueItemClicked += (index, item) => _ = PlayQueueItemAsync(index, item);

        // Refresh the visible popup when the track changes, debounced (SMTC fires several events per change).
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            if (_popup.Visible) _ = ShowNowPlayingAsync(queue: _lastShowQueue);
        };
        _media.Changed += () => _popup.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });

        StartBridge();
        _ = InitMediaAsync();
        ReportHotkeyErrors();
    }

    // ---------------------------------------------------------------- setup

    HotkeyManager RegisterHotkeys(Config cfg)
    {
        var hk = new HotkeyManager();
        hk.Register(HotkeyAction.Next, cfg.Next);
        hk.Register(HotkeyAction.Previous, cfg.Previous);
        hk.Register(HotkeyAction.PlayPause, cfg.PlayPause);
        hk.Register(HotkeyAction.NowPlaying, cfg.NowPlaying);
        hk.Register(HotkeyAction.Shuffle, cfg.Shuffle);
        hk.Register(HotkeyAction.Like, cfg.Like);
        hk.Register(HotkeyAction.VolumeUp, cfg.VolumeUp);
        hk.Register(HotkeyAction.VolumeDown, cfg.VolumeDown);
        hk.Register(HotkeyAction.Repeat, cfg.Repeat);
        hk.Pressed += OnHotkey;
        return hk;
    }

    void ReportHotkeyErrors()
    {
        if (_hotkeys.Errors.Count == 0) return;
        _tray.ShowBalloonTip(8000, "Some hotkeys could not be registered",
            string.Join("\n", _hotkeys.Errors) + "\n\nEdit config.json from the tray menu to change them.",
            ToolTipIcon.Warning);
    }

    async Task InitMediaAsync()
    {
        try
        {
            await _media.InitAsync();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(8000, "Media control unavailable",
                "Could not connect to Windows media sessions. Hotkeys will fall back to virtual media keys.\n" + ex.Message,
                ToolTipIcon.Warning);
        }
    }

    void StartBridge()
    {
        _bridge?.Dispose();
        _bridge = new BridgeServer(_cfg.BridgePort);
        _bridge.ConnectionChanged += connected => _popup.BeginInvoke(() =>
        {
            _bridgeStatusItem.Text = connected ? "Chrome extension: connected" : "Chrome extension: not connected";
        });
        _bridge.StateReceived += _ => _popup.BeginInvoke(() =>
        {
            if (_popup.Visible) { _refreshTimer.Stop(); _refreshTimer.Start(); }
        });

        try
        {
            _bridge.Start();
        }
        catch (Exception ex)
        {
            _bridgeStatusItem.Text = $"Chrome extension: port {_cfg.BridgePort} unavailable";
            _tray.ShowBalloonTip(8000, "Extension bridge failed",
                $"Could not listen on 127.0.0.1:{_cfg.BridgePort} ({ex.Message}). Queue, shuffle, like and volume hotkeys will not work. " +
                "Change BridgePort in config.json and extension/background.js if another program uses that port.",
                ToolTipIcon.Warning);
        }
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("Now playing + queue", null, (_, _) => _ = ShowNowPlayingAsync(fresh: true, queue: true))
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Hotkeys") { Enabled = false });
        AddBinding(menu, "Next track", _cfg.Next);
        AddBinding(menu, "Previous / restart", _cfg.Previous);
        AddBinding(menu, "Play / pause", _cfg.PlayPause);
        AddBinding(menu, "Show now playing + queue", _cfg.NowPlaying);
        AddBinding(menu, "Shuffle (extension)", _cfg.Shuffle);
        AddBinding(menu, "Like song (extension)", _cfg.Like);
        AddBinding(menu, "Volume up (extension)", _cfg.VolumeUp);
        AddBinding(menu, "Volume down (extension)", _cfg.VolumeDown);
        AddBinding(menu, "Repeat mode (extension)", _cfg.Repeat);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(_bridgeStatusItem);
        menu.Items.Add(new ToolStripMenuItem("Set up Chrome extension...", null, (_, _) => ShowExtensionSetup()));
        menu.Items.Add(new ToolStripMenuItem("Open bridge log", null, (_, _) => OpenFile(LogPath)));
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Edit hotkeys (config.json)...", null, (_, _) => OpenConfig()));
        menu.Items.Add(new ToolStripMenuItem("Reload config", null, (_, _) => ReloadConfig()));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Quit()));
        return menu;
    }

    static void AddBinding(ContextMenuStrip menu, string name, string binding)
    {
        string shown = string.IsNullOrWhiteSpace(binding) ? "(unbound)" : binding;
        menu.Items.Add(new ToolStripMenuItem($"{name}\t{shown}") { Enabled = false });
    }

    // ---------------------------------------------------------------- actions

    async void OnHotkey(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.Next: await _media.NextAsync(); break;
                case HotkeyAction.Previous: await _media.PreviousAsync(); break;
                case HotkeyAction.PlayPause: await _media.PlayPauseAsync(); break;

                case HotkeyAction.NowPlaying: await ShowNowPlayingAsync(fresh: true, queue: true); return;
                case HotkeyAction.Shuffle: await RunBridgeActionAsync("shuffle"); return;
                case HotkeyAction.Like: await RunBridgeActionAsync("like"); return;
                case HotkeyAction.Repeat: await RunBridgeActionAsync("repeat"); return;
                case HotkeyAction.VolumeUp: await RunBridgeActionAsync("volumeUp"); return;
                case HotkeyAction.VolumeDown: await RunBridgeActionAsync("volumeDown"); return;
            }

            if (_cfg.ShowPopupOnAction)
            {
                await Task.Delay(150); // give Chrome a moment to publish the new track
                await ShowNowPlayingAsync(queue: false); // quick glance only; queue is on the NowPlaying hotkey
            }
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "Spotikey", ex.Message, ToolTipIcon.Error);
        }
    }

    async Task RunBridgeActionAsync(string action)
    {
        string? message;
        if (_bridge is not { IsConnected: true })
        {
            message = "Chrome extension not connected - see tray menu";
        }
        else
        {
            try
            {
                var args = new JsonObject { ["step"] = _cfg.VolumeStep };
                var r = await _bridge.SendCommandAsync(action, BridgeTimeout, args);
                Log($"{action} ->", r);
                message = Str(r, "message") ?? Str(r, "error") ?? action;
            }
            catch (Exception ex)
            {
                message = ex.Message;
            }
        }
        await ShowNowPlayingAsync(fresh: false, message, queue: false);
    }

    /// <summary>User clicked a row in the popup: ask the extension to play that song from the queue.</summary>
    async Task PlayQueueItemAsync(int index, QueueItem item)
    {
        string? message;
        if (_bridge is not { IsConnected: true })
        {
            message = "Chrome extension not connected - see tray menu";
        }
        else
        {
            try
            {
                var args = new JsonObject { ["index"] = index, ["title"] = item.Title, ["artist"] = item.Artist };
                Log("playQueue <-", args);
                var r = await _bridge.SendCommandAsync("playQueue", TimeSpan.FromSeconds(6), args);
                Log("playQueue ->", r);
                message = Str(r, "message") ?? Str(r, "error") ?? "Playing";
            }
            catch (Exception ex)
            {
                message = ex.Message;
            }
        }

        await Task.Delay(500); // let Spotify switch track before we read the new state
        await ShowNowPlayingAsync(fresh: true, message, queue: true);
    }

    /// <summary>
    /// Fetches track info (Windows) and state from the extension and shows the popup.
    /// With <paramref name="queue"/> the upcoming tracks are fetched and listed; without it only the
    /// current song (plus liked/shuffle state) is shown.
    /// </summary>
    async Task ShowNowPlayingAsync(bool fresh = false, string? message = null, bool queue = false)
    {
        if (_showBusy) { _showPending = true; return; }
        _showBusy = true;
        _lastShowQueue = queue;
        try
        {
            var infoTask = _media.GetNowPlayingAsync();

            JsonObject? bridgeState = null;
            bool connected = _bridge is { IsConnected: true };
            if (connected)
            {
                string command = queue ? "getQueue" : "getState";
                try
                {
                    var args = new JsonObject { ["fresh"] = fresh, ["limit"] = _cfg.QueueRows };
                    bridgeState = await _bridge!.SendCommandAsync(command, BridgeTimeout, args);
                    Log($"{command}(fresh={fresh}) ->", bridgeState);
                }
                catch (Exception ex)
                {
                    Log($"{command} failed: {ex.Message}");
                    message ??= ex.Message;
                }
            }

            var info = await infoTask;
            _popup.ShowInfo(info, ParseExtras(bridgeState, message, connected, queue), _cfg.PopupSeconds);
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "Spotikey", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _showBusy = false;
            if (_showPending)
            {
                _showPending = false;
                _ = ShowNowPlayingAsync(queue: _lastShowQueue);
            }
        }
    }

    static PopupExtras ParseExtras(JsonObject? r, string? message, bool connected, bool queueRequested)
    {
        if (r == null) return PopupExtras.None with { BridgeConnected = connected, Message = message, QueueRequested = queueRequested };

        bool? liked = r["track"]?["liked"] is JsonValue lv && lv.TryGetValue<bool>(out bool l) ? l : null;
        string? shuffle = Str(r, "shuffle");

        var queue = new List<QueueItem>();
        var q = r["queue"] as JsonObject;
        if (q?["items"] is JsonArray items)
            foreach (var item in items)
                if (item is JsonObject o) queue.Add(new QueueItem(Str(o, "title") ?? "", Str(o, "artist") ?? ""));

        message ??= Str(r, "error") ?? (q != null ? Str(q, "error") : null);

        return new PopupExtras(connected, message, liked, shuffle, queue, q != null ? Str(q, "source") : null, queueRequested, Str(r, "site"));
    }

    static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    // ---------------------------------------------------------------- menu commands

    void ShowExtensionSetup()
    {
        try { Clipboard.SetText(ExtensionDir); } catch { }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionDir}\"") { UseShellExecute = true }); } catch { }

        MessageBox.Show(
            "The Chrome extension adds: queue in the popup, shuffle, like, volume and repeat hotkeys for\n" +
            "Spotify and YouTube Music.\n\n" +
            "1. In Chrome, open  chrome://extensions\n" +
            "2. Turn on \"Developer mode\" (top right)\n" +
            "3. Click \"Load unpacked\" and choose this folder (path is on your clipboard):\n" +
            $"    {ExtensionDir}\n" +
            "4. Reload your open.spotify.com / music.youtube.com tabs\n\n" +
            "The tray menu then shows \"Chrome extension: connected\".\n" +
            "Chrome shows a \"disable developer mode extensions\" bubble at startup; just close it.",
            "Set up Chrome extension", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void OpenConfig()
    {
        if (!File.Exists(Config.FilePath)) { try { _cfg.Save(); } catch { } }
        OpenFile(Config.FilePath);
    }

    static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Spotikey", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ReloadConfig()
    {
        _hotkeys.Pressed -= OnHotkey;
        _hotkeys.Dispose();

        int oldPort = _cfg.BridgePort;
        _cfg = Config.Load();
        _hotkeys = RegisterHotkeys(_cfg);
        if (_cfg.BridgePort != oldPort) StartBridge();

        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = BuildMenu();

        if (_hotkeys.Errors.Count == 0)
            _tray.ShowBalloonTip(3000, "Spotikey", "Hotkeys reloaded.", ToolTipIcon.Info);
        else
            ReportHotkeyErrors();
    }

    static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value
                && value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)!;
            if (enabled) key.SetValue(RunValueName, $"\"{ExePath}\"");
            else key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not change autostart: " + ex.Message, "Spotikey", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var green = new SolidBrush(Color.FromArgb(30, 215, 96));
            g.FillEllipse(green, 1, 1, 30, 30);
            using var font = new Font("Segoe UI Symbol", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString("♪", font);
            g.DrawString("♪", font, Brushes.Black, (32 - size.Width) / 2f, (32 - size.Height) / 2f - 1);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    void Quit()
    {
        _tray.Visible = false;
        _hotkeys.Dispose();
        _bridge?.Dispose();
        _media.Dispose();
        _popup.Dispose();
        _tray.Dispose();
        ExitThread();
    }
}
