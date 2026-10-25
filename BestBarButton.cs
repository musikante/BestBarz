using System.Runtime.InteropServices;

namespace BestBarz;

/// <summary>
/// Button showing a target's shell icon and optional label, in one of the four
/// BestBarz button styles. Width follows the text, capped by MaxButtonWidth.
/// </summary>
public sealed class BestBarButton : Button
{
    private readonly Font? _font;

    /// <summary>False for the icon-only styles.</summary>
    public bool ShowsText     { get; }
    /// <summary>The label didn't fit within MaxButtonWidth and is shown with an ellipsis.</summary>
    public bool TextTruncated { get; private set; }

    public BestBarButton(ShortcutItem sc, ButtonStyle style, BarConfig bar, int maxWidth)
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize  = bar.ShowBorders ? 1 : 0;
        FlatAppearance.BorderColor = Color.FromArgb(140, 140, 140);
        BackColor   = bar.ButtonColor;
        ForeColor   = bar.ButtonTextColor;
        Cursor      = Cursors.Hand;
        Margin      = new Padding(1);
        AutoSize    = false;
        AutoEllipsis = true;
        UseMnemonic = false;

        bool large = style is ButtonStyle.LargeIconText or ButtonStyle.LargeIcon;
        ShowsText  = style is ButtonStyle.LargeIconText or ButtonStyle.SmallIconText;

        // A group's icon is an arrow pointing to where its popup opens.
        Image = sc.IsGroup
            ? GroupArrowImage(bar.DockPosition, large ? 32 : 16, bar.ButtonTextColor)
            : (FindControlPanelFile(sc) is { } cpl ? ExtractEmbeddedIcon(cpl, large) : null)
              ?? ExtractIcon(sc.ExpandedTarget, large);

        switch (style)
        {
            case ButtonStyle.LargeIconText:
                Font              = _font = new Font("Segoe UI", 8f);
                Text              = sc.Text;
                TextImageRelation = TextImageRelation.ImageAboveText;
                ImageAlign        = ContentAlignment.TopCenter;
                TextAlign         = ContentAlignment.BottomCenter;
                Size              = new Size(FitWidth(12, 56, maxWidth), 60);
                break;

            case ButtonStyle.LargeIcon:
                Size = new Size(40, 40);
                break;

            case ButtonStyle.SmallIconText:
                Font              = _font = new Font("Segoe UI", 9f);
                Text              = sc.Text;
                TextImageRelation = TextImageRelation.ImageBeforeText;
                ImageAlign        = ContentAlignment.MiddleLeft;
                TextAlign         = ContentAlignment.MiddleLeft;
                Size              = new Size(FitWidth(SmallTextExtra, 40, maxWidth), 24);
                // WinForms' AutoEllipsis draws an empty label next to an image when the text doesn't fit,
                // so the ellipsis is applied to the text itself.
                AutoEllipsis = false;
                if (TextTruncated) Text = Ellipsize(sc.Text, Width - SmallTextExtra);
                break;

            case ButtonStyle.SmallIcon:
                Size = new Size(24, 24);
                break;
        }
    }

    private const int SmallTextExtra = 16 + 16;   // icon + side margins

    private string Ellipsize(string text, int available)
    {
        for (int n = text.Length - 1; n > 0; n--)
        {
            var candidate = text[..n].TrimEnd() + "…";
            if (TextRenderer.MeasureText(candidate, Font).Width <= available) return candidate;
        }
        return "…";
    }

    private int FitWidth(int extra, int min, int max)
    {
        int natural = TextRenderer.MeasureText(Text, Font).Width + extra;
        int width   = Math.Clamp(natural, min, Math.Max(min, max));
        TextTruncated = natural > width;
        return width;
    }

    // Drawn rather than a ▸-style glyph: those come from a fallback font and break text layout.
    private static Bitmap GroupArrowImage(DockPosition edge, int size, Color color)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        float c = size / 2f, half = size * 0.28f, depth = size * 0.24f;
        PointF[] pts = edge switch
        {
            DockPosition.Left  => [new(c - depth, c - half), new(c - depth, c + half), new(c + depth, c)],
            DockPosition.Right => [new(c + depth, c - half), new(c + depth, c + half), new(c - depth, c)],
            DockPosition.Top   => [new(c - half, c - depth), new(c + half, c - depth), new(c, c + depth)],
            _                  => [new(c - half, c + depth), new(c + half, c + depth), new(c, c - depth)],
        };
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
        return bmp;
    }

    private static readonly System.Text.RegularExpressions.Regex CplArg =
        new(@"""([^""]+?\.cpl)""|([^\s"",]+?\.cpl)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // .cpl files have no shell icon of their own (Explorer shows a generic one), but each carries its
    // applet icon. Applies to a .cpl target, or control.exe / rundll32.exe launching one.
    private static string? FindControlPanelFile(ShortcutItem sc)
    {
        string? cpl = null;
        var target = sc.ExpandedTarget;
        if (target.EndsWith(".cpl", StringComparison.OrdinalIgnoreCase))
            cpl = target;
        else if (Path.GetFileName(target) is var exe &&
                 (exe.Equals("control.exe",  StringComparison.OrdinalIgnoreCase) ||
                  exe.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)))
        {
            var m = CplArg.Match(sc.ExpandedArguments);
            if (m.Success) cpl = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        if (cpl == null) return null;
        if (!Path.IsPathRooted(cpl)) cpl = Path.Combine(Environment.SystemDirectory, cpl);
        return File.Exists(cpl) ? cpl : null;
    }

    private static Image? ExtractEmbeddedIcon(string file, bool large)
    {
        var handle = new IntPtr[1];
        uint n = large ? Win32.ExtractIconEx(file, 0, handle, null, 1)
                       : Win32.ExtractIconEx(file, 0, null, handle, 1);
        if (n == 0 || handle[0] == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(handle[0]);
            return icon.ToBitmap();
        }
        finally
        {
            Win32.DestroyIcon(handle[0]);
        }
    }

    private static Image? ExtractIcon(string path, bool large)
    {
        if (string.IsNullOrEmpty(path)) return null;

        uint flags = Win32.SHGFI_ICON | (large ? Win32.SHGFI_LARGEICON : Win32.SHGFI_SMALLICON);
        uint attrs = 0;
        // Missing targets still get the generic icon for their extension.
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            flags |= Win32.SHGFI_USEFILEATTRIBUTES;
            attrs  = Win32.FILE_ATTRIBUTE_NORMAL;
        }

        var shfi = new Win32.SHFILEINFO();
        if (Win32.SHGetFileInfo(path, attrs, ref shfi, (uint)Marshal.SizeOf<Win32.SHFILEINFO>(), flags) == IntPtr.Zero
            || shfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using var icon = Icon.FromHandle(shfi.hIcon);
            return icon.ToBitmap();
        }
        finally
        {
            Win32.DestroyIcon(shfi.hIcon);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Image?.Dispose();
            _font?.Dispose();
        }
        base.Dispose(disposing);
    }
}
