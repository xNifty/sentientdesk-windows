# SentientDesk (Windows)

A free, open-source desktop icon organizer for Windows, inspired by that other desktop organizer. Group desktop shortcuts into movable, resizable, collapsible fences you control and can extend.

**Stack:** WPF / .NET 8 · Windows 10/11 · Velopack updates via GitHub Releases

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
- [Velopack CLI (`vpk`)](https://docs.velopack.io/) to pack releases locally (`dotnet tool install -g vpk`) — optional for day-to-day `dotnet run`

## Build & run

From the repository root:

```powershell
dotnet build
dotnet run --project SentientDesk
```

The app runs from the **system tray** (no main window). Double-click the tray icon or use the tray menu to create a fence. Hotkey: **Ctrl+Shift+F**.

Layout is saved to `%AppData%\SentientDesk\layout.json`.

## Installer & updates

The app is packaged with [Velopack](https://velopack.io/). Auto-updates check [GitHub Releases](https://github.com/xNifty/sentientdesk-windows/releases) for new packages.

The publish is **self-contained win-x64**, so end users do not need the .NET runtime.

### Which installer to use

| Asset | Behavior |
|-------|----------|
| **`.msi` (recommended)** | Wizard: choose per-user (`%LocalAppData%`) or Program Files / a custom folder. Finish screen has a **Launch SentientDesk** checkbox (checked by default; uncheck to skip). |
| **`Setup.exe`** | One-click install to `%LocalAppData%\SentientDesk` and launches immediately (no folder picker). |

Auto-updates work the same for both install methods.

### Local pack

```powershell
.\scripts\New-Release.ps1
# optional: .\scripts\New-Release.ps1 -Version 0.1.1
# optional: .\scripts\New-Release.ps1 -Upload   # needs GITHUB_TOKEN or gh auth
```

Output: `artifacts\releases\` (`.msi`, Setup.exe, nupkg, release metadata).

### Publish a release (CI)

1. Bump `<Version>` in `SentientDesk/SentientDesk.csproj` if you want local builds to match.
2. Commit, then create and push a version tag:

```powershell
git tag v0.1.1
git push origin v0.1.1
```

The [Release](.github/workflows/release.yml) workflow publishes a GitHub Release with Velopack assets (including the MSI). Installed clients pick up the update via **Check for updates…** (also checked quietly on startup).

Windows SmartScreen will warn on unsigned hobby builds — that is expected. Layout data under `%AppData%\SentientDesk` is left in place on uninstall. Uninstall restores the native desktop icon ListView and clears the “Start with Windows” Run entry.

If you previously installed via the old Inno Setup build (Program Files), uninstall it once and install from the Velopack **`.msi`**.

## Notes

- **Hide desktop icons** toggles the Explorer desktop ListView (`SysListView32`).
- **Start with Windows** writes a HKCU `Run` registry entry.
- Exit from the tray menu restores desktop icons.

## Features (v0.1)

| Feature | Support |
|---------|---------|
| Movable / resizable / collapsible / recolorable fences | Yes |
| Desktop icons inside fences | User + Public Desktop (first-launch import; overlay thereafter) |
| Drag icons between fences | Yes |
| Double-click icon to open | Yes |
| Icon context menu | Open, Cut/Copy, Delete (remove from fence), Rename, Properties, Move to |
| Default fence for new icons | Right-click fence title → Set as default |
| Hide native desktop icons | Explorer ListView (restored on Exit / uninstall) |
| Start at login | HKCU Run |
| New-fence hotkey | Ctrl+Shift+F |
| Snap-to-grid | Yes (16px; tray toggle; hold Alt to bypass; grid overlay while dragging) |
| Fence templates | Blank / Narrow column / Wide shelf / Large (tray submenu) |
| Auto-update via GitHub Releases | Yes (Velopack) |
| Layout JSON | `%AppData%\SentientDesk\` |
| Multi-monitor placement | Virtual-desktop coords; off-screen recovery; new fence on cursor monitor |

## Usage

### Tray menu

| Action | Description |
|--------|-------------|
| New fence | Submenu of templates (Blank matches the hotkey) |
| Hide desktop icons | Toggles native desktop icons |
| Snap to grid | Quantize fence move/resize to a 16px grid (off = free placement) |
| Start with Windows | Launch at login |
| Refresh icons | Rescans Desktop folders |
| Open layout folder | Opens the settings folder |
| Check for updates… | Checks GitHub Releases for a newer build |
| Exit | Restores desktop icons and quits |

### Fence tips

- Drag the title bar to move
- Drag the bottom-right grip to resize
- With **Snap to grid** on, fences align to a 16px grid; a faint overlay appears while dragging. Hold **Alt** to place freely for that gesture
- Double-click the title bar to collapse/expand
- Double-click the title text to rename
- Right-click the title bar for color / default fence / remove
- Right-click an icon for Open, Cut/Copy, Delete (from fence), Rename, Properties, Move to
- Delete key removes the selected icon from the fence (file stays on disk)

### Overlay model

Fences organize a view of Desktop folder items; they do not replace your desktop files. The first launch imports current desktop icons into fences. New files that appear on the Desktop go to the default fence. Removing an icon from a fence only hides it from the organizer — Exit or uninstall shows the normal desktop icons again.

## Architecture

| Area | Role |
|------|------|
| `Services/FenceManager` | Owns fence windows and persistence |
| `Services/DesktopIconService` | Enumerates Desktop folders + icons |
| `Services/DesktopIconHider` | Shows/hides the shell desktop ListView |
| `Services/UpdateService` | Velopack + GitHub Releases update checks |
| `Views/FenceWindow` | WPF overlay UI per fence |
| `Views/GridOverlayWindow` | Snap-grid overlay during move/resize |
| `Models` / `LayoutStore` | JSON layout under AppData |

## Roadmap ideas

- Portal / rollup animations
- Rules that auto-sort new icons by type or name
- Parent fence chrome to the wallpaper WorkerW layer

## Related

- [SentientDesk for macOS](https://github.com/xNifty/sentientdesk-macos)

## License

MIT — use and modify freely.
