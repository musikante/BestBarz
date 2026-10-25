namespace BestBarz;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum DockPosition { Left = 0, Top = 1, Right = 2, Bottom = 3 }

public enum ButtonStyle { LargeIconText, LargeIcon, SmallIconText, SmallIcon }

// ── Shortcut / group data ─────────────────────────────────────────────────────

public class ShortcutItem
{
    public string Text              { get; set; } = "";
    public string Target            { get; set; } = "";
    public string Arguments         { get; set; } = "";
    public string WorkingDirectory  { get; set; } = "";
    public string Hotkey            { get; set; } = "";
    public bool   LaunchShellShortcut { get; set; } = false;
    public string GroupFile         { get; set; } = "";   // non-empty → group button (path to .xml file)

    public bool IsSeparator => string.IsNullOrEmpty(Target) && string.IsNullOrEmpty(GroupFile);
    public bool IsGroup     => !string.IsNullOrEmpty(GroupFile);

    // Explorer's "Copy as path" wraps paths in quotes, which breaks File.Exists and icon lookup.
    public static string CleanPath(string path) => path.Trim().Trim('"').Trim();

    public ShortcutItem Clone() => (ShortcutItem)MemberwiseClone();

    // Environment variables (%TEMP%, %USERPROFILE%…) are stored as typed and expanded on use, so a
    // button keeps working for another user or PC. Only %NAME% of an existing variable is replaced.
    public string ExpandedTarget           => Environment.ExpandEnvironmentVariables(Target);
    public string ExpandedArguments        => Environment.ExpandEnvironmentVariables(Arguments);
    public string ExpandedWorkingDirectory => Environment.ExpandEnvironmentVariables(WorkingDirectory);
}

// ── Per-bar config ────────────────────────────────────────────────────────────

public class BarConfig
{
    public string        Name              { get; set; } = "";
    public string        FolderPath        { get; set; } = "";
    public DockPosition  DockPosition      { get; set; } = DockPosition.Left;
    public Color         ToolbarColor      { get; set; } = SystemColors.Control;
    public Color         ButtonColor       { get; set; } = SystemColors.Control;
    public Color         ButtonTextColor   { get; set; } = SystemColors.ControlText;
    public Color         SeparatorColor    { get; set; } = SystemColors.Control;
    public Color         SeparatorTextColor{ get; set; } = SystemColors.ControlText;
    public string        SeparatorTextAlign{ get; set; } = "Center";
    public bool          ShowBorders       { get; set; } = true;
    public int           DisplayTarget     { get; set; } = 0;
    public bool          Enabled           { get; set; } = true;
    public List<ShortcutItem> Shortcuts    { get; set; } = [];
    public string        FilePath          { get; set; } = "";   // non-empty for group bars; main bars use FolderPath/_bar.xml

    // A separator coloured like the bar is invisible — the default, both Control — so it then takes
    // a contrasting shade of the bar colour. A distinct separator colour is kept as chosen.
    public Color VisibleSeparatorColor =>
        SeparatorColor.ToArgb() != ToolbarColor.ToArgb() ? SeparatorColor
        : ToolbarColor.GetBrightness() < 0.5f            ? ControlPaint.Light(ToolbarColor)
        :                                                   ControlPaint.Dark(ToolbarColor);
}

// ── Global config ─────────────────────────────────────────────────────────────

public class GlobalConfig
{
    public bool   ShowTrayIcon       { get; set; } = true;
    public bool   ShowToolTips       { get; set; } = true;
    public int    DataInterval       { get; set; } = 100;
    public bool   FolderParsingMenu  { get; set; } = false;
    public bool   OnlyOnDesktop      { get; set; } = false;
    public bool   ShowArrangeHelp    { get; set; } = true;

    public HashSet<DockPosition> StretchBars      { get; set; } = [DockPosition.Left, DockPosition.Top, DockPosition.Right];
    public HashSet<DockPosition> CenterBars        { get; set; } = [];
    public HashSet<DockPosition> ReverseAlignBars  { get; set; } = [];
    public HashSet<DockPosition> ScrollableBars    { get; set; } = [DockPosition.Left, DockPosition.Right];
    public HashSet<DockPosition> AlwaysVisible     { get; set; } = [];
    public HashSet<DockPosition> FullScreenBars    { get; set; } = [];   // use Screen.Bounds (over the taskbar), not WorkingArea
    public bool ProtectTopLeftCorner  { get; set; } = true;
    public bool ProtectTopRightCorner { get; set; } = true;

    public ButtonStyle[] ButtonLayout    { get; set; } = [ButtonStyle.LargeIconText, ButtonStyle.LargeIcon, ButtonStyle.LargeIconText, ButtonStyle.LargeIcon];
    public int[]         ShowDelay       { get; set; } = [0, 0, 0, 0];
    public int[]         HideDelay       { get; set; } = [0, 0, 0, 0];
    public int[]         MaxButtonWidth  { get; set; } = [180, 120, 180, 120];
    public int[]         PointerDistance { get; set; } = [10, 10, 10, 0];

    public bool   ToolbarFading          { get; set; } = true;
    public bool   DialogFading           { get; set; } = true;
    public int    ToolbarFadeInTime      { get; set; } = 10;
    public double ToolbarFadeInInterval  { get; set; } = 0.05;
    public int    ToolbarFadeOutTime     { get; set; } = 10;
    public double ToolbarFadeOutInterval { get; set; } = 0.025;
    public double MaxOpacity             { get; set; } = 1.0;
}

// ── Clipboard ─────────────────────────────────────────────────────────────────

internal static class BarClipboard
{
    public static ShortcutItem? Item   { get; private set; }
    public static bool          IsCopy { get; private set; }   // Copy: paste a clone, any number of times

    public static void Cut(ShortcutItem sc)  { Item = sc; IsCopy = false; }
    public static void Copy(ShortcutItem sc) { Item = sc; IsCopy = true; }
    public static void Clear()               { Item = null; IsCopy = false; }
}
