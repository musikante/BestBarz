namespace BestBarz;

/// <summary>
/// Global + per-bar settings dialog. Edits a local copy; nothing is applied unless OK is pressed.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly GlobalConfig     _global;
    private readonly List<BarConfig>  _bars;
    private readonly ConfigService    _cfg;

    private readonly CheckBox      _chkTray, _chkTips, _chkFade, _chkOnlyDesktop, _chkStartup, _chkArrangeHelp;
    private readonly NumericUpDown _nudInterval, _nudFadeIn, _nudFadeOut;

    // Per-bar working copies (sized to bars.Count in constructor)
    private bool[]        _enabled;
    private int[]         _monitor;
    private Color[]       _color;
    private Color[]       _sepColor;
    private ButtonStyle[] _style;
    private bool[]        _alwaysVis;
    private bool[]        _fullScreen;
    private string[]      _align;   // "Stretch" | "Near" | "Center" | "Far"

    public SettingsForm(GlobalConfig global, List<BarConfig> bars, ConfigService cfg)
    {
        _global = global;
        _bars   = [.. bars.OrderBy(b => b.DockPosition)];
        _cfg    = cfg;

        int n      = bars.Count;
        _enabled   = new bool[n];
        _monitor   = new int[n];
        _color     = new Color[n];
        _sepColor  = new Color[n];
        _style     = new ButtonStyle[n];
        _alwaysVis  = new bool[n];
        _fullScreen = new bool[n];
        _align      = new string[n];

        Text            = "BestBarz — Settings";
        ClientSize      = new Size(500, 420);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;

        var tabs = new TabControl { Location = new Point(8, 8), Size = new Size(484, 364) };

        // ── General ───────────────────────────────────────────────────────────
        var gen = new TabPage("General");
        int y = 12;
        gen.Controls.Add(_chkTray        = CB(12, y, "Show system tray icon",              global.ShowTrayIcon));    y += 28;
        gen.Controls.Add(_chkStartup     = CB(12, y, "Start with Windows",                 GetStartup()));           y += 28;
        gen.Controls.Add(_chkTips        = CB(12, y, "Show tooltips",                      global.ShowToolTips));    y += 28;
        gen.Controls.Add(_chkFade        = CB(12, y, "Toolbar fading",                     global.ToolbarFading));   y += 28;
        // Stored inverted as OnlyOnDesktop. Fullscreen apps always need Ctrl, whatever this says.
        gen.Controls.Add(_chkOnlyDesktop = CB(12, y, "Show bars over apps without Ctrl",   !global.OnlyOnDesktop));  y += 28;
        gen.Controls.Add(_chkArrangeHelp = CB(12, y, "Show Arrange mode help",             global.ShowArrangeHelp)); y += 40;

        gen.Controls.Add(Lbl(12, y, "Poll interval (ms):"));
        gen.Controls.Add(_nudInterval = NUD(170, y, 10, 2000, global.DataInterval, 0, 10));                          y += 32;
        gen.Controls.Add(Lbl(12, y, "Fade-in step:"));
        gen.Controls.Add(_nudFadeIn  = NUD(170, y, 0.005m, 1m, (decimal)global.ToolbarFadeInInterval,  3, 0.005m)); y += 32;
        gen.Controls.Add(Lbl(12, y, "Fade-out step:"));
        gen.Controls.Add(_nudFadeOut = NUD(170, y, 0.005m, 1m, (decimal)global.ToolbarFadeOutInterval, 3, 0.005m)); y += 44;
        var btnCleanup = new Button { Text = "Clean up unused groups…", Location = new Point(12, y), Size = new Size(170, 26) };
        btnCleanup.Click += (_, _) => CleanupOrphanGroups();
        gen.Controls.Add(btnCleanup);
        // Reloading replaces the bars this dialog edits, so close without applying; the app reloads and reopens Settings.
        var btnReload = new Button { Text = "Reload config", Location = new Point(btnCleanup.Right + 8, y), Size = new Size(120, 26) };
        btnReload.Click += (_, _) => DialogResult = DialogResult.Retry;
        gen.Controls.Add(btnReload);
        tabs.TabPages.Add(gen);

        // ── One tab per bar ───────────────────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            int idx  = i;
            var bar  = _bars[i];
            int edge = (int)bar.DockPosition;
            _enabled[i]   = bar.Enabled;
            _monitor[i]   = bar.DisplayTarget;
            _color[i]     = bar.ToolbarColor;
            _sepColor[i]  = bar.SeparatorColor;
            _style[i]     = global.ButtonLayout[edge];
            _alwaysVis[i]  = global.AlwaysVisible.Contains(bar.DockPosition);
            _fullScreen[i] = global.FullScreenBars.Contains(bar.DockPosition);
            _align[i]     = global.StretchBars.Contains(bar.DockPosition)      ? "Stretch"
                          : global.CenterBars.Contains(bar.DockPosition)        ? "Center"
                          : global.ReverseAlignBars.Contains(bar.DockPosition)  ? "Far"
                          :                                                        "Near";

            var tab = new TabPage(bar.Name);
            int ty = 12;

            var chkEnabled = CB(12, ty, "Enabled", _enabled[i]);
            chkEnabled.CheckedChanged += (_, _) => _enabled[idx] = chkEnabled.Checked;
            tab.Controls.Add(chkEnabled); ty += 32;

            tab.Controls.Add(Lbl(12, ty, "Monitor index:"));
            var nudMon = NUD(170, ty, 0, Math.Max(0, Screen.AllScreens.Length - 1), _monitor[i], 0, 1);
            nudMon.ValueChanged += (_, _) => _monitor[idx] = (int)nudMon.Value;
            tab.Controls.Add(nudMon); ty += 32;

            tab.Controls.Add(Lbl(12, ty, "Button style:"));
            var cmb = new ComboBox { Location = new Point(170, ty), Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var v in Enum.GetValues<ButtonStyle>()) cmb.Items.Add(v);
            cmb.SelectedItem = _style[i];
            cmb.SelectedIndexChanged += (_, _) => { if (cmb.SelectedItem is ButtonStyle s) _style[idx] = s; };
            tab.Controls.Add(cmb); ty += 32;

            tab.Controls.Add(Lbl(12, ty, "Toolbar colour:"));
            var btnColor = new Button { Location = new Point(170, ty), Size = new Size(80, 24), BackColor = _color[i], FlatStyle = FlatStyle.Flat };
            btnColor.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { Color = _color[idx] };
                if (dlg.ShowDialog(this) == DialogResult.OK) btnColor.BackColor = _color[idx] = dlg.Color;
            };
            tab.Controls.Add(btnColor); ty += 32;

            tab.Controls.Add(Lbl(12, ty, "Separator colour:"));
            var btnSep = new Button { Location = new Point(170, ty), Size = new Size(80, 24), BackColor = _sepColor[i], FlatStyle = FlatStyle.Flat };
            btnSep.Click += (_, _) =>
            {
                using var dlg = new ColorDialog { Color = _sepColor[idx] };
                if (dlg.ShowDialog(this) == DialogResult.OK) btnSep.BackColor = _sepColor[idx] = dlg.Color;
            };
            tab.Controls.Add(btnSep); ty += 32;

            var chkAlways = CB(12, ty, "Always visible", _alwaysVis[i]);
            chkAlways.CheckedChanged += (_, _) => _alwaysVis[idx] = chkAlways.Checked;
            tab.Controls.Add(chkAlways); ty += 32;

            var chkFull = CB(12, ty, "Cover the taskbar", _fullScreen[i]);
            chkFull.CheckedChanged += (_, _) => _fullScreen[idx] = chkFull.Checked;
            tab.Controls.Add(chkFull); ty += 32;

            tab.Controls.Add(Lbl(12, ty, "Alignment:"));
            var cmbAlign = new ComboBox { Location = new Point(170, ty), Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAlign.Items.AddRange(["Stretch", "Near", "Center", "Far"]);
            cmbAlign.SelectedItem = _align[i];
            cmbAlign.SelectedIndexChanged += (_, _) => { if (cmbAlign.SelectedItem is string s) _align[idx] = s; };
            tab.Controls.Add(cmbAlign);

            tabs.TabPages.Add(tab);
        }

        tabs.TabPages.Add(BuildAboutTab());

        var ok     = new Button { Text = "OK",     Size = new Size(80, 28), Location = new Point(316, 382), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Size = new Size(80, 28), Location = new Point(404, 382), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Apply();

        Controls.Add(tabs);
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Apply()
    {
        _global.ShowTrayIcon           = _chkTray.Checked;
        _global.ShowToolTips           = _chkTips.Checked;
        _global.ToolbarFading          = _chkFade.Checked;
        _global.OnlyOnDesktop          = !_chkOnlyDesktop.Checked;
        _global.ShowArrangeHelp        = _chkArrangeHelp.Checked;
        SetStartup(_chkStartup.Checked);
        _global.DataInterval           = (int)_nudInterval.Value;
        _global.ToolbarFadeInInterval  = (double)_nudFadeIn.Value;
        _global.ToolbarFadeOutInterval = (double)_nudFadeOut.Value;

        for (int i = 0; i < _bars.Count; i++)
        {
            var bar = _bars[i];
            bar.Enabled       = _enabled[i];
            bar.DisplayTarget = _monitor[i];
            bar.ToolbarColor  = _color[i];
            bar.SeparatorColor = _sepColor[i];
            _global.ButtonLayout[(int)bar.DockPosition] = _style[i];
            if (_alwaysVis[i]) _global.AlwaysVisible.Add(bar.DockPosition);
            else               _global.AlwaysVisible.Remove(bar.DockPosition);

            if (_fullScreen[i]) _global.FullScreenBars.Add(bar.DockPosition);
            else                _global.FullScreenBars.Remove(bar.DockPosition);

            _global.StretchBars.Remove(bar.DockPosition);
            _global.CenterBars.Remove(bar.DockPosition);
            _global.ReverseAlignBars.Remove(bar.DockPosition);
            switch (_align[i])
            {
                case "Stretch": _global.StretchBars.Add(bar.DockPosition);      break;
                case "Center":  _global.CenterBars.Add(bar.DockPosition);        break;
                case "Far":     _global.ReverseAlignBars.Add(bar.DockPosition);  break;
                // "Near": offset = 0, no HashSet needed
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CheckBox CB(int x, int y, string text, bool chk) =>
        new() { Text = text, Checked = chk, Location = new Point(x, y), AutoSize = true };

    private static Label Lbl(int x, int y, string text) =>
        new() { Text = text, Location = new Point(x, y + 3), AutoSize = true };

    // ── About ─────────────────────────────────────────────────────────────────

    private TabPage BuildAboutTab()
    {
        var page    = new TabPage("About");
        var asm     = typeof(SettingsForm).Assembly;
        var version = asm.GetName().Version?.ToString(3) ?? "?";
        var desc    = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyDescriptionAttribute), false)
                         .OfType<System.Reflection.AssemblyDescriptionAttribute>().FirstOrDefault()?.Description ?? "";

        var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold);
        page.Disposed += (_, _) => titleFont.Dispose();
        page.Controls.Add(new Label
        {
            Text = "BestBarz", Font = titleFont,
            Location = new Point(12, 12), AutoSize = true,
        });
        page.Controls.Add(Lbl(14, 52, $"Version {version}"));
        page.Controls.Add(Lbl(14, 74, desc));
        page.Controls.Add(Lbl(14, 96, $".NET {Environment.Version}"));

        page.Controls.Add(Lbl(14, 140, "Config folder:"));
        var txtPath = new TextBox
        {
            Text = _cfg.ConfigDir, ReadOnly = true, TabStop = false,
            Location = new Point(14, 164), Width = 360,
        };
        var btnOpen = new Button { Text = "Open folder", Location = new Point(380, 163), Size = new Size(84, 25) };
        btnOpen.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", $"\"{_cfg.ConfigDir}\"");
        page.Controls.Add(txtPath);
        page.Controls.Add(btnOpen);
        return page;
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void CleanupOrphanGroups()
    {
        var orphans = _cfg.FindOrphanGroupFiles();
        if (orphans.Count == 0)
        {
            MessageBox.Show(this, "No unused group files found.", "BestBarz",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var list = string.Join("\n", orphans.Select(f => "    " + Path.GetRelativePath(_cfg.ConfigDir, f)));
        if (MessageBox.Show(this,
                $"These group files are not used by any bar:\n\n{list}\n\nMove them to the Recycle Bin?",
                "BestBarz", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
            != DialogResult.Yes)
            return;

        foreach (var f in orphans)
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
    }

    // ── Startup registry ──────────────────────────────────────────────────────

    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static bool GetStartup()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue("BestBarz") != null;
    }

    private static void SetStartup(bool enable)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key == null) return;
        if (enable)
            key.SetValue("BestBarz", $"\"{Application.ExecutablePath}\"");   // quoted: the path may contain spaces
        else
            key.DeleteValue("BestBarz", throwOnMissingValue: false);
    }

    private static NumericUpDown NUD(int x, int y, decimal min, decimal max, decimal val, int dec, decimal step) =>
        new()
        {
            Minimum       = min,
            Maximum       = max,
            Value         = Math.Clamp(val, min, max),
            DecimalPlaces = dec,
            Increment     = step,
            Location      = new Point(x, y),
            Width         = 100,
        };
}
