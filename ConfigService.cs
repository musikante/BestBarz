using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BestBarz;

/// <summary>
/// Reads and writes BestBarz configuration.
/// Config/
///   _global.xml                — global settings (XML attributes)
///   Left/ Top/ Right/ Bottom/  — one folder per edge; the folder name is the edge, other folders are ignored
///     _bar.xml                 — bar settings (XML attributes) + ordered item list
///     {GroupName}.xml          — group button's item list (flat file, no subfolder)
/// </summary>
public class ConfigService
{
    private static readonly DockPosition[] AllEdges =
        [DockPosition.Left, DockPosition.Top, DockPosition.Right, DockPosition.Bottom];


    private readonly string _dir;

    public string ConfigDir => _dir;

    // The config watcher must not treat the app's own saves as external edits: reloading would
    // recreate every bar window (ending Arrange mode, closing popups) 1.5 s after each change.
    private long _lastOwnWrite = long.MinValue / 2;
    public  bool IsOwnWriteRecent => Environment.TickCount64 - Interlocked.Read(ref _lastOwnWrite) < 2000;
    public  void NoteOwnWrite()   => Interlocked.Exchange(ref _lastOwnWrite, Environment.TickCount64);

    public ConfigService(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    private string GlobalPath => Path.Combine(_dir, "_global.xml");

    public bool NeedsMigration =>
        File.Exists(Path.Combine(_dir, "Coolbarz.cfg")) && !File.Exists(GlobalPath);

    // ── Global ────────────────────────────────────────────────────────────────

    public GlobalConfig LoadGlobal()
    {
        var c = new GlobalConfig();
        if (!File.Exists(GlobalPath)) return c;
        try
        {
            var root = XDocument.Load(GlobalPath).Root!;
            c.ShowTrayIcon           = Attr(root, "showTrayIcon",           c.ShowTrayIcon);
            c.ShowToolTips           = Attr(root, "showToolTips",           c.ShowToolTips);
            c.DataInterval           = Attr(root, "dataInterval",           c.DataInterval);
            c.FolderParsingMenu      = Attr(root, "folderParsingMenu",      c.FolderParsingMenu);
            c.OnlyOnDesktop          = Attr(root, "onlyOnDesktop",          c.OnlyOnDesktop);
            c.ShowArrangeHelp        = Attr(root, "showArrangeHelp",        c.ShowArrangeHelp);
            c.StretchBars            = AttrEdgeSet(root, "stretchBars",            c.StretchBars);
            c.CenterBars             = AttrEdgeSet(root, "centerBars",             c.CenterBars);
            c.ReverseAlignBars       = AttrEdgeSet(root, "reverseAlignBars",       c.ReverseAlignBars);
            c.ScrollableBars         = AttrEdgeSet(root, "scrollableBars",         c.ScrollableBars);
            c.AlwaysVisible          = AttrEdgeSet(root, "alwaysVisible",          c.AlwaysVisible);
            c.FullScreenBars         = AttrEdgeSet(root, "fullScreenBars",         c.FullScreenBars);

            c.ProtectTopLeftCorner   = Attr(root, "protectTopLeftCorner",  c.ProtectTopLeftCorner);
            c.ProtectTopRightCorner  = Attr(root, "protectTopRightCorner", c.ProtectTopRightCorner);
            c.ButtonLayout           = AttrEnumArr<ButtonStyle>(root, "buttonLayout",   c.ButtonLayout);
            c.ShowDelay              = AttrIntArr(root, "showDelay",       c.ShowDelay);
            c.HideDelay              = AttrIntArr(root, "hideDelay",       c.HideDelay);
            c.MaxButtonWidth         = AttrIntArr(root, "maxButtonWidth",  c.MaxButtonWidth);
            c.PointerDistance        = AttrIntArr(root, "pointerDistance", c.PointerDistance);
            c.ToolbarFading          = Attr(root, "toolbarFading",          c.ToolbarFading);
            c.DialogFading           = Attr(root, "dialogFading",           c.DialogFading);
            c.ToolbarFadeInTime      = Attr(root, "toolbarFadeInTime",      c.ToolbarFadeInTime);
            c.ToolbarFadeInInterval  = AttrD(root, "toolbarFadeInInterval",  c.ToolbarFadeInInterval);
            c.ToolbarFadeOutTime     = Attr(root, "toolbarFadeOutTime",     c.ToolbarFadeOutTime);
            c.ToolbarFadeOutInterval = AttrD(root, "toolbarFadeOutInterval", c.ToolbarFadeOutInterval);
            c.MaxOpacity             = AttrD(root, "maxOpacity",            c.MaxOpacity);
        }
        catch { /* return partial/defaults on corrupt file */ }
        return c;
    }

    public void SaveGlobal(GlobalConfig c)
    {
        NoteOwnWrite();
        var root = new XElement("global",
            WA("showTrayIcon",           c.ShowTrayIcon),
            WA("showToolTips",           c.ShowToolTips),
            WA("dataInterval",           c.DataInterval),
            WA("folderParsingMenu",      c.FolderParsingMenu),
            WA("onlyOnDesktop",          c.OnlyOnDesktop),
            WA("showArrangeHelp",        c.ShowArrangeHelp),
            WAEdgeSet("stretchBars",            c.StretchBars),
            WAEdgeSet("centerBars",             c.CenterBars),
            WAEdgeSet("reverseAlignBars",       c.ReverseAlignBars),
            WAEdgeSet("scrollableBars",         c.ScrollableBars),
            WAEdgeSet("alwaysVisible",          c.AlwaysVisible),
            WAEdgeSet("fullScreenBars",         c.FullScreenBars),

            WA("protectTopLeftCorner",   c.ProtectTopLeftCorner),
            WA("protectTopRightCorner",  c.ProtectTopRightCorner),
            WAArr("buttonLayout",   c.ButtonLayout.Select(b => b.ToString())),
            WAArr("showDelay",      c.ShowDelay.Select(v => v.ToString())),
            WAArr("hideDelay",      c.HideDelay.Select(v => v.ToString())),
            WAArr("maxButtonWidth", c.MaxButtonWidth.Select(v => v.ToString())),
            WAArr("pointerDistance",c.PointerDistance.Select(v => v.ToString())),
            WA("toolbarFading",          c.ToolbarFading),
            WA("dialogFading",           c.DialogFading),
            WA("toolbarFadeInTime",      c.ToolbarFadeInTime),
            WA("toolbarFadeInInterval",  c.ToolbarFadeInInterval),
            WA("toolbarFadeOutTime",     c.ToolbarFadeOutTime),
            WA("toolbarFadeOutInterval", c.ToolbarFadeOutInterval),
            WA("maxOpacity",             c.MaxOpacity)
        );
        WriteAtomic(GlobalPath, new XDocument(new XDeclaration("1.0", "utf-8", null), root));
    }

    // ── Bar discovery ─────────────────────────────────────────────────────────

    // One bar per edge, and the folder name is the edge. Any other folder (e.g. "Top-Work") is
    // ignored, so swapping a bar's whole content is just renaming folders.
    public List<BarConfig> LoadBars()
    {
        var list = new List<BarConfig>();
        foreach (var edge in AllEdges)
        {
            if (LoadBarFromFolder(Path.Combine(_dir, edge.ToString())) is not BarConfig bar) continue;
            bar.DockPosition = edge;
            bar.Name         = edge.ToString();
            list.Add(bar);
        }
        return list;
    }

    // Group .xml files that no bar or group references: removed groups, or a group cut and never pasted.
    public List<string> FindOrphanGroupFiles()
    {
        var orphans = new List<string>();
        foreach (var bar in LoadBars())
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (BarClipboard.Item is { IsGroup: true } cut) used.Add(Path.GetFullPath(cut.GroupFile));
            CollectGroupFiles(bar.Shortcuts, used);
            orphans.AddRange(Directory.EnumerateFiles(bar.FolderPath, "*.xml")
                .Where(f => !Path.GetFileName(f).Equals("_bar.xml", StringComparison.OrdinalIgnoreCase)
                         && !used.Contains(Path.GetFullPath(f))));
        }
        return orphans;
    }

