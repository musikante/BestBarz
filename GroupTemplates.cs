namespace BestBarz;

/// <summary>
/// Ready-made groups offered by the bar menu's "Add Group" submenu. Paths use environment variables
/// so they work on any PC; once created, a template group is an ordinary group the user can edit.
/// </summary>
internal sealed record GroupTemplate(string MenuText, string Name, Func<List<ShortcutItem>> Items)
{
    private const string Sys = @"%SystemRoot%\System32\";

    private static ShortcutItem Item(string text, string target, string args = "") =>
        new() { Text = text, Target = target, Arguments = args };

    private static ShortcutItem Separator() => new();

    public static readonly GroupTemplate[] All =
    [
        new("System utilities", "System", () =>
        [
            Item("Control Panel",         Sys + "control.exe"),
            Item("Programs and Features", Sys + "control.exe", "appwiz.cpl"),
            Item("System Properties",     Sys + "SystemPropertiesAdvanced.exe"),
            Item("Environment Variables", Sys + "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables"),
            Item("Disk Cleanup",          Sys + "cleanmgr.exe"),
            Separator(),
            Item("Temp",                  "%TEMP%"),
            Item("Startup",               "shell:startup"),
            Item("AppData",               "%APPDATA%"),
        ]),
        new("Admin utilities", "Admin", () =>
        [
            Item("Computer Management",   Sys + "compmgmt.msc"),
            Item("Device Manager",        Sys + "devmgmt.msc"),
            Item("Disk Management",       Sys + "diskmgmt.msc"),
            Item("Event Viewer",          Sys + "eventvwr.msc"),
            Item("Registry Editor",       @"%SystemRoot%\regedit.exe"),
            Separator(),
            Item("Task Manager",          Sys + "Taskmgr.exe"),
            Item("Resource Monitor",      Sys + "resmon.exe"),
            Item("System Information",    Sys + "msinfo32.exe"),
        ]),
        new("Control Panel applets", "Applets", InstalledApplets),
    ];

    private static readonly Dictionary<string, string> AppletNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["appwiz"]   = "Programs and Features", ["bthprops"] = "Bluetooth",
        ["desk"]     = "Display",               ["Firewall"] = "Windows Firewall",
        ["hdwwiz"]   = "Add Hardware",          ["inetcpl"]  = "Internet Options",
        ["intl"]     = "Region",                ["irprops"]  = "Infrared",
        ["joy"]      = "Game Controllers",      ["main"]     = "Mouse",
        ["mmsys"]    = "Sound",                 ["ncpa"]     = "Network Connections",
        ["powercfg"] = "Power Options",         ["sysdm"]    = "System Properties",
        ["TabletPC"] = "Tablet PC Settings",    ["telephon"] = "Phone and Modem",
        ["timedate"] = "Date and Time",         ["wscui"]    = "Security and Maintenance",
    };

    // Every .cpl installed on this PC, read when the group is created — a quarry to copy buttons from.
    // Known applets get a short name; others (drivers, third-party) their file description plus the
    // file name, since descriptions repeat (Autodesk ships two "Autodesk Hardcopy component").
    private static List<ShortcutItem> InstalledApplets() =>
        [.. Directory.EnumerateFiles(Environment.SystemDirectory, "*.cpl")
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var text = AppletNames.TryGetValue(name, out var known) ? known
                         : System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } desc
                             ? $"{desc} ({name})"
                         : name;
                return Item(text, Sys + "control.exe", Path.GetFileName(path));
            })
            .OrderBy(sc => sc.Text, StringComparer.CurrentCultureIgnoreCase)];
}
