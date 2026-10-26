using System.Threading;

namespace BestBarz;

static class Program
{
    public static readonly string DefaultConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

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
