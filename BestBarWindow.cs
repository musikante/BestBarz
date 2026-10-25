using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BestBarz;

/// <summary>
/// One auto-hiding toolbar on a screen edge: proximity trigger with show/hide delays,
/// optional fading, stretch/center/reverse alignment, drag-and-drop and per-button menus.
/// </summary>
public sealed class BestBarWindow : Form
{
    private const int BarPadding = 2;

    private enum Phase { Hidden, FadingIn, Shown, FadingOut }

    private readonly BarConfig       _bar;
    private readonly GlobalConfig    _global;
    private readonly ConfigService   _cfg;
    private readonly Action<ToolStripItemCollection>? _fillAppMenu;   // tray menu items, for the "BestBarz" submenu
    private readonly FlowLayoutPanel _panel;
    private readonly System.Windows.Forms.Timer _fadeTimer = new();
    private readonly ToolTip         _tip = new() { ShowAlways = true };

    private Phase     _phase = Phase.Hidden;
    private Rectangle _barBounds;
    private long      _nearSince = -1, _awaySince = -1;
    private int       _holdOpen;
    private bool      _suspended;
    private bool      _fullscreenActive;
    private bool      _focusBlocked;
    private GroupPopupWindow?               _activePopup;
    private ShortcutItem?                   _activePopupSc;
    private ContextMenuStrip                _barMenu = null!;
    private ShortcutItem?                   _menuTarget;
    private ShortcutItem?                   _arrangeTarget;
    private ArrangeHelpWindow?              _arrangeHelp;
    private List<ShortcutItem>?             _arrangeSnapshot;   // order on entering Arrange mode, restored by Esc
    private ShortcutItem?                   _pendingGroupDrop;
    private int                             _pendingDelta;
    private IntPtr                          _kbHook = IntPtr.Zero;
    private Win32.LowLevelKeyboardProc?     _kbProc;
    private const string ReorderFormat = "BestBarz.ButtonReorder";


    public event Action? ShortcutsChanged;

    private DockPosition Edge      => _bar.DockPosition;
    private int          EdgeIndex => (int)_bar.DockPosition;
    private bool         Vertical  => Edge is DockPosition.Left or DockPosition.Right;

    public BestBarWindow(BarConfig bar, GlobalConfig global, ConfigService cfg,
                         Action<ToolStripItemCollection>? fillAppMenu = null)
    {
        _bar           = bar;
        _global        = global;
        _cfg           = cfg;
        _fillAppMenu   = fillAppMenu;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        BackColor       = bar.ToolbarColor;
        AllowDrop       = true;

        _panel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            BackColor     = bar.ToolbarColor,
            FlowDirection = Vertical ? FlowDirection.TopDown : FlowDirection.LeftToRight,
            WrapContents  = false,
            AutoScroll    = global.ScrollableBars.Contains(Edge),
            AllowDrop     = true,
            Padding       = new Padding(BarPadding),
            Margin        = Padding.Empty,
        };
        Controls.Add(_panel);

        _fadeTimer.Tick  += OnFadeTick;
        DragEnter        += HandleDragEnter;
        DragDrop         += HandleDragDrop;
        _panel.DragEnter += HandleDragEnter;
        _panel.DragOver  += HandleDragOver;
        _panel.DragDrop  += HandleDragDrop;
        _panel.Paint     += OnPanelPaint;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _barMenu = BuildBarMenu();
        _panel.ContextMenuStrip = _barMenu;
        ContextMenuStrip        = _barMenu;

        RebuildButtons();