    private void CollectGroupFiles(List<ShortcutItem> items, HashSet<string> used)
    {
        foreach (var sc in items.Where(s => s.IsGroup))
        {
            if (!used.Add(Path.GetFullPath(sc.GroupFile))) continue;
            if (LoadGroupBar(sc.GroupFile) is BarConfig group) CollectGroupFiles(group.Shortcuts, used);
        }
    }

    public BarConfig? LoadBarFromFolder(string folderPath)
    {
        var barFile = Path.Combine(folderPath, "_bar.xml");
        if (!File.Exists(barFile)) return null;
        try
        {
            var root = XDocument.Load(barFile).Root!;
            var bar = new BarConfig
            {
                Name               = Path.GetFileName(folderPath),
                FolderPath         = folderPath,
                ToolbarColor       = AttrColor(root, "toolbarColor",      SystemColors.Control),
                ButtonColor        = AttrColor(root, "buttonColor",       SystemColors.Control),
                ButtonTextColor    = AttrColor(root, "buttonTextColor",   SystemColors.ControlText),
                SeparatorColor     = AttrColor(root, "separatorColor",    SystemColors.Control),
                SeparatorTextColor = AttrColor(root, "separatorTextColor",SystemColors.ControlText),
                SeparatorTextAlign = root.Attribute("separatorTextAlign")?.Value ?? "Center",
                ShowBorders        = Attr(root, "showBorders",   true),
                DisplayTarget      = Attr(root, "displayTarget", 0),
                Enabled            = Attr(root, "enabled",       true),
            };
            bar.Shortcuts = ReadItems(root, folderPath);
            return bar;
        }
        catch { return null; }
    }

