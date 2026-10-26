# BestBarz

Auto-hiding toolbars on the edges of the Windows screen, inspired by **Coolbarz**.
Move the pointer to a screen edge and a bar of program, folder and file shortcuts slides in;
move away and it hides again.

Built with C# and WinForms on .NET 10.

## Features

- **One bar per edge** — Left, Top, Right and Bottom — each with its own buttons, colors,
  button style (large/small icon, with or without text) and alignment (Near, Center, Far, Stretch).
- **Groups**: buttons that open a popup with more shortcuts.
- **Edit in place** from the right-click menu: Cut, Copy, Paste, Edit, Remove, separators,
  and **Arrange mode** to reorder with the keyboard (Enter saves, Esc cancels) or by dragging.
- **Quick content**: *Add Programs…* picks from the Start menu with search; *Add Group* creates
  ready-made groups (system utilities, admin tools, Control Panel applets, system folders).
- **Stays out of the way**:
  - hides automatically while a fullscreen app (a game, a remote desktop, a video) is in front;
  - optionally opens over apps only while **Ctrl** is held;
  - the bar on the taskbar's side opens only with **Ctrl**;
  - **Ctrl+Alt+B** hides or shows every bar.
- Bars sit beside the Windows taskbar, or over it if you prefer.
- Paths may use environment variables (`%AppData%`, `%SystemRoot%`, …), so buttons work on
  other PCs and user accounts.
- Plain XML configuration, reloaded automatically when you edit it.

## Requirements

- Windows 10 or 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run;
  the .NET 10 SDK to build.

## Build and run

```
dotnet build -c Release
bin\Release\net10.0-windows\BestBarz.exe
```

Settings are reached from the tray icon or from any bar's right-click menu (*Global ▸ Settings…*),
where *Start with Windows* is also available.

## Configuration

By default the configuration lives in a `Config` folder next to `BestBarz.exe`.
Another folder can be given on the command line:

```
BestBarz.exe --config "%AppData%\BestBarz"
```

```
Config\
  _global.xml          settings shared by all bars
  Left\  Top\  Right\  Bottom\
    _bar.xml           the bar's settings and buttons
    <Group>.xml        one file per group button
```

The folder name decides the edge, and other folders are ignored: to switch a whole bar,
rename folders (for example `Top` → `Top-Work`, `Top-Home` → `Top`).
Changes made by hand are picked up automatically.

### Coming from Coolbarz

Copy Coolbarz's `Coolbarz.cfg`, `Coolbar1.cfg` … `Coolbar4.cfg` and `Coolbar1.shortcuts` …
`Coolbar4.shortcuts` into an empty configuration folder before the first run: BestBarz converts them.

## License

[MIT](LICENSE)