        if (bar.Enabled && global.AlwaysVisible.Contains(Edge))
            ShowNow();
    }

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

    // ── Geometry ──────────────────────────────────────────────────────────────

    private Screen TargetScreen =>
        _bar.DisplayTarget >= 0 && _bar.DisplayTarget < Screen.AllScreens.Length
            ? Screen.AllScreens[_bar.DisplayTarget]
            : Screen.PrimaryScreen!;

    // By default WorkingArea, like Coolbarz: bars sit beside the Windows taskbar instead of over it.
    // Layout and edge trigger must use the same area, so the cursor never crosses a gap to reach the bar.
    private Rectangle EdgeArea =>
        _global.FullScreenBars.Contains(Edge) ? TargetScreen.Bounds : TargetScreen.WorkingArea;

    // Length the items need along the edge, including the bar's padding.
    private int ItemsLength()
    {
        int along = 2 * BarPadding;
        foreach (Control c in _panel.Controls)
            along += Vertical ? c.Height + c.Margin.Vertical : c.Width + c.Margin.Horizontal;
        return along;
    }

    // Stretch fills the whole edge; its items sit in the middle instead of at one end.
    // FlowLayoutPanel can't center, so the leading padding takes half the spare length.
    private void CenterStretchedItems()
    {
        int len  = Vertical ? _barBounds.Height : _barBounds.Width;
        int lead = _global.StretchBars.Contains(Edge) ? Math.Max(0, (len - ItemsLength()) / 2) : 0;
        var pad  = Vertical ? new Padding(BarPadding, BarPadding + lead, BarPadding, BarPadding)
                            : new Padding(BarPadding + lead, BarPadding, BarPadding, BarPadding);
        if (_panel.Padding != pad) _panel.Padding = pad;
    }

    private Rectangle ComputeBounds()
    {
        var area  = EdgeArea;
        int along = ItemsLength(), across = 0;
        foreach (Control c in _panel.Controls)
            across = Math.Max(across, Vertical ? c.Width + c.Margin.Horizontal : c.Height + c.Margin.Vertical);
        across += 2 * BarPadding;

        bool stretch = _global.StretchBars.Contains(Edge);
        int areaLen  = Vertical ? area.Height : area.Width;
        int len      = stretch ? areaLen : Math.Min(along, areaLen);
        if (along > len && _panel.AutoScroll)
            across += Vertical ? SystemInformation.VerticalScrollBarWidth : SystemInformation.HorizontalScrollBarHeight;

        int offset = stretch                                     ? 0
                   : _global.CenterBars.Contains(Edge)       ? (areaLen - len) / 2
                   : _global.ReverseAlignBars.Contains(Edge) ? areaLen - len
                   :                                               0;

        return Edge switch
        {
            DockPosition.Left   => new Rectangle(area.Left,           area.Top + offset, across, len),
            DockPosition.Right  => new Rectangle(area.Right - across, area.Top + offset, across, len),
            DockPosition.Top    => new Rectangle(area.Left + offset,  area.Top,          len,    across),
            _                   => new Rectangle(area.Left + offset,  area.Bottom - across, len, across),
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        // A bar that was never shown has no window handle, and BeginInvoke would throw.
        if (!IsHandleCreated) { _barBounds = ComputeBounds(); CenterStretchedItems(); return; }
        BeginInvoke(() =>
        {
            _barBounds = ComputeBounds();
            CenterStretchedItems();
            if (Visible) Bounds = _barBounds;
        });
    }

    // ── Proximity ─────────────────────────────────────────────────────────────

    public void SuspendPolling() { _suspended = true;  HideNow(); }
    public void ResumePolling()  { _suspended = false; }

    public void SetFullscreenActive(bool active)
    {
        if (active == _fullscreenActive) return;
        _fullscreenActive = active;
        if (active) HideNow();
    }

    public void SetFocusBlocked(bool blocked) => _focusBlocked = blocked;

    public void Poll(Point cursor)
    {
        if (IsDisposed || _suspended) return;

        if (WindowState == FormWindowState.Minimized && _phase != Phase.Hidden)
        {
            _fadeTimer.Stop();
            _phase = Phase.Hidden;
        }

        if (_global.AlwaysVisible.Contains(Edge) && !_fullscreenActive)
        {
            if (_phase != Phase.Shown) ShowNow();
            return;
        }

        // Fullscreen apps and "only on desktop" block the edge trigger; holding Ctrl overrides both.
        // GetAsyncKeyState because the bar never has focus, so Control.ModifierKeys wouldn't see the key.
        bool blocked = _fullscreenActive || _focusBlocked || BesideTaskbar;
        bool ctrl    = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        bool atEdge  = IsAtEdge(cursor, ctrl) && (!blocked || ctrl);
        bool want    = _holdOpen > 0 || (_phase != Phase.Hidden && (Bounds.Contains(cursor) || EdgeStrip.Contains(cursor))) || atEdge;
        long now  = Environment.TickCount64;

        if (want)
        {
            _awaySince = -1;
            if (_nearSince < 0) _nearSince = now;
            if ((_phase is Phase.Hidden or Phase.FadingOut) && now - _nearSince >= _global.ShowDelay[EdgeIndex])
                BeginShow();
        }
        else
        {
            _nearSince = -1;
            if (_awaySince < 0) _awaySince = now;
            if ((_phase is Phase.Shown or Phase.FadingIn) && now - _awaySince >= _global.HideDelay[EdgeIndex])
                BeginHide();
        }
    }

    // The strip the bar would fill if stretched: the whole edge, as deep as the bar. The trigger is the
    // whole edge, so the way from where the bar opened to the bar itself runs through here — an open
    // bar stays open in it instead of hiding as soon as the cursor leaves the thin trigger band.
    private Rectangle EdgeStrip
    {
        get
        {
            Rectangle a = EdgeArea, b = _barBounds;
            return Edge switch
            {
                DockPosition.Left  => Rectangle.FromLTRB(a.Left, a.Top, b.Right, a.Bottom),
                DockPosition.Right => Rectangle.FromLTRB(b.Left, a.Top, a.Right, a.Bottom),
                DockPosition.Top   => Rectangle.FromLTRB(a.Left, a.Top, a.Right, b.Bottom),
                _                  => Rectangle.FromLTRB(a.Left, b.Top, a.Right, a.Bottom),
            };
        }
    }

    // An appbar — normally the Windows taskbar, on whichever side — occupies this edge: the trigger
    // zone is on the way to it (or, with "Cover the taskbar", on it), so a plain pass would open the
    // bar in vain. Such a bar only opens with Ctrl.
    private bool BesideTaskbar
    {
        get
        {
            var screen = TargetScreen;
            Rectangle wa = screen.WorkingArea, all = screen.Bounds;
            return Edge switch
            {
                DockPosition.Left  => wa.Left   > all.Left,
                DockPosition.Right => wa.Right  < all.Right,
                DockPosition.Top   => wa.Top    > all.Top,
                _                  => wa.Bottom < all.Bottom,
            };
        }
    }

    // With Ctrl, the Windows taskbar strip beyond the working area also counts, so the bar next to
    // the taskbar can be reached from it. Without Ctrl the taskbar stays the taskbar's.
    private bool IsAtEdge(Point p, bool ctrl)
    {
        var area = EdgeArea;
        if (!(ctrl ? TargetScreen.Bounds : area).Contains(p)) return false;

        if (InProtectedCorner(p, area)) return false;

        // The whole edge triggers, whatever the bar's length and alignment. Each corner is left out —
        // a square as deep as the neighbouring edge's band — so it doesn't open the two bars meeting there.
        int Band(DockPosition e) => _global.PointerDistance[(int)e] + 1;
        int d = Band(Edge) - 1;
        bool alongY = p.Y >= area.Top + Band(DockPosition.Top)  && p.Y < area.Bottom - Band(DockPosition.Bottom);
        bool alongX = p.X >= area.Left + Band(DockPosition.Left) && p.X < area.Right - Band(DockPosition.Right);
        return Edge switch
        {
            DockPosition.Left   => p.X <= area.Left + d       && alongY,
            DockPosition.Right  => p.X >= area.Right - 1 - d  && alongY,
            DockPosition.Top    => p.Y <= area.Top + d        && alongX,
            _                   => p.Y >= area.Bottom - 1 - d && alongX,
        };
    }

    // A maximized window keeps its minimize/maximize/close buttons in the top-right corner (and its
    // icon or menu top-left): with "protect corner" on, reaching them opens no bar (Top, Left or Right).
    private bool InProtectedCorner(Point p, Rectangle area)
    {
        float scale = DeviceDpi / 96f;
        int w = (int)(150 * scale), h = (int)(40 * scale);
        return p.Y < area.Top + h &&
               ((_global.ProtectTopLeftCorner  && p.X <  area.Left + w) ||
                (_global.ProtectTopRightCorner && p.X >= area.Right - w));
    }

    // ── Show / hide / fade ────────────────────────────────────────────────────

    private void BeginShow()
    {
        if (!_global.ToolbarFading) { ShowNow(); return; }
        if (_phase == Phase.Hidden)
        {
            Opacity = 0;
            RestoreAt(_barBounds);
        }
        _phase = Phase.FadingIn;
        _fadeTimer.Interval = Math.Max(1, _global.ToolbarFadeInTime);
        _fadeTimer.Start();
    }

    private void BeginHide()
    {
        if (!_global.ToolbarFading) { HideNow(); return; }
        _phase = Phase.FadingOut;
        _fadeTimer.Interval = Math.Max(1, _global.ToolbarFadeOutTime);
        _fadeTimer.Start();
    }

    public void ShowNow()
    {
        _fadeTimer.Stop();
        Opacity = _global.MaxOpacity;
        RestoreAt(_barBounds);
        _phase = Phase.Shown;
    }

    private void RestoreAt(Rectangle bounds)
    {
        if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
        Bounds = bounds;
        if (!Visible) Show();
        // Showing keeps the old z-order, and the Windows 11 taskbar (also topmost) puts itself on top
        // whenever it is used: a bar over it would appear hidden behind it. Bring the bar to the front.
        Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                           Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public void HideNow()
    {
        ExitArrangeMode();
        _barMenu.Close();
        CloseActivePopup();
        _fadeTimer.Stop();
        Hide();
        _phase = Phase.Hidden;
        _nearSince = _awaySince = -1;
    }

    private void OnFadeTick(object? sender, EventArgs e)
    {
        if (_phase == Phase.FadingIn)
        {
            Opacity = Math.Min(Opacity + _global.ToolbarFadeInInterval, _global.MaxOpacity);
            if (Opacity >= _global.MaxOpacity - 0.001) { _fadeTimer.Stop(); _phase = Phase.Shown; }
        }
        else if (_phase == Phase.FadingOut)
        {
            Opacity = Math.Max(Opacity - _global.ToolbarFadeOutInterval, 0.0);
            if (Opacity <= 0.001) HideNow();
        }
        else
        {
            _fadeTimer.Stop();
        }
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    public void RebuildButtons()
    {
        _panel.SuspendLayout();
        _tip.RemoveAll();
        var old = _panel.Controls.Cast<Control>().ToArray();
        _panel.Controls.Clear();
        foreach (var c in old) c.Dispose();

        var style    = _global.ButtonLayout[EdgeIndex];
        int maxWidth = _global.MaxButtonWidth[EdgeIndex];
        var buttons  = new List<Control>();
        var seps     = new List<Control>();

        foreach (var sc in _bar.Shortcuts)
        {
            if (sc.IsSeparator)
            {
                var sep = CreateSeparator(sc);
                seps.Add(sep);
                _panel.Controls.Add(sep);
                continue;
            }

            var btn = new BestBarButton(sc, style, _bar, maxWidth);
            btn.Tag = sc;
            btn.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && _arrangeTarget != null)
                    btn.DoDragDrop(new DataObject(ReorderFormat, sc), DragDropEffects.Move | DragDropEffects.Link);
            };

            if (sc.IsGroup)
            {
                btn.Click += (_, _) =>
                {
                    if (_arrangeTarget != null) return;
                    if (_activePopupSc == sc) { CloseActivePopup(); return; }
                    OpenGroupPopup(sc, btn);
                };
                btn.AllowDrop  = true;
                btn.DragEnter += (_, e) =>
                {
                    if (DraggedItem(e) is not { } dragged) return;
                    if (dragged.IsGroup) { e.Effect = DragDropEffects.Move; return; }
                    e.Effect      = DragDropEffects.Link;
                    btn.Cursor    = Cursors.Default;
                    btn.BackColor = ControlPaint.Light(_bar.ButtonColor, 0.5f);
                    btn.FlatAppearance.BorderColor = Color.DodgerBlue;
                    btn.FlatAppearance.BorderSize  = 2;
                };
                btn.DragLeave += (_, _) =>
                {
                    btn.Cursor    = Cursors.Hand;
                    btn.BackColor = _bar.ButtonColor;
                    btn.FlatAppearance.BorderColor = Color.FromArgb(140, 140, 140);
                    btn.FlatAppearance.BorderSize  = _bar.ShowBorders ? 1 : 0;
                };
                btn.DragOver  += (_, e) =>
                {
                    if (DraggedItem(e) is { } dragged)
                        e.Effect = dragged.IsGroup ? DragDropEffects.Move : DragDropEffects.Link;
                };
                btn.DragDrop  += (_, e) =>
                {
                    btn.BackColor = _bar.ButtonColor;
                    btn.FlatAppearance.BorderColor = Color.FromArgb(140, 140, 140);
                    btn.FlatAppearance.BorderSize  = _bar.ShowBorders ? 1 : 0;
                    if (DraggedItem(e) is not { } dragged || dragged == sc) return;
                    if (dragged.IsGroup) { HandleDragDrop(btn, e); return; }
                    ExitArrangeMode();   // moving into a group touches another file: it approves, like Enter
                    DropIntoGroup(dragged, sc);
                };
                btn.ContextMenuStrip = _barMenu;
                if (_global.ShowToolTips && (!btn.ShowsText || btn.TextTruncated))
                    _tip.SetToolTip(btn, sc.Text);
            }
            else
            {
                btn.Click += (_, _) => { if (_arrangeTarget == null) Launch(sc); };
                btn.ContextMenuStrip = _barMenu;
                if (_global.ShowToolTips)
                    _tip.SetToolTip(btn, string.IsNullOrEmpty(sc.Text) ? sc.Target : $"{sc.Text}\n{sc.Target}");
            }

            buttons.Add(btn);
            _panel.Controls.Add(btn);
        }

        if (_bar.Shortcuts.Count == 0)
        {
            var hint = new Label
            {
                Text             = "Drop files here",
                TextAlign        = ContentAlignment.MiddleCenter,
                ForeColor        = Color.Gray,
                AutoSize         = false,
                Size             = Vertical ? new Size(80, 40) : new Size(110, 24),
                ContextMenuStrip = _barMenu,
            };
            buttons.Add(hint);
            _panel.Controls.Add(hint);
        }

        int across = buttons.Count == 0 ? 24 : buttons.Max(b => Vertical ? b.Width : b.Height);
        foreach (var c in buttons.Concat(seps))
            if (Vertical) c.Width = across; else c.Height = across;

        _panel.ResumeLayout(true);
        if (_arrangeTarget != null) HighlightArrangeButton(true);
        _barBounds = ComputeBounds();
        CenterStretchedItems();
        if (Visible) Bounds = _barBounds;
        PlaceArrangeHelp();
    }

    private Label CreateSeparator(ShortcutItem sc)
    {
        bool labelled = !string.IsNullOrEmpty(sc.Text);
        var sep = new Label
        {
            AutoSize    = false,
            Text        = sc.Text,
            UseMnemonic = false,
            BackColor   = _bar.VisibleSeparatorColor,
            ForeColor   = _bar.SeparatorTextColor,
            Margin      = new Padding(1, 2, 1, 2),
            TextAlign   = _bar.SeparatorTextAlign switch
            {
                "Left"  or "Near" => ContentAlignment.MiddleLeft,
                "Right" or "Far"  => ContentAlignment.MiddleRight,
                _                 => ContentAlignment.MiddleCenter,
            },
        };
        if (Vertical) sep.Height = labelled ? 18 : 3;
        else          sep.Width  = labelled ? TextRenderer.MeasureText(sc.Text, sep.Font).Width + 8 : 3;
        sep.Tag              = sc;
        sep.ContextMenuStrip = _barMenu;
        return sep;
    }

    // ── Group popup ───────────────────────────────────────────────────────────

    private void OpenGroupPopup(ShortcutItem sc, BestBarButton btn)
    {
        CloseActivePopup();
        var groupBar = _cfg.LoadGroupBar(sc.GroupFile)
                       ?? new BarConfig { Name = Path.GetFileNameWithoutExtension(sc.GroupFile), FilePath = sc.GroupFile };
        var anchorBounds = btn.RectangleToScreen(btn.ClientRectangle);
        var popup = new GroupPopupWindow(_cfg, groupBar, _global, _bar, Edge, anchorBounds, fillAppMenu: _fillAppMenu);
        _activePopup   = popup;
        _activePopupSc = sc;
        _holdOpen++;
        popup.FormClosed += (_, _) =>
        {
            _holdOpen = Math.Max(0, _holdOpen - 1);
            if (_activePopup == popup) { _activePopup = null; _activePopupSc = null; }
        };
        popup.Show(this);
    }

    private void CloseActivePopup()
    {
        _activePopup?.Close();
        _activePopup   = null;
        _activePopupSc = null;
    }

    // An open popup holds its own copy of the group's items and saves it on every edit, so it must
    // close before the bar writes, moves or renames that group's file — or it would overwrite it later.
    private void ClosePopupOf(ShortcutItem groupSc)
    {
        if (_activePopupSc == groupSc) CloseActivePopup();
    }

    // ── Shortcut operations ───────────────────────────────────────────────────

    private static void Launch(ShortcutItem sc)
    {
        try
        {
            var workDir = sc.WorkingDirectory;
            if (string.IsNullOrEmpty(workDir))
                workDir = Directory.Exists(sc.Target) ? sc.Target : Path.GetDirectoryName(sc.Target) ?? "";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName         = sc.Target,
                Arguments        = sc.Arguments,
                WorkingDirectory = workDir,
                UseShellExecute  = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not launch:\n{sc.Target}\n\n{ex.Message}", "BestBarz",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void CommitChange() => BeginInvoke(() =>
    {
        _cfg.SaveBar(_bar);
        RebuildButtons();
        ShortcutsChanged?.Invoke();
    });

    private void EditShortcut(ShortcutItem sc)
    {
        _holdOpen++;
        try
        {
            using var dlg = new ShortcutEditorForm(sc);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location      = DialogLocationOutside(dlg.Size);
            if (dlg.ShowDialog(this) == DialogResult.OK) CommitChange();
        }
        finally { _holdOpen--; }
    }

    private Point DialogLocationOutside(Size dlgSize)
    {
        var bar    = Bounds;
        var screen = Screen.FromRectangle(bar).WorkingArea;
        int x, y;
        switch (_bar.DockPosition)
        {
            case DockPosition.Left:
                x = bar.Right;
                y = Math.Min(bar.Top, screen.Bottom - dlgSize.Height);
                break;
            case DockPosition.Right:
                x = bar.Left - dlgSize.Width;
                y = Math.Min(bar.Top, screen.Bottom - dlgSize.Height);
                break;
            case DockPosition.Top:
                x = Math.Min(bar.Left, screen.Right - dlgSize.Width);
                y = bar.Bottom;
                break;
            default: // Bottom
                x = Math.Min(bar.Left, screen.Right - dlgSize.Width);
                y = bar.Top - dlgSize.Height;
                break;
        }
        return new Point(
            Math.Max(screen.Left, Math.Min(x, screen.Right  - dlgSize.Width)),
            Math.Max(screen.Top,  Math.Min(y, screen.Bottom - dlgSize.Height)));
    }

    private void MoveShortcut(ShortcutItem sc, int delta)
    {
        int i = _bar.Shortcuts.IndexOf(sc), j = i + delta;
        if (i < 0 || j < 0 || j >= _bar.Shortcuts.Count) return;
        (_bar.Shortcuts[i], _bar.Shortcuts[j]) = (_bar.Shortcuts[j], _bar.Shortcuts[i]);
        CommitChange();
    }

    private void AddSeparatorAfter(ShortcutItem? after)
    {
        InsertAfter(new ShortcutItem(), after);
        CommitChange();
    }

    private void RemoveShortcut(ShortcutItem sc)
    {
        _holdOpen++;
        try
        {
            if (!ConfirmRemove(this, sc)) return;
        }
        finally { _holdOpen = Math.Max(0, _holdOpen - 1); }
        ClosePopupOf(sc);
        _bar.Shortcuts.Remove(sc);
        CommitChange();
    }

    internal static bool ConfirmRemove(IWin32Window owner, ShortcutItem sc)
    {
        string what = sc.IsSeparator ? "this separator"
                    : sc.IsGroup     ? $"the group \"{sc.Text}\""
                    :                  $"\"{sc.Text}\"";
        return MessageBox.Show(owner, $"Remove {what}?", "BestBarz",
                   MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
               == DialogResult.Yes;
    }

    // ── Drag & drop ───────────────────────────────────────────────────────────

    private void HandleDragEnter(object? sender, DragEventArgs e)
    {
        if (DraggedItem(e) != null)
            e.Effect = DragDropEffects.Move;
        else if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            e.Effect = DragDropEffects.Link;
    }

    // Only this bar's own items: one dragged from another bar would be copied, not moved.
    private ShortcutItem? DraggedItem(DragEventArgs e) =>
        e.Data?.GetData(ReorderFormat) is ShortcutItem sc && _bar.Shortcuts.Contains(sc) ? sc : null;

    private void HandleDragOver(object? sender, DragEventArgs e)
    {
        if (DraggedItem(e) == null) return;
        var pt = _panel.PointToClient(new Point(e.X, e.Y));
        var hit = _panel.GetChildAtPoint(pt);
        e.Effect = (hit?.Tag is ShortcutItem sc && sc.IsGroup && DraggedItem(e) is { IsGroup: false })
            ? DragDropEffects.Link   // cursor distinto indica "mover al grupo"
            : DragDropEffects.Move;
    }

    private void HandleDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(ReorderFormat) == true)
        {
            if (DraggedItem(e) is not { } draggedSc) return;
            var pt      = _panel.PointToClient(new Point(e.X, e.Y));
            int dropIdx = GetReorderDropIndex(pt);
            int fromIdx = _bar.Shortcuts.IndexOf(draggedSc);
            if (fromIdx < 0 || dropIdx == fromIdx) return;
            _bar.Shortcuts.RemoveAt(fromIdx);
            if (dropIdx > fromIdx) dropIdx--;
            _bar.Shortcuts.Insert(Math.Clamp(dropIdx, 0, _bar.Shortcuts.Count), draggedSc);
            if (_arrangeTarget != null) { _arrangeTarget = draggedSc; ArrangeRefresh(); }
            else                        CommitChange();
            return;
        }
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files)
            _bar.Shortcuts.Add(new ShortcutItem
            {
                Target = f,
                Text   = Directory.Exists(f) ? Path.GetFileName(f.TrimEnd('\\')) : Path.GetFileNameWithoutExtension(f),
            });
        CommitChange();
    }

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
        _arrangeSnapshot = [.. _bar.Shortcuts];
        _holdOpen++;
        HighlightArrangeButton(true);
        InstallKbHook();
        if (_global.ShowArrangeHelp)
        {
            _arrangeHelp = new ArrangeHelpWindow(withGroups: true);
            PlaceArrangeHelp();
        }
    }

    private void PlaceArrangeHelp()
    {
        if (_arrangeHelp == null) return;
        var item   = _panel.Controls.Cast<Control>().FirstOrDefault(c => c.Tag == _arrangeTarget);
        var anchor = item != null ? item.RectangleToScreen(item.ClientRectangle) : Bounds;
        _arrangeHelp.PlaceBeside(anchor, Bounds, Edge);
    }

    // Moves are only shown while arranging; leaving the mode saves them (keep) or restores the
    // snapshot (Esc). dropPending: Enter on an orange-highlighted group moves the item into it.
    private void ExitArrangeMode(bool keep = true, bool dropPending = false)
    {
        if (_arrangeTarget == null) return;
        if (_pendingGroupDrop != null)
            HighlightGroupForDrop(_pendingGroupDrop, false);
        HighlightArrangeButton(false);
        var target   = _arrangeTarget;
        var pending  = _pendingGroupDrop;
        var snapshot = _arrangeSnapshot;
        _pendingGroupDrop = null;
        _arrangeTarget    = null;
        _arrangeSnapshot  = null;
        _holdOpen = Math.Max(0, _holdOpen - 1);
        UninstallKbHook();
        _arrangeHelp?.Close();
        _arrangeHelp = null;

        bool changed = snapshot != null && !_bar.Shortcuts.SequenceEqual(snapshot);
        if (!keep)
        {
            if (!changed) return;
            _bar.Shortcuts.Clear();
            _bar.Shortcuts.AddRange(snapshot!);
            CommitChange();
        }
        else if (dropPending && pending != null) DropIntoGroup(target, pending);
        else if (changed)                        CommitChange();
    }

    private void ArrangeRefresh() => BeginInvoke(RebuildButtons);

    private void HighlightArrangeButton(bool on)
    {
        _panel.Invalidate();
        foreach (Control c in _panel.Controls)
        {
            if (c is BestBarButton btn && btn.Tag == _arrangeTarget)
            {
                btn.FlatAppearance.BorderColor = on ? Color.DodgerBlue : Color.FromArgb(140, 140, 140);
                btn.FlatAppearance.BorderSize  = on ? 2 : (_bar.ShowBorders ? 1 : 0);
                return;
            }
        }
    }

    private void HighlightGroupForDrop(ShortcutItem groupSc, bool on)
    {
        foreach (Control c in _panel.Controls)
        {
            if (c is BestBarButton btn && btn.Tag == groupSc)
            {
                btn.BackColor = on ? ControlPaint.Light(_bar.ButtonColor, 0.5f) : _bar.ButtonColor;
                btn.FlatAppearance.BorderColor = on ? Color.Orange : Color.FromArgb(140, 140, 140);
                btn.FlatAppearance.BorderSize  = on ? 2 : (_bar.ShowBorders ? 1 : 0);
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
            var ks = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            switch ((Keys)ks.vkCode)
            {
                case Keys.Left:  case Keys.Up:   case Keys.Prior:
                    BeginInvoke(() => ArrangeMove(-1)); return (IntPtr)1;
                case Keys.Right: case Keys.Down: case Keys.Next:
                    BeginInvoke(() => ArrangeMove(+1)); return (IntPtr)1;
                case Keys.Home:
                    BeginInvoke(() => ArrangeMoveToExtreme(-1)); return (IntPtr)1;
                case Keys.End:
                    BeginInvoke(() => ArrangeMoveToExtreme(+1)); return (IntPtr)1;
                case Keys.Escape:
                    BeginInvoke(() => ExitArrangeMode(keep: false)); return (IntPtr)1;
                case Keys.Return:
                    BeginInvoke(() => ExitArrangeMode(keep: true, dropPending: true)); return (IntPtr)1;
            }
        }
        return Win32.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private void ArrangeMove(int delta)
    {
        if (_arrangeTarget == null) return;

        if (_pendingGroupDrop != null)
        {
            var group = _pendingGroupDrop;
            HighlightGroupForDrop(group, false);
            _pendingGroupDrop = null;

            if (delta == _pendingDelta)
            {
                // Misma dirección: pasar por encima del grupo (swap)
                int i2 = _bar.Shortcuts.IndexOf(_arrangeTarget);
                int j2 = i2 + delta;
                if (i2 >= 0 && j2 >= 0 && j2 < _bar.Shortcuts.Count)
                    (_bar.Shortcuts[i2], _bar.Shortcuts[j2]) = (_bar.Shortcuts[j2], _bar.Shortcuts[i2]);
                ArrangeRefresh();
                return;
            }
            // Dirección contraria: cancelar hover y seguir con movimiento normal
        }

        int i = _bar.Shortcuts.IndexOf(_arrangeTarget), j = i + delta;
        if (i < 0 || j < 0 || j >= _bar.Shortcuts.Count) return;
        var neighbor = _bar.Shortcuts[j];
        // Groups can't nest (popups don't show groups), so a group just passes over other groups.
        if (neighbor.IsGroup && !_arrangeTarget.IsGroup)
        {
            // Primera vez que toca el grupo: hover, esperar confirmación
            _pendingGroupDrop = neighbor;
            _pendingDelta     = delta;
            HighlightGroupForDrop(neighbor, true);
            return;
        }
        (_bar.Shortcuts[i], _bar.Shortcuts[j]) = (_bar.Shortcuts[j], _bar.Shortcuts[i]);
        ArrangeRefresh();
    }

    private void ArrangeMoveToExtreme(int dir)
    {
        if (_arrangeTarget == null) return;
        if (_pendingGroupDrop != null)
        {
            HighlightGroupForDrop(_pendingGroupDrop, false);
            _pendingGroupDrop = null;
        }
        int i = _bar.Shortcuts.IndexOf(_arrangeTarget);
        if (i < 0) return;
        var sc = _arrangeTarget;
        _bar.Shortcuts.RemoveAt(i);
        if (dir < 0) _bar.Shortcuts.Insert(0, sc);
        else         _bar.Shortcuts.Add(sc);
        ArrangeRefresh();
    }

    private int GetReorderDropIndex(Point panelPt)
    {
        var controls = _panel.Controls.Cast<Control>().ToList();
        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i].Tag is not ShortcutItem) break;
            int mid = Vertical ? controls[i].Top  + controls[i].Height / 2
                                : controls[i].Left + controls[i].Width  / 2;
            if ((Vertical ? panelPt.Y : panelPt.X) < mid) return i;
        }
        return _bar.Shortcuts.Count;
    }

    // ── Bar background menu ───────────────────────────────────────────────────

    private ContextMenuStrip BuildBarMenu()
    {
        var menu      = new ContextMenuStrip();
        var cut       = new ToolStripMenuItem("Cut",              null, (_, _) => { if (_menuTarget != null) CutShortcut(_menuTarget); });
        var copy      = new ToolStripMenuItem("Copy",             null, (_, _) => { if (_menuTarget != null) BarClipboard.Copy(_menuTarget); });
        var paste     = new ToolStripMenuItem("Paste",            null, (_, _) => PasteAfter(_menuTarget));
        var pasteInto = new ToolStripMenuItem("Paste Into Group", null, (_, _) => { if (_menuTarget != null) PasteIntoGroup(_menuTarget); });
        var edit      = new ToolStripMenuItem("Edit…",            null, (_, _) =>
        {
            if (_menuTarget == null) return;
            if (_menuTarget.IsGroup) RenameGroup(_menuTarget);
            else                     EditShortcut(_menuTarget);
        });
        var arrange   = new ToolStripMenuItem("Arrange mode",     null, (_, _) => { if (_menuTarget != null) ToggleArrangeMode(_menuTarget); });
        var remove    = new ToolStripMenuItem("Remove",           null, (_, _) => { if (_menuTarget != null) RemoveShortcut(_menuTarget); });

        menu.Items.AddRange([cut, copy, paste, pasteInto, new ToolStripSeparator(), edit, arrange, new ToolStripSeparator()]);
        menu.Items.Add("Add Button…",   null, (_, _) => AddButtonAfter(_menuTarget));
        menu.Items.Add("Add Group…",    null, (_, _) => AddGroupAfter(_menuTarget));
        menu.Items.Add("Add Separator", null, (_, _) => AddSeparatorAfter(_menuTarget));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(remove);
        // Submenu with the tray menu's items (Settings, Hide bars, bar toggles, Exit). Being part of
        // this menu, it keeps the bar open while in use — a separate menu died when the bar hid.
        var app = new ToolStripMenuItem("BestBarz");
        if (_fillAppMenu != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(app);
        }

        menu.Opening += (_, _) =>
        {
            _fillAppMenu?.Invoke(app.DropDownItems);
            _menuTarget       = menu.SourceControl?.Tag as ShortcutItem;
            bool onItem       = _menuTarget != null;
            bool hasClip      = BarClipboard.Item != null;
            cut.Enabled       = onItem;
            copy.Enabled      = onItem;
            paste.Enabled     = hasClip;
            pasteInto.Enabled = hasClip && !BarClipboard.Item!.IsGroup && _menuTarget?.IsGroup == true;
            edit.Enabled      = onItem;
            edit.Text         = _menuTarget?.IsGroup == true ? "Rename…" : "Edit…";
            arrange.Enabled   = onItem;
            arrange.Checked   = onItem && _arrangeTarget == _menuTarget;
            remove.Enabled    = onItem;
        };
        // Any other command while arranging (Cut, Remove, …) first approves the pending order,
        // so Esc can't later restore a list those commands already changed on disk.
        menu.ItemClicked += (_, e) => { if (e.ClickedItem != arrange && e.ClickedItem != app && _arrangeTarget != null) ExitArrangeMode(); };
        menu.Opened += (_, _) => _holdOpen++;
        menu.Closed += (_, _) => _holdOpen = Math.Max(0, _holdOpen - 1);
        MenuDismiss.Attach(menu);
        return menu;
    }

    // ── Add / rename operations ───────────────────────────────────────────────

    private void CutShortcut(ShortcutItem sc)
    {
        ClosePopupOf(sc);
        BarClipboard.Cut(sc);
        _bar.Shortcuts.Remove(sc);
        CommitChange();
    }

    private void PasteAfter(ShortcutItem? after)
    {
        if (_cfg.TakeClipboardItem(_bar.FolderPath) is not ShortcutItem item) return;
        InsertAfter(item, after);
        CommitChange();
    }

    private void DropIntoGroup(ShortcutItem draggedSc, ShortcutItem groupSc)
    {
        ClosePopupOf(groupSc);
        _bar.Shortcuts.Remove(draggedSc);
        var groupBar = _cfg.LoadGroupBar(groupSc.GroupFile)
                       ?? new BarConfig { Name = Path.GetFileNameWithoutExtension(groupSc.GroupFile), FilePath = groupSc.GroupFile };
        groupBar.Shortcuts.Add(draggedSc);
        _cfg.SaveBar(groupBar);
        CommitChange();
    }

    private void PasteIntoGroup(ShortcutItem groupSc)
    {
        ClosePopupOf(groupSc);
        if (_cfg.TakeClipboardItem(Path.GetDirectoryName(groupSc.GroupFile)!) is not ShortcutItem item) return;
        var groupBar = _cfg.LoadGroupBar(groupSc.GroupFile)
                       ?? new BarConfig { Name = Path.GetFileNameWithoutExtension(groupSc.GroupFile), FilePath = groupSc.GroupFile };
        groupBar.Shortcuts.Add(item);
        _cfg.SaveBar(groupBar);
    }

    private void AddButtonAfter(ShortcutItem? after)
    {
        var sc = new ShortcutItem();
        _holdOpen++;
        try
        {
            using var dlg = new ShortcutEditorForm(sc);
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location      = DialogLocationOutside(dlg.Size);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            InsertAfter(sc, after);
            CommitChange();
        }
        finally { _holdOpen--; }
    }

    private void AddGroupAfter(ShortcutItem? after)
    {
        string? name = PromptName("Add Group", "Group name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrEmpty(safeName)) return;
        // Never reuse an existing file: it belongs to another group (or is the bar's own _bar.xml).
        var groupFile = ConfigService.UniqueGroupPath(_bar.FolderPath, safeName);
        _cfg.SaveBar(new BarConfig { Name = safeName, FilePath = groupFile });
        InsertAfter(new ShortcutItem { Text = name, GroupFile = groupFile }, after);
        CommitChange();
    }

    private void RenameGroup(ShortcutItem sc)
    {
        string? name = PromptName("Rename Group", "Group name:", sc.Text);
        if (name == null || name == sc.Text) return;
        var safeName = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrEmpty(safeName)) return;
        var newFile = Path.Combine(Path.GetDirectoryName(sc.GroupFile)!, safeName + ".xml");
        if (!string.Equals(sc.GroupFile, newFile, StringComparison.OrdinalIgnoreCase))
        {
            ClosePopupOf(sc);
            _cfg.NoteOwnWrite();
            try { File.Move(sc.GroupFile, newFile, overwrite: false); }
            catch { return; }   // target already exists or file locked — abort rename
        }
        sc.Text      = name;
        sc.GroupFile = newFile;
        CommitChange();
    }

    private void InsertAfter(ShortcutItem sc, ShortcutItem? after)
    {
        if (after == null) _bar.Shortcuts.Add(sc);
        else
        {
            int i = _bar.Shortcuts.IndexOf(after);
            _bar.Shortcuts.Insert(i < 0 ? _bar.Shortcuts.Count : i + 1, sc);
        }
    }

    private string? PromptName(string title, string label, string initial = "")
    {
        using var dlg = new Form
        {
            Text            = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.Manual,
            MinimizeBox     = false,
            MaximizeBox     = false,
            ShowInTaskbar   = false,
            ClientSize      = new Size(268, 84),
        };
        dlg.Location = DialogLocationOutside(dlg.Size);
        var lbl = new Label   { Text = label, Location = new Point(8, 10), AutoSize = true };
        var txt = new TextBox { Location = new Point(8, 28), Width = 252, Text = initial };
        var ok  = new Button  { Text = "OK",     DialogResult = DialogResult.OK,     Location = new Point(98,  54), Width = 75 };
        var can = new Button  { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(181, 54), Width = 75 };
        dlg.Controls.AddRange([lbl, txt, ok, can]);
        dlg.AcceptButton = ok;
        dlg.CancelButton = can;
        txt.SelectAll();
        _holdOpen++;   // like the other dialogs: the bar stays put while the name is typed
        try { return dlg.ShowDialog(this) == DialogResult.OK ? txt.Text.Trim() : null; }
        finally { _holdOpen = Math.Max(0, _holdOpen - 1); }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            UninstallKbHook();
            _arrangeHelp?.Close();
            CloseActivePopup();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _fadeTimer.Dispose();
            _tip.Dispose();
            _barMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
