# Top Bar Revolution

![Top Bar Revolution](docs/preview.png)

A fork of **[TopBar](https://github.com/Rikonardo/TopBar) by [Rikonardo](https://github.com/Rikonardo)**, the
simple and modern top bar for Windows. All credit for TopBar itself — the bar, its widgets and its
plugin system — goes to Rikonardo. Top Bar Revolution builds on top of it: an in-process engine that
plugs into TopBar through its `extensions` folder, a settings app, and an installer.

## What it adds

- **Settings window** (right-click the bar → *Settings…*): themes, fonts, bar height, frosted / clear /
  acrylic backdrops, rounded "pill" groups and a left / center / right layout — all applied live.
- **Arrange** your pills by dragging them between the left, center and right of the bar.
- **Clock panel**: click the clock for a calendar with a stopwatch, timer and alarm.
- **Usage widgets**: CPU graph with an adjustable rate, RAM in GB, and a GPU load bar with optional VRAM.
- **Click any usage pill** to see the processes using the most CPU, memory or GPU.
- **Your other PCs on the bar**: every PC running Top Bar Revolution on your LAN shares its CPU, RAM and
  GPU usage and shows the others' — they find each other automatically (UDP broadcast, port 51736).
  Choose per part what to show (PC name, bar, VRAM), and click a network PC's pill to see its top processes.
- **Wake-on-LAN**: a PC that's off turns into a ⏻ button on the bar; click it (or right-click the bar →
  *Wake up*) to wake it.
- **Monitor watcher**: restarts TopBar after a display change so it stays docked at the top.

## Install

Run `TopBarExtrasSetup.exe` (built as below). It installs the engine, the settings app and the monitor
watcher, and — on a PC that doesn't have it yet — TopBar itself with a starter layout. Two optional
network steps ask Windows for admin approval once: a firewall rule so the other PCs can be heard, and
arming the wired network card for Wake-on-LAN (waking from shut down may also need Wake-on-LAN enabled
in the BIOS).

## Build

Needs Windows, [TopBar](https://github.com/Rikonardo/TopBar/releases) installed (its `TopBar.exe` and
`Newtonsoft.Json.dll` are compile references), the .NET Framework 4.8 reference assemblies, Python 3 with
PyInstaller and Pillow, and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```bat
installer\build.cmd
```

That compiles the engine (`extension\bin\TopBarExtras.dll`), the monitor watcher, the settings app
(`dist\TopBar Settings.exe`) and the installer (`dist\TopBarExtrasSetup.exe`). The installer packs the
TopBar binaries from your local TopBar install for PCs that don't have it — TopBar's source is at
[Rikonardo/TopBar](https://github.com/Rikonardo/TopBar).

To run the settings app from source: `TopBar Settings.cmd` (or `python topbar_settings.py`).

## Layout

| Folder | What's in it |
| --- | --- |
| `extension/` | The engine and widgets, loaded into TopBar's process (C#, .NET Framework) |
| `tbx/` | The settings app (Python / tkinter) |
| `watch/` | The monitor watcher |
| `installer/` | Inno Setup script and the build script |
| `assets/` | Icon, the network setup script, and the bundled JetBrains Mono font |

## License

Top Bar Revolution is released under the [GNU General Public License v3.0](LICENSE), the same license as
TopBar. TopBar is copyright © 2021 Rikonardo. The bundled JetBrains Mono font is under the SIL Open Font
License 1.1 (`assets/fonts/OFL.txt`).
