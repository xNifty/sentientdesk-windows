# SentientDesk (Windows)

A free, open-source desktop icon organizer for Windows, inspired by that other desktop organizer. Group desktop shortcuts into movable, resizable, collapsible fences you control and can extend.

**Stack:** WPF / .NET 8 · Windows 10/11

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- [Inno Setup 6](https://jrsoftware.org/isdl.php) to build the installer (optional for day-to-day `dotnet run`)

## Build & run

From the repository root:

```powershell
dotnet build
dotnet run --project SentientDesk
```

The app runs from the **system tray** (no main window). Double-click the tray icon or use the tray menu to create a fence. Hotkey: **Ctrl+Shift+F**.

Layout is saved to `%AppData%\SentientDesk\layout.json`.

## Installer (optional)

Creates an unsigned Setup.exe. The wizard defaults to `C:\Program Files\SentientDesk` (`{autopf}`) and still lets the user pick any folder. Installing to Program Files requires elevation. The publish is **self-contained win-x64**, so end users do not need the .NET runtime.

```powershell
.\scripts\New-Installer.ps1
# optional: .\scripts\New-Installer.ps1 -Version 0.1.1
```

Output: `artifacts\installer\SentientDesk-Setup-<version>.exe`

Windows SmartScreen will warn on unsigned hobby builds — that is expected. Layout data under `%AppData%\SentientDesk` is left in place on uninstall.

## Notes

- **Hide desktop icons** toggles the Explorer desktop ListView (`SysListView32`).
- **Start with Windows** writes a HKCU `Run` registry entry.
- Exit from the tray menu restores desktop icons.

## Features (v0.1)

| Feature | Support |
|---------|---------|
| Movable / resizable / collapsible / recolorable fences | Yes |
| Desktop icons inside fences | User + Public Desktop |
| Drag icons between fences | Yes |
| Double-click icon to open | Yes |
| Hide native desktop icons | Explorer ListView |
| Start at login | HKCU Run |
| New-fence hotkey | Ctrl+Shift+F |
| Layout JSON | `%AppData%\SentientDesk\` |

## Usage

### Tray menu

| Action | Description |
|--------|-------------|
| New fence | Creates an empty fence |
| Hide desktop icons | Toggles native desktop icons |
| Start with Windows | Launch at login |
| Refresh icons | Rescans Desktop folders |
| Open layout folder | Opens the settings folder |
| Exit | Restores desktop icons and quits |

### Fence tips

- Drag the title bar to move
- Drag the bottom-right grip to resize
- Double-click the title bar to collapse/expand
- Double-click the title text to rename
- Right-click the title bar for color / remove

## Architecture

| Area | Role |
|------|------|
| `Services/FenceManager` | Owns fence windows and persistence |
| `Services/DesktopIconService` | Enumerates Desktop folders + icons |
| `Services/DesktopIconHider` | Shows/hides the shell desktop ListView |
| `Views/FenceWindow` | WPF overlay UI per fence |
| `Models` / `LayoutStore` | JSON layout under AppData |

## Roadmap ideas

- Portal / rollup animations
- Rules that auto-sort new icons by type or name
- Multi-monitor aware placement
- Snap-to-grid and fence templates
- Parent fence chrome to the wallpaper WorkerW layer

## Related

- [SentientDesk for macOS](https://github.com/xNifty/sentientdesk-macos)

## License

MIT — use and modify freely.
