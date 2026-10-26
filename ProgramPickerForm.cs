using System.Text.RegularExpressions;

namespace BestBarz;

/// <summary>
/// Picks programs from the Start menu shortcuts (all users + current user) to add as buttons.
/// Searchable, with checkboxes; uninstallers and documentation links are left out.
/// </summary>
internal sealed class ProgramPickerForm : Form
{
    private sealed record Entry(string Name, string Folder, string Target);

    private static readonly Regex Noise = new(
        @"uninstall|desinstal|readme|l[eé]ame|\bhelp\b|ayuda|website|sitio web|manual|licen[cs]|changelog|release notes|documentaci[oó]n|documentation",
        RegexOptions.IgnoreCase);

    private readonly List<Entry> _all;
    private readonly HashSet<Entry> _checked = [];
    private readonly TextBox  _search;
    private readonly ListView _list;
    private readonly Button   _add;
    private bool _filling;

    public List<ShortcutItem> Selected { get; } = [];

    public ProgramPickerForm()
    {
        Text            = "Add Programs";
        ClientSize      = new Size(460, 520);
        MinimumSize     = new Size(360, 300);
        StartPosition   = FormStartPosition.Manual;
        ShowInTaskbar   = false;
        MinimizeBox     = false;
        MaximizeBox     = false;

        _all = LoadPrograms();

        _search = new TextBox { Location = new Point(8, 8), Width = 444, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                                PlaceholderText = "Search…" };
        _search.TextChanged += (_, _) => Fill();

        _list = new ListView
        {
            Location = new Point(8, 36), Size = new Size(444, 438),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("Program", 250);
        _list.Columns.Add("Start menu folder", 170);
        _list.ItemChecked += (_, e) =>
        {
            if (_filling || e.Item.Tag is not Entry p) return;
            if (e.Item.Checked) _checked.Add(p); else _checked.Remove(p);
            UpdateAddButton();
        };

        _add = new Button { Text = "Add", Size = new Size(110, 28), Location = new Point(256, 484), DialogResult = DialogResult.OK,
                            Anchor = AnchorStyles.Bottom | AnchorStyles.Right, Enabled = false };
        _add.Click += (_, _) => Selected.AddRange(_all.Where(_checked.Contains)
                                                      .Select(p => new ShortcutItem { Text = p.Name, Target = p.Target }));
        var cancel = new Button { Text = "Cancel", Size = new Size(80, 28), Location = new Point(372, 484), DialogResult = DialogResult.Cancel,
                                  Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

        Controls.AddRange([_search, _list, _add, cancel]);
        AcceptButton = _add;
        CancelButton = cancel;
        Fill();
    }

    // Keeps checks across searches: _checked is the truth, the list just shows the matches.
    private void Fill()
    {
        _filling = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in _all.Where(p => words.All(w => p.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                                                         p.Folder.Contains(w, StringComparison.CurrentCultureIgnoreCase))))
            _list.Items.Add(new ListViewItem([p.Name, p.Folder]) { Tag = p, Checked = _checked.Contains(p) });
        _list.EndUpdate();
        _filling = false;
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        _add.Enabled = _checked.Count > 0;
        _add.Text    = _checked.Count > 0 ? $"Add {_checked.Count}" : "Add";
    }

    // Start menu shortcuts of all users and of the current user, stored with %ProgramData% / %AppData%
    // so the buttons work on another PC. The first of each name wins; the list is sorted by name.
    private static List<Entry> LoadPrograms()
    {
        (string Var, string Dir)[] roots =
        [
            ("%ProgramData%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("%AppData%",     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
        ];
        const string menu = @"Microsoft\Windows\Start Menu\Programs";
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var list = new List<Entry>();
        foreach (var (envVar, dir) in roots)
        {
            var programs = Path.Combine(dir, menu);
            if (!Directory.Exists(programs)) continue;
            foreach (var lnk in Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (Noise.IsMatch(name) || !seen.Add(name)) continue;
                var folder = Path.GetRelativePath(programs, Path.GetDirectoryName(lnk)!);
                list.Add(new Entry(name, folder == "." ? "" : folder,
                                     Path.Combine(envVar, menu, Path.GetRelativePath(programs, lnk))));
            }
        }
        return [.. list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
