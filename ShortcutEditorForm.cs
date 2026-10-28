namespace BestBarz;

/// <summary>Small dialog to edit or create a shortcut entry.</summary>
public sealed class ShortcutEditorForm : Form
{
    private readonly ShortcutItem _sc;

    private readonly TextBox _txtText;
    private readonly TextBox _txtTarget;
    private readonly TextBox _txtArgs;
    private readonly TextBox _txtWorkDir;

    // Variables tried in longest-expanded-value-first order so a more specific variable
    // (e.g. %ProgramFiles(x86)%) wins over a shorter one (%ProgramFiles%) on the same path.
    private static readonly (string Var, string Expanded)[] _envVars =
        new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramData",
                "LocalAppData", "AppData", "UserProfile", "SystemRoot", "Public" }
        .Select(v => (Var: $"%{v}%", Expanded: Environment.GetEnvironmentVariable(v) ?? ""))
        .Where(t => t.Expanded.Length > 0)
        .OrderByDescending(t => t.Expanded.Length)
        .ToArray();

    public ShortcutEditorForm(ShortcutItem sc)
    {
        _sc = sc;

        Text            = "Edit Shortcut";
        Size            = new Size(420, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;

        int lx = 12, fx = 100, w = 270, dy = 32, y = 12;
        const int BtnW = 28, Gap = 4;
        // Rows that have a [%] button: textbox starts BtnW+Gap px to the right and is that much narrower.
        int wPct = w - BtnW - Gap;

        Controls.Add(Label(lx, y, "Label:"));
        Controls.Add(_txtText = TB(fx, y, w, sc.Text)); y += dy;

        Controls.Add(Label(lx, y, "Target:"));
        var btnPctTarget = PctButton(fx, y);
        var btnBrowse    = new Button { Text = "…", Location = new Point(fx + BtnW + Gap + wPct + Gap, y), Width = BtnW, Height = 22 };
        btnBrowse.Click += BrowseTarget;
        Controls.Add(_txtTarget = TB(fx + BtnW + Gap, y, wPct, sc.Target));
        Controls.Add(btnPctTarget);
        Controls.Add(btnBrowse); y += dy;

        Controls.Add(Label(lx, y, "Arguments:"));
        Controls.Add(_txtArgs = TB(fx, y, w, sc.Arguments)); y += dy;

        Controls.Add(Label(lx, y, "Work Dir:"));
        var btnPctWorkDir = PctButton(fx, y);
        Controls.Add(_txtWorkDir = TB(fx + BtnW + Gap, y, wPct, sc.WorkingDirectory));
        Controls.Add(btnPctWorkDir); y += dy + 6;

        btnPctTarget.Click  += (_, _) => _txtTarget.Text  = ContractPath(_txtTarget.Text);
        btnPctWorkDir.Click += (_, _) => _txtWorkDir.Text = ContractPath(_txtWorkDir.Text);
        _txtTarget.TextChanged  += (_, _) => btnPctTarget.Enabled  = CanContract(_txtTarget.Text);
        _txtWorkDir.TextChanged += (_, _) => btnPctWorkDir.Enabled = CanContract(_txtWorkDir.Text);
        btnPctTarget.Enabled  = CanContract(sc.Target);
        btnPctWorkDir.Enabled = CanContract(sc.WorkingDirectory);

        var ok     = new Button { Text = "OK",     Size = new Size(75, 26), Location = new Point(fx,      y), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Size = new Size(75, 26), Location = new Point(fx + 80, y), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Apply();
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Apply()
    {
        _sc.Text             = _txtText.Text;
        _sc.Target           = ShortcutItem.CleanPath(_txtTarget.Text);
        _sc.Arguments        = _txtArgs.Text;
        _sc.WorkingDirectory = ShortcutItem.CleanPath(_txtWorkDir.Text);
    }

    private void BrowseTarget(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog { Title = "Select target", Filter = "All files|*.*" };
        if (dlg.ShowDialog() == DialogResult.OK)
        {
            _txtTarget.Text = dlg.FileName;
            if (string.IsNullOrEmpty(_txtText.Text))
                _txtText.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    // Returns true when path starts with one of the known variable expansions.
    // Applied to the raw textbox text (not expanded), so a path already using %VAR% stays false.
    private static bool CanContract(string path)
    {
        path = ShortcutItem.CleanPath(path);
        return path.Length > 0 &&
               _envVars.Any(t => path.StartsWith(t.Expanded, StringComparison.OrdinalIgnoreCase));
    }

    // Replaces the longest matching variable prefix.
    private static string ContractPath(string path)
    {
        path = ShortcutItem.CleanPath(path);
        foreach (var (var, expanded) in _envVars)
            if (path.StartsWith(expanded, StringComparison.OrdinalIgnoreCase))
                return var + path[expanded.Length..];
        return path;
    }

    private static Button PctButton(int x, int y) =>
        new() { Text = "%", Location = new Point(x, y), Width = 28, Height = 22, Enabled = false };

    private static Label Label(int x, int y, string t) =>
        new() { Text = t, Location = new Point(x, y + 3), AutoSize = true };

    private static TextBox TB(int x, int y, int w, string val) =>
        new() { Text = val, Location = new Point(x, y), Width = w, Height = 22 };
}
