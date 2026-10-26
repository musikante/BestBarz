using System.Threading;

namespace BestBarz;

static class Program
{
    public static readonly string DefaultConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

    // The csproj's RepositoryUrl, embedded as assembly metadata.
    public static readonly string? RepositoryUrl =
        typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;

    // The user guide shipped next to the exe; the README on GitHub if it's missing.
    public static void OpenHelp()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "BestBarz.html");
        if (File.Exists(file))            OpenUrl(file);
        else if (RepositoryUrl != null)   OpenUrl(RepositoryUrl + "#readme");
    }

    public static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    [STAThread]
    static void Main(string[] args)
    {
        // Single-instance guard: one set of bars, whatever config folder each launch asks for.
        using var mutex = new Mutex(true, "BestBarz_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("BestBarz is already running.",
                "BestBarz", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        if (ConfigDirFromArgs(args) is not { } configDir) return;
        try { Directory.CreateDirectory(configDir); }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot use the config folder:\n{configDir}\n\n{ex.Message}",
                "BestBarz", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new BestBarzApp(configDir));
    }

    // --config "folder" or --config=folder; environment variables allowed, relative paths are taken
    // from the exe's folder. Without it, the Config folder next to the exe. Null: bad usage, already reported.
    private static string? ConfigDirFromArgs(string[] args)
    {
        string? value = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
                value = args[i]["--config=".Length..];
            else if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
                value = i + 1 < args.Length ? args[++i] : "";
        }
        if (value == null) return DefaultConfigDir;
        if (string.IsNullOrWhiteSpace(value))
        {
            MessageBox.Show("Usage: BestBarz.exe --config \"folder\"", "BestBarz",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                                             Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))));
    }
}
