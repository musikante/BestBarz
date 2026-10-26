namespace BestBarz;

/// <summary>
/// Non-activating borderless popup that shows a group button's items.
/// Positioned adjacent to the parent bar at the group button's screen bounds.
/// Auto-closes when the mouse leaves both the popup and the anchor button area.
/// Supports Cut / Paste for moving items between bars and groups.
/// </summary>
public sealed class GroupPopupWindow : Form
{
    private const int LeaveMargin = 16;
    private const int LeaveDelay  = 400;

    private readonly Rectangle          _anchor;
    private readonly FlowLayoutPanel    _panel;
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 80 };
    private readonly ConfigService      _cfg;
    private readonly BarConfig          _groupBar;
    private readonly GlobalConfig       _global;
    private readonly BarConfig          _parentBar;
    private readonly DockPosition       _parentEdge;
    private readonly Action<ToolStripItemCollection>? _fillAppMenu;   // tray menu items, for the "Global" submenu
    private readonly ContextMenuStrip   _menu;
    private ShortcutItem?               _menuTarget;

    private long   _leftSince = -1;
    private int    _menuOpen;
    private ShortcutItem?               _arrangeTarget;
    private ArrangeHelpWindow?          _arrangeHelp;
    private List<ShortcutItem>?         _arrangeSnapshot;   // order on entering Arrange mode, restored by Esc
    private IntPtr                      _kbHook = IntPtr.Zero;
    private Win32.LowLevelKeyboardProc? _kbProc;
    private const string ReorderFormat = "BestBarz.ButtonReorder";

    public GroupPopupWindow(
        ConfigService cfg,
        BarConfig groupBar,
        GlobalConfig global,
        BarConfig parentBar,
        DockPosition parentEdge,
        Rectangle anchorBounds,
        Action<ToolStripItemCollection>? fillAppMenu = null)
    {
        _cfg          = cfg;
        _groupBar     = groupBar;
        _global       = global;
        _parentBar    = parentBar;
        _parentEdge   = parentEdge;
        _anchor       = anchorBounds;
        _fillAppMenu  = fillAppMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        BackColor       = parentBar.ToolbarColor;

        _panel = new FlowLayoutPanel
        {
            AutoSize      = true,
            AutoSizeMode  = AutoSizeMode.GrowAndShrink,
            Padding       = new Padding(2),
            Margin        = Padding.Empty,
            BackColor     = parentBar.ToolbarColor,
            FlowDirection = FlowDirection.TopDown,
            WrapContents  = false,
        };
        Controls.Add(_panel);
        AutoSize     = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _menu = BuildMenu();
        _panel.ContextMenuStrip = _menu;
        ContextMenuStrip        = _menu;
        _panel.AllowDrop  = true;
        _panel.DragEnter += HandleDragEnter;
        _panel.DragOver  += HandleDragOver;
        _panel.DragDrop  += HandleDragDrop;
        _panel.Paint     += OnPanelPaint;
        RebuildItems();

        _pollTimer.Tick += OnPollTick;
        FormClosed      += (_, _) => _pollTimer.Dispose();
    }

    // ── Build items ───────────────────────────────────────────────────────────

    private void RebuildItems()
    {
        _panel.SuspendLayout();
        foreach (Control c in _panel.Controls.Cast<Control>().ToArray()) c.Dispose();
        _panel.Controls.Clear();

        var style    = _global.ButtonLayout[(int)_parentEdge];
        int maxWidth = _global.MaxButtonWidth[(int)_parentEdge];

        var buttons = new List<Control>();
        var seps    = new List<Control>();

        foreach (var sc in _groupBar.Shortcuts)
        {
            if (sc.IsGroup) continue;
            if (sc.IsSeparator)
            {
                var sep = CreateSeparator(sc);
                seps.Add(sep);
                _panel.Controls.Add(sep);
                continue;
            }
            var btn = new BestBarButton(sc, style, _parentBar, maxWidth);
            btn.Tag = sc;
            btn.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && _arrangeTarget != null)
                    btn.DoDragDrop(new DataObject(ReorderFormat, sc), DragDropEffects.Move);
            };
            btn.Click += (_, _) => { if (_arrangeTarget == null) { Close(); LaunchItem(sc); } };
            btn.ContextMenuStrip = _menu;
            buttons.Add(btn);
            _panel.Controls.Add(btn);
        }

        int sepWidth = buttons.Count == 0 ? 80 : buttons.Max(b => b.Width);
        foreach (var sep in seps) sep.Width = sepWidth;

        if (_panel.Controls.Count == 0)
        {
            _panel.Controls.Add(new Label
            {
                Text             = "(empty)",
                ForeColor        = Color.Gray,
                AutoSize         = true,
                Padding          = new Padding(8),
                ContextMenuStrip = _menu,
            });
        }

        _panel.ResumeLayout(true);
        if (_arrangeTarget != null) HighlightArrangeButton(true);
        if (Visible) RepositionNearAnchor();
        PlaceArrangeHelp();
    }

    // ── Context menus ─────────────────────────────────────────────────────────

    private Label CreateSeparator(ShortcutItem sc)
    {
        bool labelled = !string.IsNullOrEmpty(sc.Text);
        return new Label
        {
            AutoSize         = false,
            Text             = sc.Text,
            UseMnemonic      = false,
            BackColor        = _parentBar.VisibleSeparatorColor,
            ForeColor        = _parentBar.SeparatorTextColor,
            Margin           = new Padding(1, 2, 1, 2),
            Height           = labelled ? 18 : 3,
            TextAlign        = _parentBar.SeparatorTextAlign switch
            {
                "Left"  or "Near" => ContentAlignment.MiddleLeft,
                "Right" or "Far"  => ContentAlignment.MiddleRight,
                _                 => ContentAlignment.MiddleCenter,
            },
            Tag              = sc,
            ContextMenuStrip = _menu,
        };
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu    = new ContextMenuStrip();
        var cut     = new ToolStripMenuItem("Cut",          null, (_, _) => { if (_menuTarget != null) CutItem(_menuTarget); });
        var copy    = new ToolStripMenuItem("Copy",         null, (_, _) => { if (_menuTarget != null) BarClipboard.Copy(_menuTarget); });
        var paste   = new ToolStripMenuItem("Paste",        null, (_, _) => PasteAfterItem(_menuTarget));
        var edit    = new ToolStripMenuItem("Edit…",        null, (_, _) => { if (_menuTarget != null) EditItem(_menuTarget); });
        var arrange = new ToolStripMenuItem("Arrange mode", null, (_, _) => { if (_menuTarget != null) ToggleArrangeMode(_menuTarget); });
        var remove  = new ToolStripMenuItem("Remove",       null, (_, _) => { if (_menuTarget != null) RemoveItem(_menuTarget); });

        menu.Items.AddRange([cut, copy, paste, new ToolStripSeparator(), edit, arrange, new ToolStripSeparator()]);
        menu.Items.Add("Add Button…",   null, (_, _) => AddButtonAfter(_menuTarget));
        menu.Items.Add("Add Programs…", null, (_, _) => AddProgramsAfter(_menuTarget));
        menu.Items.Add("Add Separator", null, (_, _) => AddSeparatorAfter(_menuTarget));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(remove);
        var app = new ToolStripMenuItem("Global");   // tray menu items; see BestBarWindow
        if (_fillAppMenu != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(app);
        }

        menu.Opening += (_, _) =>
        {
            _fillAppMenu?.Invoke(app.DropDownItems);
            _menuTarget     = menu.SourceControl?.Tag as ShortcutItem;
            bool onItem     = _menuTarget != null;
            cut.Enabled     = onItem;
            copy.Enabled    = onItem;
            paste.Enabled   = BarClipboard.Item is { IsGroup: false };
            edit.Enabled    = onItem;
            arrange.Enabled = onItem;
            arrange.Checked = onItem && _arrangeTarget == _menuTarget;
            remove.Enabled  = onItem;
        };
        // Any other command while arranging first approves the pending order (see BestBarWindow).
        menu.ItemClicked += (_, e) => { if (e.ClickedItem != arrange && e.ClickedItem != app && _arrangeTarget != null) ExitArrangeMode(); };
        menu.Opened += (_, _) => _menuOpen++;
        menu.Closed += (_, _) => _menuOpen = Math.Max(0, _menuOpen - 1);
        MenuDismiss.Attach(menu);
        return menu;
    }

    // ── Cut / Paste ───────────────────────────────────────────────────────────

    private void CutItem(ShortcutItem sc)
    {
        BarClipboard.Cut(sc);
        _groupBar.Shortcuts.Remove(sc);
        _cfg.SaveBar(_groupBar);
        RebuildItems();
    }

    private void PasteAfterItem(ShortcutItem? after)
    {
        if (_cfg.TakeClipboardItem(Path.GetDirectoryName(_groupBar.FilePath)!) is not ShortcutItem item) return;
        if (after == null) _groupBar.Shortcuts.Add(item);
        else
        {
            int i = _groupBar.Shortcuts.IndexOf(after);
            _groupBar.Shortcuts.Insert(i < 0 ? _groupBar.Shortcuts.Count : i + 1, item);
        }
        _cfg.SaveBar(_groupBar);
        RebuildItems();
    }

    // ── WinForms overrides ────────────────────────────────────────────────────

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RepositionNearAnchor();
        _pollTimer.Start();
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    private void RepositionNearAnchor()
    {
        var screen = Screen.FromRectangle(_anchor).WorkingArea;
        int x, y;
        switch (_parentEdge)
        {
            case DockPosition.Left:
                x = _anchor.Right;
                y = _anchor.Top;
                break;
            case DockPosition.Right:
                x = _anchor.Left - Width;
                y = _anchor.Top;
                break;
            case DockPosition.Top:
                x = _anchor.Left;
                y = _anchor.Bottom;
                break;
            default: // Bottom
                x = _anchor.Left;
                y = _anchor.Top - Height;
                break;
        }
        Location = new Point(
            Math.Max(screen.Left, Math.Min(x, screen.Right  - Width)),
            Math.Max(screen.Top,  Math.Min(y, screen.Bottom - Height)));
    }

    // ── Poll timer ────────────────────────────────────────────────────────────

    private void OnPollTick(object? sender, EventArgs e)
    {
        if (IsDisposed || _menuOpen > 0 || _arrangeTarget != null) return;
        var cursor  = Cursor.Position;
        var popupR  = Bounds.GrowBy(LeaveMargin);
        var anchorR = _anchor.GrowBy(LeaveMargin);
        bool near   = popupR.Contains(cursor) || anchorR.Contains(cursor);
        long now    = Environment.TickCount64;

        if (near)
        {
            _leftSince = -1;
        }
        else
        {
            if (_leftSince < 0) _leftSince = now;
            if (now - _leftSince >= LeaveDelay) BeginInvoke(Close);
        }
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    private void EditItem(ShortcutItem sc)
    {
        _menuOpen++;
        try
        {
            using var dlg = new ShortcutEditorForm(sc);
            PlaceDialog(dlg);
            if (dlg.ShowDialog() == DialogResult.OK)
            {
                _cfg.SaveBar(_groupBar);
                RebuildItems();
            }
        }
        finally { _menuOpen = Math.Max(0, _menuOpen - 1); }
    }

    private void MoveItem(ShortcutItem sc, int delta)
    {
        int i = _groupBar.Shortcuts.IndexOf(sc), j = i + delta;
        if (i < 0 || j < 0 || j >= _groupBar.Shortcuts.Count) return;
        (_groupBar.Shortcuts[i], _groupBar.Shortcuts[j]) = (_groupBar.Shortcuts[j], _groupBar.Shortcuts[i]);
        _cfg.SaveBar(_groupBar);
        RebuildItems();
    }

    private void RemoveItem(ShortcutItem sc)
    {
        _menuOpen++;
        try
        {
            if (!BestBarWindow.ConfirmRemove(this, sc)) return;
        }
        finally { _menuOpen = Math.Max(0, _menuOpen - 1); }
        _groupBar.Shortcuts.Remove(sc);
        _cfg.SaveBar(_groupBar);
        RebuildItems();
    }

    private void AddSeparatorAfter(ShortcutItem? after)
    {
        int i = after == null ? -1 : _groupBar.Shortcuts.IndexOf(after);
        _groupBar.Shortcuts.Insert(i < 0 ? _groupBar.Shortcuts.Count : i + 1, new ShortcutItem());
        _cfg.SaveBar(_groupBar);
        RebuildItems();
    }

    private void AddButtonAfter(ShortcutItem? after)
    {
        var sc = new ShortcutItem();
        _menuOpen++;
        try
        {
            using var dlg = new ShortcutEditorForm(sc);
            PlaceDialog(dlg);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            int i = after == null ? _groupBar.Shortcuts.Count
                                  : _groupBar.Shortcuts.IndexOf(after) + 1;
            _groupBar.Shortcuts.Insert(Math.Clamp(i, 0, _groupBar.Shortcuts.Count), sc);
            _cfg.SaveBar(_groupBar);
            RebuildItems();
        }
        finally { _menuOpen = Math.Max(0, _menuOpen - 1); }
    }

    private void AddProgramsAfter(ShortcutItem? after)
    {
        _menuOpen++;
        try
        {
            using var dlg = new ProgramPickerForm();
            PlaceDialog(dlg);
            if (dlg.ShowDialog() != DialogResult.OK) return;
            int i = after == null ? _groupBar.Shortcuts.Count : _groupBar.Shortcuts.IndexOf(after) + 1;
            _groupBar.Shortcuts.InsertRange(Math.Clamp(i, 0, _groupBar.Shortcuts.Count), dlg.Selected);
            _cfg.SaveBar(_groupBar);
            RebuildItems();
        }
        finally { _menuOpen = Math.Max(0, _menuOpen - 1); }
    }

    // Beside the popup, on the side away from the bar, kept on screen.
    private void PlaceDialog(Form dlg)
    {
        var screen = Screen.FromRectangle(Bounds).WorkingArea;
        var popup  = Bounds;
        dlg.StartPosition = FormStartPosition.Manual;
        var (dx, dy) = _parentEdge switch
        {
            DockPosition.Left  => (popup.Right,             popup.Top),
            DockPosition.Right => (popup.Left - dlg.Width,  popup.Top),
            DockPosition.Top   => (popup.Left,              popup.Bottom),
            _                  => (popup.Left,              popup.Top - dlg.Height),
        };
        dlg.Location = new Point(
            Math.Max(screen.Left, Math.Min(dx, screen.Right  - dlg.Width)),
            Math.Max(screen.Top,  Math.Min(dy, screen.Bottom - dlg.Height)));
    }

    private static void LaunchItem(ShortcutItem sc) => BestBarWindow.Launch(sc);

    // ── Arrange mode ─────────────────────────────────────────────────────────

    private void ToggleArrangeMode(ShortcutItem sc)
    {
        if (_arrangeTarget == sc) ExitArrangeMode();
        else                      EnterArrangeMode(sc);
    }

    private void EnterArrangeMode(ShortcutItem sc)
    {
        ExitArrangeMode();
        _arrangeTarget   = sc;
        _arrangeSnapshot = [.. _groupBar.Shortcuts];
        _menuOpen++;
        HighlightArrangeButton(true);
        InstallKbHook();
        if (_global.ShowArrangeHelp)
        {
            _arrangeHelp = new ArrangeHelpWindow(withGroups: false);
            PlaceArrangeHelp();
        }
    }

    // Moves are only shown while arranging; leaving the mode saves them (keep) or restores the snapshot (Esc).
    private void ExitArrangeMode(bool keep = true)
    {
        if (_arrangeTarget == null) return;
        HighlightArrangeButton(false);
        var snapshot = _arrangeSnapshot;
        _arrangeTarget   = null;
        _arrangeSnapshot = null;
        _menuOpen = Math.Max(0, _menuOpen - 1);
        UninstallKbHook();
        _arrangeHelp?.Close();
        _arrangeHelp = null;

        if (snapshot == null || _groupBar.Shortcuts.SequenceEqual(snapshot)) return;
        if (!keep)
        {
            _groupBar.Shortcuts.Clear();
            _groupBar.Shortcuts.AddRange(snapshot);
        }
        _cfg.SaveBar(_groupBar);
        if (!keep && !IsDisposed) RebuildItems();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        ExitArrangeMode();   // closing while arranging keeps the new order
        base.OnFormClosing(e);
    }

    private void PlaceArrangeHelp()
    {
        if (_arrangeHelp == null) return;
        var item   = _panel.Controls.Cast<Control>().FirstOrDefault(c => c.Tag == _arrangeTarget);
        var anchor = item != null ? item.RectangleToScreen(item.ClientRectangle) : Bounds;
        _arrangeHelp.PlaceBeside(anchor, Bounds, _parentEdge);
    }

    private void HighlightArrangeButton(bool on)
    {
        _panel.Invalidate();
        foreach (Control c in _panel.Controls)
        {
            if (c is BestBarButton btn && btn.Tag == _arrangeTarget)
            {
                btn.FlatAppearance.BorderColor = on ? Color.DodgerBlue : Color.FromArgb(140, 140, 140);
                btn.FlatAppearance.BorderSize  = on ? 2 : (_parentBar.ShowBorders ? 1 : 0);
                return;
            }
        }
    }

    private void OnPanelPaint(object? sender, PaintEventArgs e)
    {
        if (_arrangeTarget == null) return;
        using var pen = new Pen(Color.DodgerBlue, 3);
        e.Graphics.DrawRectangle(pen, 1, 1, _panel.Width - 3, _panel.Height - 3);
    }

    private void InstallKbHook()
    {
        _kbProc = OnKeyboardHook;
        using var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
        _kbHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _kbProc,
                                          Win32.GetModuleHandle(mod.ModuleName!), 0);
    }

    private void UninstallKbHook()
    {
        if (_kbHook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        _kbProc = null;
    }

    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)Win32.WM_KEYDOWN && _arrangeTarget != null)
        {
            var ks = System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            switch ((Keys)ks.vkCode)
            {
                case Keys.Up:   case Keys.Left:  case Keys.Prior:
                    BeginInvoke(() => ArrangeMove(-1)); return (IntPtr)1;
                case Keys.Down: case Keys.Right: case Keys.Next:
                    BeginInvoke(() => ArrangeMove(+1)); return (IntPtr)1;
                case Keys.Home:
                    BeginInvoke(() => ArrangeMoveToExtreme(-1)); return (IntPtr)1;
                case Keys.End:
                    BeginInvoke(() => ArrangeMoveToExtreme(+1)); return (IntPtr)1;
                case Keys.Escape:
                    BeginInvoke(() => ExitArrangeMode(keep: false)); return (IntPtr)1;
                case Keys.Return:
                    BeginInvoke(() => ExitArrangeMode(keep: true));  return (IntPtr)1;
            }
        }
        return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private void ArrangeMove(int delta)
    {
        if (_arrangeTarget == null) return;
        int i = _groupBar.Shortcuts.IndexOf(_arrangeTarget), j = i + delta;
        if (i < 0 || j < 0 || j >= _groupBar.Shortcuts.Count) return;
        (_groupBar.Shortcuts[i], _groupBar.Shortcuts[j]) = (_groupBar.Shortcuts[j], _groupBar.Shortcuts[i]);
        RebuildItems();
    }

    private void ArrangeMoveToExtreme(int dir)
    {
        if (_arrangeTarget == null) return;
        int i = _groupBar.Shortcuts.IndexOf(_arrangeTarget);
        if (i < 0) return;
        var sc = _arrangeTarget;
        _groupBar.Shortcuts.RemoveAt(i);
        if (dir < 0) _groupBar.Shortcuts.Insert(0, sc);
        else         _groupBar.Shortcuts.Add(sc);
        RebuildItems();
    }

    // ── Drag & drop ───────────────────────────────────────────────────────────

    private void HandleDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(ReorderFormat) == true)
            e.Effect = DragDropEffects.Move;
    }

    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(ReorderFormat) == true)
            e.Effect = DragDropEffects.Move;
    }

    private void HandleDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(ReorderFormat) is not ShortcutItem dragged) return;
        var pt      = _panel.PointToClient(new Point(e.X, e.Y));
        int dropIdx = GetReorderDropIndex(pt);
        int fromIdx = _groupBar.Shortcuts.IndexOf(dragged);
        if (fromIdx < 0 || dropIdx == fromIdx) return;
        _groupBar.Shortcuts.RemoveAt(fromIdx);
        if (dropIdx > fromIdx) dropIdx--;
        _groupBar.Shortcuts.Insert(Math.Clamp(dropIdx, 0, _groupBar.Shortcuts.Count), dragged);
        if (_arrangeTarget != null) _arrangeTarget = dragged;
        else                        _cfg.SaveBar(_groupBar);
        RebuildItems();
    }

    private int GetReorderDropIndex(Point panelPt)
    {
        var controls = _panel.Controls.Cast<Control>().ToList();
        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i].Tag is not ShortcutItem sc) break;
            int mid = controls[i].Top + controls[i].Height / 2;
            if (panelPt.Y < mid) return _groupBar.Shortcuts.IndexOf(sc);
        }
        return _groupBar.Shortcuts.Count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { UninstallKbHook(); _arrangeHelp?.Close(); _pollTimer.Dispose(); _menu.Dispose(); }
        base.Dispose(disposing);
    }
}

internal static class RectangleExtensions
{
    public static Rectangle GrowBy(this Rectangle r, int margin) =>
        Rectangle.FromLTRB(r.Left - margin, r.Top - margin, r.Right + margin, r.Bottom + margin);
}
