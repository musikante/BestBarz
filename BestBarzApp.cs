namespace BestBarz;

/// <summary>
/// Application host: owns CoolbarWindows (dynamic list), tray icon, mouse-polling timer
/// and a FileSystemWatcher that triggers live config reload when the Config folder changes.
/// </summary>
public sealed class BestBarzApp : ApplicationContext
{
    private readonly GlobalConfig    _global;
    private List<BarConfig>          _bars    = [];
    private List<BestBarWindow>      _windows = [];
    private readonly ConfigService   _cfg;
    private readonly NotifyIcon      _tray;
    private readonly System.Windows.Forms.Timer _mouseTimer;
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 1500 };
    private FileSystemWatcher?       _fsw;
    private bool                     _barsSuspended;
    private HotkeyWindow?            _hotkeyWin;
    // Marshals watcher events (thread pool) to the UI thread. Not a bar window: a bar that was
    // never shown has no handle, and BeginInvoke on it would drop the reload.
    private readonly WindowsFormsSynchronizationContext _ui = new();

    public BestBarzApp()
    {
        _cfg = new ConfigService(Path.Combine(AppContext.BaseDirectory, "Config"));

        if (_cfg.NeedsMigration)
            _cfg.MigrateFromLegacy();

        if (_cfg.NeedsGroupMigration)
            _cfg.MigrateGroupFoldersToFiles();

        _global = _cfg.LoadGlobal();
        _bars   = _cfg.LoadBars();
        if (_bars.Count == 0)
            _bars = _cfg.CreateDefaultBars();

        CreateWindows();
        StartFsw();
        _hotkeyWin = new HotkeyWindow(ToggleBars);

        _tray = new NotifyIcon
        {
            Icon             = SystemIcons.Application,
            Text             = "BestBarz",
            Visible          = _global.ShowTrayIcon,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowTrayMenu(); };

        _mouseTimer = new System.Windows.Forms.Timer { Interval = Math.Max(10, _global.DataInterval) };
        _mouseTimer.Tick += OnTick;
        _mouseTimer.Start();

        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            DoRefresh();
        };
    }

    private void ToggleBars()
    {
        _barsSuspended = !_barsSuspended;
        foreach (var w in _windows)
        {
            if (_barsSuspended) w.SuspendPolling();
            else                w.ResumePolling();
        }
    }

    private void CreateWindows()
    {
        foreach (var bar in _bars)
        {
            var win = new BestBarWindow(bar, _global, _cfg, fillAppMenu: FillAppMenu);
            if (_barsSuspended) win.SuspendPolling();   // recreated after Settings/reload: keep Ctrl+Alt+B state
            _windows.Add(win);
        }
    }

    private void DisposeWindows()
    {
        foreach (var w in _windows) w.Dispose();
        _windows.Clear();
    }

    // ── FileSystemWatcher ─────────────────────────────────────────────────────

    private void StartFsw()
    {
        try
        {
            _fsw = new FileSystemWatcher(Path.Combine(AppContext.BaseDirectory, "Config"))
            {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents   = true,
            };
            _fsw.Changed += OnFswEvent;
            _fsw.Created += OnFswEvent;
            _fsw.Deleted += OnFswEvent;
            _fsw.Renamed += OnFswEvent;
        }
        catch { /* FSW not critical; continue without it */ }
    }

    private void OnFswEvent(object sender, FileSystemEventArgs e)
    {
        // Skip temp files written by WriteAtomic
        if (e.Name?.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) == true) return;
        if (_cfg.IsOwnWriteRecent) return;

        // Restart the debounce timer on the UI thread
        _ui.Post(_ =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }, null);
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public void DoRefresh()
    {
        DisposeWindows();
        // No default bars here (only on first run): while the user renames edge folders to swap
        // bars, recreating Left/Top/… would make their next rename fail.
        _bars = _cfg.LoadBars();
        CreateWindows();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        bool fs = IsForegroundFullscreen();
        foreach (var w in _windows) w.SetFullscreenActive(fs);

        bool blocked = _global.OnlyOnDesktop && !IsForegroundDesktopOrTaskbar();
        foreach (var w in _windows) w.SetFocusBlocked(blocked);

        var cursor = Cursor.Position;
        for (int i = 0; i < _bars.Count; i++)
            if (_bars[i].Enabled)
                _windows[i].Poll(cursor);
    }

    private bool IsForegroundDesktopOrTaskbar()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return true;
        if (IsOwnWindow(hwnd)) return true;
        return IsShellWindow(hwnd);
    }

    // Any BestBarz window (bars, the tray menu — which takes the foreground —, Settings, dialogs).
    private static bool IsOwnWindow(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsShellWindow(IntPtr hwnd) =>
        Win32.GetClassName(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private bool IsForegroundFullscreen()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        if (IsOwnWindow(hwnd) || IsShellWindow(hwnd)) return false;
        if (!Win32.GetWindowRect(hwnd, out var rect)) return false;
        var screen = Screen.FromHandle(hwnd).Bounds;
        return rect.Left <= screen.Left && rect.Top <= screen.Top &&
               rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    // ── Tray menu ─────────────────────────────────────────────────────────────

    // Left click on the icon opens the same menu as a right click; NotifyIcon only exposes this privately.
    private void ShowTrayMenu() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.Invoke(_tray, null);

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        FillAppMenu(menu.Items);
        // Rebuilt on every opening; Cancel is preset when the strip has no items, so clear it.
        menu.Opening += (_, e) => { FillAppMenu(menu.Items); e.Cancel = false; };
        MenuDismiss.Attach(menu);
        return menu;
    }

    // Items of the tray menu, also used by the bars' "BestBarz" submenu. Rebuilt on each opening so
    // checks and labels reflect the current state. Actions run once the menu has closed: Settings and
    // Exit recreate or dispose the bar windows that may own the menu being clicked.
    private void FillAppMenu(ToolStripItemCollection items)
    {
        foreach (var old in items.Cast<ToolStripItem>().ToList()) old.Dispose();
        items.Clear();
        void Later(Action action) => _ui.Post(_ => action(), null);

        items.Add("Settings…", null, (_, _) => Later(ShowSettings));
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem(_barsSuspended ? "Show bars" : "Hide bars", null, (_, _) => Later(ToggleBars))
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+B",
        });
        items.Add(new ToolStripSeparator());

        foreach (var (bar, window) in _bars.Select((b, i) => (b, _windows[i]))
                                           .OrderBy(x => x.b.DockPosition))
        {
            items.Add(new ToolStripMenuItem(bar.Name, null, (_, _) => Later(() =>
            {
                bar.Enabled = !bar.Enabled;
                if (!bar.Enabled) window.HideNow();
                _cfg.SaveBar(bar);
            }))
            {
                Checked = bar.Enabled,
            });
        }

        items.Add(new ToolStripSeparator());
        items.Add("Exit", null, (_, _) => Later(Exit));
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    private void ShowSettings()
    {
        if (_fsw != null) _fsw.EnableRaisingEvents = false;
        try { ShowSettingsCore(); }
        finally { if (_fsw != null) _fsw.EnableRaisingEvents = true; }
    }

    private void ShowSettingsCore()
    {
        DialogResult result;
        using (var dlg = new SettingsForm(_global, _bars, _cfg))
            result = dlg.ShowDialog();

        if (result == DialogResult.Retry)   // "Reload config": discard dialog edits, reload, reopen
        {
            DoRefresh();
            ShowSettingsCore();
            return;
        }
        if (result != DialogResult.OK) return;

        _cfg.SaveGlobal(_global);
        foreach (var bar in _bars) _cfg.SaveBar(bar);

        _tray.Visible        = _global.ShowTrayIcon;
        _mouseTimer.Interval = Math.Max(10, _global.DataInterval);

        DisposeWindows();
        CreateWindows();
    }

    // ── Exit ──────────────────────────────────────────────────────────────────

    private void Exit()
    {
        _mouseTimer.Stop();
        _refreshTimer.Stop();
        _tray.Visible = false;
        DisposeWindows();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkeyWin?.Dispose();
            _fsw?.Dispose();
            _ui.Dispose();
            _mouseTimer.Dispose();
            _refreshTimer.Dispose();
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Global hotkey receiver ────────────────────────────────────────────────

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int Id = 0xBB;   // arbitrary app-unique id
        private readonly Action _callback;

        public HotkeyWindow(Action callback)
        {
            _callback = callback;
            CreateHandle(new CreateParams());
            Win32.RegisterHotKey(Handle, Id, Win32.MOD_CTRL | Win32.MOD_ALT, (int)Keys.B);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_HOTKEY && m.WParam.ToInt32() == Id)
                _callback();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Win32.UnregisterHotKey(Handle, Id);
            DestroyHandle();
        }
    }
}
