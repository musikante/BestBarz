namespace BestBarz;

/// <summary>Small dialog to edit or create a shortcut entry.</summary>
public sealed class ShortcutEditorForm : Form
{
    private readonly ShortcutItem _sc;

    private readonly TextBox _txtText;
    private readonly TextBox _txtTarget;
    private readonly TextBox _txtArgs;
    private readonly TextBox _txtWorkDir;

    public ShortcutEditorForm(ShortcutItem sc)
    {
        _sc = sc;

        Text            = "Edit Shortcut";
        Size            = new Size(420, 230);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;

        int lx = 12, fx = 100, w = 270, dy = 32, y = 12;

        Controls.Add(Label(lx, y, "Label:"));
        Controls.Add(_txtText   = TB(fx, y, w, sc.Text));   y += dy;

        Controls.Add(Label(lx, y, "Target:"));
        var btnBrowse = new Button { Text = "…", Location = new Point(fx + w + 4, y), Width = 28, Height = 22 };
        btnBrowse.Click += BrowseTarget;
        Controls.Add(_txtTarget = TB(fx, y, w, sc.Target));
        Controls.Add(btnBrowse);                              y += dy;

        Controls.Add(Label(lx, y, "Arguments:"));
        Controls.Add(_txtArgs   = TB(fx, y, w, sc.Arguments)); y += dy;

        Controls.Add(Label(lx, y, "Work Dir:"));
        Controls.Add(_txtWorkDir = TB(fx, y, w, sc.WorkingDirectory)); y += dy + 6;

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

    private static Label  Label(int x, int y, string t) =>
        new() { Text = t, Location = new Point(x, y + 3), AutoSize = true };

    private static TextBox TB(int x, int y, int w, string val) =>
        new() { Text = val, Location = new Point(x, y), Width = w, Height = 22 };
}
