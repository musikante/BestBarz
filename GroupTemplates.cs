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
    ];
}