    private static List<ShortcutItem> ReadItems(XElement root, string folderPath)
    {
        var list = new List<ShortcutItem>();
        var itemsEl = root.Element("items");
        if (itemsEl == null) return list;

        foreach (var el in itemsEl.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "button":
                    list.Add(new ShortcutItem
                    {
                        Text              = el.Attribute("text")?.Value    ?? "",
                        Target            = ShortcutItem.CleanPath(el.Attribute("target")?.Value  ?? ""),
                        Arguments         = el.Attribute("args")?.Value    ?? "",
                        WorkingDirectory  = ShortcutItem.CleanPath(el.Attribute("workdir")?.Value ?? ""),
                        Hotkey            = el.Attribute("hotkey")?.Value  ?? "",
                        LaunchShellShortcut = bool.TryParse(el.Attribute("shell")?.Value, out var b) && b,
                    });
                    break;

                case "separator":
                    list.Add(new ShortcutItem { Text = el.Attribute("text")?.Value ?? "" });
                    break;

                case "group":
                    var fileAttr = el.Attribute("file")?.Value ?? "";
                    if (string.IsNullOrEmpty(fileAttr))
                    {
                        var sub = el.Attribute("folder")?.Value ?? "";     // legacy format
                        if (!string.IsNullOrEmpty(sub)) fileAttr = sub + ".xml";
                    }
                    if (!string.IsNullOrEmpty(fileAttr))
                        list.Add(new ShortcutItem
                        {
                            Text      = el.Attribute("text")?.Value ?? Path.GetFileNameWithoutExtension(fileAttr),
                            GroupFile = Path.Combine(folderPath, fileAttr),
                        });
                    break;
            }
        }
        return list;
    }

    // A group pasted from another bar still points at that bar's folder, but the reference is saved
    // as a bare file name resolved against this bar's folder — so the file has to move with it.
    private void RelocateGroupFile(ShortcutItem sc, string barDir)
    {
        var srcDir = Path.GetDirectoryName(sc.GroupFile);
        if (string.IsNullOrEmpty(srcDir) ||
            string.Equals(Path.GetFullPath(srcDir), Path.GetFullPath(barDir), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sc.GroupFile))
            return;

        var dest = UniqueGroupPath(barDir, Path.GetFileNameWithoutExtension(sc.GroupFile));
        NoteOwnWrite();
        try { File.Move(sc.GroupFile, dest); }
        catch { return; }
        sc.GroupFile = dest;
    }

    // "_bar" is taken by the bar's own file, so a group with that name also gets a numbered file.
    public static string UniqueGroupPath(string dir, string name)
    {
        var path = Path.Combine(dir, name + ".xml");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{name} {n}.xml");
        return path;
    }

    // Cut moves the item, so the clipboard empties. Copy pastes a fresh clone each time; a copied
    // group gets its own duplicate .xml in destDir, so the two buttons never share (or steal) a file.
    public ShortcutItem? TakeClipboardItem(string destDir)
    {
        if (BarClipboard.Item is not ShortcutItem item) return null;
        if (!BarClipboard.IsCopy) { BarClipboard.Clear(); return item; }

        var clone = item.Clone();
        if (clone.IsGroup && File.Exists(item.GroupFile))
        {
            clone.GroupFile = UniqueGroupPath(destDir, Path.GetFileNameWithoutExtension(item.GroupFile));
            NoteOwnWrite();
            File.Copy(item.GroupFile, clone.GroupFile);
        }
        return clone;
    }

    public void SaveBar(BarConfig bar)
    {
        NoteOwnWrite();   // even Directory.CreateDirectory on an existing folder raises a change event
        XElement root;
        string savePath;

        if (!string.IsNullOrEmpty(bar.FilePath))
        {
            // Group bar: save items only to the flat .xml file
            savePath = bar.FilePath;
            var dir = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            root = new XElement("bar");
        }
        else
        {
            // Main bar: save full config to FolderPath/_bar.xml
            Directory.CreateDirectory(bar.FolderPath);
            savePath = Path.Combine(bar.FolderPath, "_bar.xml");
            root = new XElement("bar",
                WAColor("toolbarColor",       bar.ToolbarColor),
                WAColor("buttonColor",        bar.ButtonColor),
                WAColor("buttonTextColor",    bar.ButtonTextColor),
                WAColor("separatorColor",     bar.SeparatorColor),
                WAColor("separatorTextColor", bar.SeparatorTextColor),
                new XAttribute("separatorTextAlign", bar.SeparatorTextAlign),
                WA("showBorders",   bar.ShowBorders),
                WA("displayTarget", bar.DisplayTarget),
                WA("enabled",       bar.Enabled)
            );
        }

        var barDir = Path.GetDirectoryName(savePath)!;
        var items  = new XElement("items");
        foreach (var sc in bar.Shortcuts)
        {
            if (sc.IsGroup)
            {
                RelocateGroupFile(sc, barDir);
                items.Add(new XElement("group",
                    new XAttribute("text", sc.Text),
                    new XAttribute("file", Path.GetFileName(sc.GroupFile))));
            }
            else if (sc.IsSeparator)
            {
                var el = new XElement("separator");
                if (!string.IsNullOrEmpty(sc.Text)) el.Add(new XAttribute("text", sc.Text));
                items.Add(el);
            }
            else
            {
                var el = new XElement("button",
                    new XAttribute("text",   sc.Text),
                    new XAttribute("target", sc.Target));
                if (!string.IsNullOrEmpty(sc.Arguments))        el.Add(new XAttribute("args",    sc.Arguments));
                if (!string.IsNullOrEmpty(sc.WorkingDirectory)) el.Add(new XAttribute("workdir", sc.WorkingDirectory));
                if (!string.IsNullOrEmpty(sc.Hotkey))           el.Add(new XAttribute("hotkey",  sc.Hotkey));
                if (sc.LaunchShellShortcut)                     el.Add(new XAttribute("shell",   "true"));
                items.Add(el);
            }
        }
        root.Add(items);
        WriteAtomic(savePath, new XDocument(new XDeclaration("1.0", "utf-8", null), root));
    }

    // ── Default bar creation ──────────────────────────────────────────────────

    public List<BarConfig> CreateDefaultBars()
    {
        var list = new List<BarConfig>();
        for (int i = 0; i < 4; i++)
        {
            var bar = new BarConfig
            {
                DockPosition = AllEdges[i],
                Name         = AllEdges[i].ToString(),
                FolderPath   = Path.Combine(_dir, AllEdges[i].ToString()),
                Enabled      = i < 3,
            };
            SaveBar(bar);
            list.Add(bar);
        }
        return list;
    }

    // ── Group bar (flat .xml) ─────────────────────────────────────────────────

    public BarConfig? LoadGroupBar(string xmlFilePath)
    {
        // New format: direct .xml file
        if (File.Exists(xmlFilePath))
        {
            try
            {
                var root = XDocument.Load(xmlFilePath).Root!;
                var bar = new BarConfig
                {
                    Name     = Path.GetFileNameWithoutExtension(xmlFilePath),
                    FilePath = xmlFilePath,
                };
                bar.Shortcuts = ReadItems(root, Path.GetDirectoryName(xmlFilePath)!);
                return bar;
            }
            catch { return null; }
        }
        // Fallback: old format — subfolder/_bar.xml (pre-migration)
        var legacyDir = Path.Combine(
            Path.GetDirectoryName(xmlFilePath)!,
            Path.GetFileNameWithoutExtension(xmlFilePath));
        if (Directory.Exists(legacyDir))
        {
            var legacyBar = LoadBarFromFolder(legacyDir);
            if (legacyBar != null)
            {
                legacyBar.FilePath   = xmlFilePath;
                legacyBar.FolderPath = "";
                return legacyBar;
            }
        }
        return null;
    }

    // ── Group folder → flat file migration ───────────────────────────────────

    public bool NeedsGroupMigration =>
        Directory.EnumerateDirectories(_dir)
                 .Any(barDir => File.Exists(Path.Combine(barDir, "_bar.xml")) &&
                      Directory.EnumerateDirectories(barDir)
                               .Any(sub => File.Exists(Path.Combine(sub, "_bar.xml"))));

    public void MigrateGroupFoldersToFiles()
    {
        foreach (var barDir in Directory.EnumerateDirectories(_dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(barDir, "_bar.xml"))) continue;
            bool anyMigrated = false;
            foreach (var sub in Directory.EnumerateDirectories(barDir).ToList())
            {
                var oldXml = Path.Combine(sub, "_bar.xml");
                if (!File.Exists(oldXml)) continue;
                var newXml = Path.Combine(barDir, Path.GetFileName(sub) + ".xml");
                try
                {
                    File.Copy(oldXml, newXml, overwrite: true);
                    Directory.Delete(sub, recursive: true);
                    anyMigrated = true;
                }
                catch { /* leave it; LoadGroupBar fallback will handle old format */ }
            }
            if (anyMigrated)
            {
                var bar = LoadBarFromFolder(barDir);
                if (bar != null) SaveBar(bar);
            }
        }
    }

    // ── Migration from legacy Coolbarz format ─────────────────────────────────

    public void MigrateFromLegacy()
    {
        SaveGlobal(ReadLegacyGlobal());
        for (int i = 0; i < 4; i++)
        {
            var bar = ReadLegacyBar(i, AllEdges[i]);
            bar.Shortcuts  = ReadLegacyShortcuts(i);
            bar.Name       = AllEdges[i].ToString();
            bar.FolderPath = Path.Combine(_dir, AllEdges[i].ToString());
            SaveBar(bar);
        }
    }

    private GlobalConfig ReadLegacyGlobal()
    {
        var c = new GlobalConfig();
        var path = Path.Combine(_dir, "Coolbarz.cfg");
        if (!File.Exists(path)) return c;
        try
        {
            var doc = XDocument.Load(path);
            if (doc.Root?.Name.LocalName != "ArrayOfXmlSetting") return c;
            string? R(string n) => doc.Root.Elements("XmlSetting")
                .FirstOrDefault(e => e.Element("Name")?.Value == n)?.Element("Value")?.Value;
            bool LB(string n, bool d) { var s = R(n); return bool.TryParse(s, out var v) ? v : d; }
            int  LI(string n, int d)  { var s = R(n); return int.TryParse(s, out var v) ? v : d; }
            double LD(string n, double d) { var s = R(n); if (s == null) return d;
                return double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d; }
            HashSet<DockPosition> LE(string n, HashSet<DockPosition> d) {
                var s = R(n); if (s == null) return d;
                var set = new HashSet<DockPosition>();
                foreach (var e in AllEdges) if (s.Contains(e.ToString(), StringComparison.OrdinalIgnoreCase)) set.Add(e);
                return set; }
            int[] LA(string n, int[] d) { var s = R(n); if (s == null) return d;
                var p = s.Split(','); var a = (int[])d.Clone();
                for (int i = 0; i < a.Length && i < p.Length; i++) if (!int.TryParse(p[i].Trim(), out a[i])) return d;
                return a; }
            T LEN<T>(string n, T d) where T : struct, System.Enum
                { var s = R(n); return System.Enum.TryParse<T>(s, true, out var v) ? v : d; }

            c.ShowTrayIcon       = LB("ShowTrayIcon",       c.ShowTrayIcon);
            c.ShowToolTips       = LB("ShowToolTips",       c.ShowToolTips);
            c.DataInterval       = LI("DataInterval",       c.DataInterval);
            c.FolderParsingMenu  = LB("FolderParsingMenu",  c.FolderParsingMenu);
            // Legacy key names from original Coolbarz.cfg:
            c.StretchBars        = LE("StretchCoolbars",        c.StretchBars);
            c.CenterBars         = LE("CenterCoolbars",         c.CenterBars);
            c.ReverseAlignBars   = LE("ReverseAlignCoolbars",   c.ReverseAlignBars);
            c.ScrollableBars     = LE("ScrollCoolbars",         c.ScrollableBars);
            c.AlwaysVisible      = LE("AlwaysVisible",          c.AlwaysVisible);
            c.ProtectTopLeftCorner   = LB("ProtectTopLeftCorner",   c.ProtectTopLeftCorner);
            c.ProtectTopRightCorner  = LB("ProtectTopRightCorner",  c.ProtectTopRightCorner);
            for (int i = 0; i < 4; i++)
                c.ButtonLayout[i] = LEN<ButtonStyle>($"ButtonLayout{AllEdges[i]}", c.ButtonLayout[i]);
            c.ShowDelay              = LA("ShowDelay",       c.ShowDelay);
            c.HideDelay              = LA("HideDelay",       c.HideDelay);
            c.MaxButtonWidth         = LA("MaxButtonWidth",  c.MaxButtonWidth);
            c.PointerDistance        = LA("PointerDistance", c.PointerDistance);
            c.ToolbarFading          = LB("ToolbarFading",          c.ToolbarFading);
            c.DialogFading           = LB("DialogFading",           c.DialogFading);
            c.ToolbarFadeInTime      = LI("ToolbarFadeInTime",      c.ToolbarFadeInTime);
            c.ToolbarFadeInInterval  = LD("ToolbarFadeInInterval",  c.ToolbarFadeInInterval);
            c.ToolbarFadeOutTime     = LI("ToolbarFadeOutTime",     c.ToolbarFadeOutTime);
            c.ToolbarFadeOutInterval = LD("ToolbarFadeOutInterval", c.ToolbarFadeOutInterval);
            c.MaxOpacity             = LD("MaxOpacity",             c.MaxOpacity);
        }
        catch { }
        return c;
    }

    private BarConfig ReadLegacyBar(int idx, DockPosition dock)
    {
        var c = new BarConfig { DockPosition = dock };
        var path = Path.Combine(_dir, $"Coolbar{idx + 1}.cfg");
        if (!File.Exists(path)) return c;
        try
        {
            var doc = XDocument.Load(path);
            if (doc.Root?.Name.LocalName != "ArrayOfXmlSetting") return c;
            string? R(string n) => doc.Root.Elements("XmlSetting")
                .FirstOrDefault(e => e.Element("Name")?.Value == n)?.Element("Value")?.Value;
            Color LC(string n, Color d) {
                var s = R(n); if (s == null) return d;
                var named = System.Drawing.Color.FromName(s.Trim());
                if (named.IsKnownColor) return named;
                var p = s.Split(';'); var v = new int[p.Length];
                for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i].Trim(), out v[i])) return d;
                return p.Length == 3 ? System.Drawing.Color.FromArgb(v[0], v[1], v[2]) : d; }
            bool LB(string n, bool d)  { var s = R(n); return bool.TryParse(s, out var bv) ? bv : d; }
            int  LI(string n, int d)   { var s = R(n); return int.TryParse(s, out var iv)  ? iv : d; }
            T LEN<T>(string n, T d) where T : struct, System.Enum
                { var s = R(n); return System.Enum.TryParse<T>(s, true, out var v) ? v : d; }

            c.DockPosition       = LEN<DockPosition>("ToolbarDockPosition",      c.DockPosition);
            c.ToolbarColor       = LC("ToolbarColor",              c.ToolbarColor);
            c.ButtonColor        = LC("ToolbarButtonColor",        c.ButtonColor);
            c.ButtonTextColor    = LC("ToolbarButtonTextColor",    c.ButtonTextColor);
            c.SeparatorColor     = LC("ToolbarSeparatorColor",     c.SeparatorColor);
            c.SeparatorTextColor = LC("ToolbarSeparatorTextColor", c.SeparatorTextColor);
            c.SeparatorTextAlign = R("ToolbarSeparatorTextAlign") ?? c.SeparatorTextAlign;
            c.ShowBorders        = LB("ShowBorders",               c.ShowBorders);
            c.DisplayTarget      = LI("DisplayTarget",             c.DisplayTarget);
            c.Enabled            = LB("ToolbarEnabled",            c.Enabled);
        }
        catch { }
        return c;
    }

    private List<ShortcutItem> ReadLegacyShortcuts(int idx)
    {
        var list = new List<ShortcutItem>();
        var path = Path.Combine(_dir, $"Coolbar{idx + 1}.shortcuts");
        if (!File.Exists(path)) return list;
        try
        {
            var doc = XDocument.Load(path);
            if (doc.Root?.Name.LocalName != "ArrayOfXmlShortcut") return list;
            foreach (var e in doc.Root.Elements("XmlShortcut"))
                list.Add(new ShortcutItem
                {
                    Text              = e.Element("Text")?.Value             ?? "",
                    Hotkey            = e.Element("Shortcut")?.Value         ?? "",
                    Target            = ShortcutItem.CleanPath(e.Element("Target")?.Value           ?? ""),
                    Arguments         = e.Element("Arguments")?.Value        ?? "",
                    WorkingDirectory  = ShortcutItem.CleanPath(e.Element("WorkingDirectory")?.Value ?? ""),
                    LaunchShellShortcut = bool.TryParse(e.Element("LaunchShellShortcut")?.Value, out var b) && b,
                });
        }
        catch { }
        return list;
    }

    // ── XML attribute readers ─────────────────────────────────────────────────

    private static bool   Attr(XElement e, string n, bool d)   { var s = e.Attribute(n)?.Value; return bool.TryParse(s, out var v) ? v : d; }
    private static int    Attr(XElement e, string n, int d)    { var s = e.Attribute(n)?.Value; return int.TryParse(s, out var v) ? v : d; }
    private static double AttrD(XElement e, string n, double d) { var s = e.Attribute(n)?.Value;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d; }

    private static T AttrE<T>(XElement e, string n, T d) where T : struct, System.Enum
        { var s = e.Attribute(n)?.Value; return System.Enum.TryParse<T>(s, true, out var v) ? v : d; }

    private static Color AttrColor(XElement e, string n, Color d)
    {
        var s = e.Attribute(n)?.Value; if (s == null) return d;
        var named = System.Drawing.Color.FromName(s.Trim());
        if (named.IsKnownColor) return named;
        var p = s.Split(';'); var v = new int[p.Length];
        for (int i = 0; i < p.Length; i++) if (!int.TryParse(p[i].Trim(), out v[i])) return d;
        return p.Length switch
        {
            3 => System.Drawing.Color.FromArgb(v[0], v[1], v[2]),
            4 => System.Drawing.Color.FromArgb(v[0], v[1], v[2], v[3]),
            _ => d
        };
    }

    private static HashSet<DockPosition> AttrEdgeSet(XElement e, string n, HashSet<DockPosition> d)
    {
        var s = e.Attribute(n)?.Value; if (s == null) return d;
        var set = new HashSet<DockPosition>();
        foreach (var edge in AllEdges)
            if (s.Split(',').Any(p => string.Equals(p.Trim(), edge.ToString(), StringComparison.OrdinalIgnoreCase)))
                set.Add(edge);
        return set;
    }

    private static int[] AttrIntArr(XElement e, string n, int[] d)
    {
        var s = e.Attribute(n)?.Value; if (s == null) return d;
        var parts = s.Split(','); var arr = (int[])d.Clone();
        for (int i = 0; i < arr.Length && i < parts.Length; i++)
            if (!int.TryParse(parts[i].Trim(), out arr[i])) return d;
        return arr;
    }

    private static T[] AttrEnumArr<T>(XElement e, string n, T[] d) where T : struct, System.Enum
    {
        var s = e.Attribute(n)?.Value; if (s == null) return d;
        var parts = s.Split(','); var arr = (T[])d.Clone();
        for (int i = 0; i < arr.Length && i < parts.Length; i++)
            if (!System.Enum.TryParse<T>(parts[i].Trim(), true, out arr[i])) return d;
        return arr;
    }

    // ── XML attribute writers ─────────────────────────────────────────────────

    private static XAttribute WA(string n, bool v)   => new(n, v ? "true" : "false");
    private static XAttribute WA(string n, int v)    => new(n, v.ToString(CultureInfo.InvariantCulture));
    private static XAttribute WA(string n, double v) => new(n, v.ToString(CultureInfo.InvariantCulture));
    private static XAttribute WA<T>(string n, T v) where T : struct, System.Enum => new(n, v.ToString());

    private static XAttribute WAEdgeSet(string n, HashSet<DockPosition> v) =>
        new(n, string.Join(",", AllEdges.Where(v.Contains)));

    private static XAttribute WAArr(string n, IEnumerable<string> v) =>
        new(n, string.Join(",", v));

    private static XAttribute WAColor(string n, Color c) =>
        new(n, c.IsNamedColor ? c.Name
             : c.A == 255    ? $"{c.R};{c.G};{c.B}"
                             : $"{c.A};{c.R};{c.G};{c.B}");

    // ── Atomic write ──────────────────────────────────────────────────────────

    private void WriteAtomic(string path, XDocument doc)
    {
        NoteOwnWrite();
        var tmp = path + ".tmp";
        var ws  = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " };
        using (var w = XmlWriter.Create(tmp, ws)) doc.Save(w);
        File.Move(tmp, path, overwrite: true);
        NoteOwnWrite();
    }
}
