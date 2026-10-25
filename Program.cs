using System.Threading;

namespace BestBarz;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Single-instance guard
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

        Application.Run(new BestBarzApp());
    }
}
