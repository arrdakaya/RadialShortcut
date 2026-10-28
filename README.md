<p align="center">
  <img src="docs/hero.png" alt="RadialShortcut" width="100%">
</p>

<p align="center">
  <a href="https://github.com/arrdakaya/RadialShortcut/releases/latest/download/RadialShortcut-Setup.exe"><img src="https://img.shields.io/badge/Download-Windows%20x64-0067C0?style=for-the-badge&logo=windows&logoColor=white" alt="Download for Windows"></a>
  <a href="https://github.com/arrdakaya/RadialShortcut/releases/latest"><img src="https://img.shields.io/github/v/release/arrdakaya/RadialShortcut?style=for-the-badge&color=1d2129&label=release" alt="Latest release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-1d2129?style=for-the-badge" alt="MIT license"></a>
</p>

<p align="center">
  Hold the right mouse button and flick: a ring opens around the cursor.<br>
  Release over a slice to grab text from the screen, translate it, record a clip, convert a file or remove a background.
</p>

## Install

Download **[RadialShortcut-Setup.exe](https://github.com/arrdakaya/RadialShortcut/releases/latest/download/RadialShortcut-Setup.exe)** and run it. No admin rights needed; it adds a Start menu entry and can start with Windows. Updates show up in the tray menu, and it uninstalls from Windows Settings like any other app.

Prefer no installer? The [zip](https://github.com/arrdakaya/RadialShortcut/releases/latest/download/RadialShortcut-win-x64.zip) runs from any folder.

Settings live in `%AppData%\RadialShortcut\config.json`. You can define per-app rings and your own actions there.

---

<p align="center">
  <img src="docs/modes.png" alt="Screen ring, file ring and drop zones" width="100%">
</p>

| Gesture | |
| --- | --- |
| Right-click + drag | Screen ring |
| Right-click + drag with files selected in Explorer | File ring |
| Shift + right-click + drag | The other ring |
| Drag a file and pause (or press Shift mid-drag) | Drop zones: drop the file on a tool |
| Ctrl + Alt + Space | Ring from the keyboard |

A plain right-click still works as usual.

## Tools

**Screen ring:** text from screen (OCR) · color picker · translate · screenshot · scrolling capture · screen recording · Google Lens · pin to screen

**File ring:** convert · compress · resize · trim video · annotate · download from link · library · remove background

Everything runs on your machine: OCR uses Windows' built-in engine and background removal uses a local model.

## Build

```powershell
dotnet test
dotnet publish src/RadialShortcut.App -c Release -r win-x64 --self-contained -o publish
```

Requires Windows 10 (19041+) and the .NET 10 SDK.

Releases are built by GitHub Actions: push a tag such as `v0.9.0` and the workflow tests, builds the setup and the zip, and publishes the release. `python tools/make_readme_images.py` redraws the README images from the app itself.

## License

[MIT](LICENSE)
