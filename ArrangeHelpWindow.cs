namespace BestBarz;

/// <summary>
/// Tooltip-like, non-activating panel listing the Arrange mode keys. Placed beside the bar or popup
/// (toward the screen centre) next to the item being moved; closed when Arrange mode ends.
/// </summary>
internal sealed class ArrangeHelpWindow : Form
{
    private const int Gap = 6;
    private readonly Font _regular = new("Segoe UI", 9f);
    private readonly Font _bold;

    public ArrangeHelpWindow(bool withGroups)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        StartPosition   = FormStartPosition.Manual;
        TopMost         = true;
        BackColor       = SystemColors.Info;
        ForeColor       = SystemColors.InfoText;
        Font            = _regular;
        AutoSize        = true;
        AutoSizeMode    = AutoSizeMode.GrowAndShrink;
        Padding         = new Padding(1);   // room for the border painted in OnPaint
        _bold           = new Font(_regular, FontStyle.Bold);

        var table = new TableLayoutPanel
        {
            AutoSize    = true,
            Dock        = DockStyle.Fill,
            ColumnCount = 2,
            Padding     = new Padding(8, 6, 10, 8),
            Margin      = Padding.Empty,
            BackColor   = SystemColors.Info,
        };
        table.Controls.Add(Cell("Arrange mode", _bold), 0, 0);
        table.SetColumnSpan(table.GetControlFromPosition(0, 0)!, 2);

        void Row(string keys, string action)
        {
            table.Controls.Add(Cell(keys, _bold));
            table.Controls.Add(Cell(action, _regular));
        }

        Row("← ↑  PgUp",   "Move one place back");
        Row("→ ↓  PgDn",   "Move one place forward");
        Row("Home / End",  "Move to the start / end");
        if (withGroups)
            Row("Next to a group", "Turns orange: Enter drops the item inside,\nthe same arrow again skips over it");
        Row("Enter",       withGroups ? "Save and finish (drops into the orange group)" : "Save and finish");
        Row("Esc",         "Cancel, restoring the previous order");
        Row("Mouse",       withGroups ? "Drag to reorder; drop on a group to move it inside and save"
                                      : "Drag to reorder");
        Controls.Add(table);
    }

    private static Label Cell(string text, Font font) =>
        new() { Text = text, Font = font, AutoSize = true, UseMnemonic = false, Margin = new Padding(2, 2, 10, 2) };

    /// <summary>Positions the window outside <paramref name="owner"/>, aligned with <paramref name="anchor"/>.</summary>
    public void PlaceBeside(Rectangle anchor, Rectangle owner, DockPosition edge)
    {
        var area = Screen.FromRectangle(owner).Bounds;
        var (x, y) = edge switch
        {
            DockPosition.Left  => (owner.Right + Gap,         anchor.Top),
            DockPosition.Right => (owner.Left - Width - Gap,  anchor.Top),
            DockPosition.Top   => (anchor.Left,               owner.Bottom + Gap),
            _                  => (anchor.Left,               owner.Top - Height - Gap),
        };
        Location = new Point(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right  - Width)),
                             Math.Clamp(y, area.Top,  Math.Max(area.Top,  area.Bottom - Height)));
        if (!Visible) Show();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        ControlPaint.DrawBorder(e.Graphics, ClientRectangle, SystemColors.ControlDark, ButtonBorderStyle.Solid);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _bold.Dispose(); _regular.Dispose(); }
    }
}
