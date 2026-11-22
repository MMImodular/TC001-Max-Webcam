# TC001 Max Thermal Webcam

> **How this code was made.** Almost all of the code, scripts and documentation in this repository
> were written by **Claude** (Anthropic's AI model) working in Claude Code. The repository owner
> directed the work, reviewed it, and tested it on their own TOPDON TC001 Max. Treat it as you
> would any AI-generated code: read it before you trust it, and expect mistakes. Code adapted from
> other projects, including code translated into another language, is attributed below, in
> `THIRD_PARTY_NOTICES.txt` and in each file's header.

Turns a **TOPDON TC001 Max** thermal camera into an ordinary webcam on **Windows 11**. Any app
that can use a webcam (the Windows Camera app, Teams, Zoom, browsers, OBS) sees a camera named
**"TC001 Max Thermal Webcam"**. It shows the thermal picture in false colour, with the hottest
spot marked and the temperatures written on the image.

## What it is

- The TC001 Max is not a UVC webcam. It is a vendor-specific USB device that Windows' camera stack
  cannot see. This project is a Media Foundation **virtual camera** (C#, .NET 10). Its media
  source is loaded by the Windows Camera Frame Server and reads the camera directly over
  **WinUSB**, which you bind to the camera once with Zadig.
- The camera is opened only while an app streams from it, and closed within about a second after
  the last app stops. When nothing streams, no program or service of this project runs.
- Output: 640 x 480 at 25 fps, RGB32 or NV12, drawn on the CPU. Shown on the image:
  - the hottest pixel, with its temperature;
  - a centre mark;
  - `MAX` / `CENTRE` readouts;
  - the colour scale, the range mode and the time.
- Settings in a small `settings.json` apply while streaming: rotation, mirroring, °C / °F,
  palette (inferno, iron, rainbow, white hot, black hot, custom: the same as labbench), range (auto, locked, manual) and which marks are
  drawn on the picture. You can also turn off the camera's automatic shutter.
- A tray icon (the notification area by the clock) opens a small settings window that stays
  open while you change things.
- It has a native `.msi` installer and a one-click updater.
- A small file-based "claims" protocol (`Core/CameraClaims.cs`) lets the owner's private lab
  software take turns with the webcam on the camera. The webcam works without it.

## Where the project is at

**Version 1.3.2 (November 2026). A personal project that works on the owner's PC. It is offered
as-is, with no support.**

Tested on real hardware (one TC001 Max, firmware 1.01.000, Windows 11 x64):

- the USB protocol, start sequence, stream layout and temperature scale;
- installing with the `.msi`, and the virtual camera working in apps (Zadig's default WinUSB
  install lets the Frame Server open the camera);
- recovery from occasional USB stream stalls;
- the auto-shutter switch;
- the repair for a camera glitch ("picture leak") that caused false readings of about 240 °C.

The self-test (fakes and recorded camera captures) checks the protocol decoding, the renderer,
the settings, the reader's state machine, the camera handoff and the update logic.

**Not tested:**

- unplugging and replugging while streaming;
- reconnecting after a long stall;
- several apps sharing the camera at once;
- other PCs and other firmware;
- absolute temperature accuracy (a room near 20 °C read 26-28 °C).

**Possible next step:** a 1/4-20 tripod adapter with a flex board that reroutes the USB connector.
Mechanical notes and CAD files are in [TC001-Max-Mounting](https://github.com/MMImodular/TC001-Max-Mounting).

## Sources and attribution

TOPDON publishes no protocol documentation. The protocol comes from community reverse
engineering and from read-only measurements on one camera. `docs/PROTOCOL.md` cites a source for
each fact, and `docs/STATE_OF_THE_ART.md` surveys what was publicly known.

| Source | What this project took from it | Licence |
|---|---|---|
| [smourier/VCamNetSample](https://github.com/smourier/VCamNetSample), a port of [VCamSample](https://github.com/smourier/VCamSample) | The Media Foundation virtual-camera plumbing. **Adapted code**: `MediaSource.cs`, `MediaStream.cs`, `Activator.cs`, `Utilities/`. `Program.cs` and `Shared.cs` follow its structure. | MIT |
| [smourier/DirectN](https://github.com/smourier/DirectN) (`DirectNCore` NuGet) | Windows API interop, shipped with the binaries | MIT |
| [WendyOS PR #2621](https://github.com/wendylabsinc/WendyOS/pull/2621) (`go/internal/agent/usbthermal/`) | The TC001 Max start sequence, command transport and stream framing. The block finder in `Tc001MaxProtocol.cs` was **translated from its Go `parser.go`** (via an earlier Python port) and modified. | Apache-2.0, Wendy Labs |
| [jvdillon/p3-ir-camera](https://github.com/jvdillon/p3-ir-camera) | The protocol of the closely related Thermal Master P3: command format, CRC test vectors, register map, start sequence and temperature scale. **Adapted (translated) code** in `Tc001MaxProtocol.cs`, `CameraSession.cs` and `experiments/cam.py`. | Apache-2.0 |
| [xaionaro-go/thermalmaster](https://github.com/xaionaro-go/thermalmaster) | Command ids recovered from the vendor app, including the auto-shutter (auto-FFC) switch. **Translated code**: the VDCMD command builder. | CC0-1.0 |
| [danjulio/iCam](https://github.com/danjulio/iCam) (InfiRay Tiny1-C SDK documents) | Background on how InfiRay cores store shutter settings. No code or commands from it. | vendor SDK |
| [leswright1977/PyThermalCamera](https://github.com/leswright1977/PyThermalCamera) | The frame layout of the original TC001, which TC001 Max blocks resemble. No code from it. | Apache-2.0 |
| matplotlib "inferno" colour map | The default palette data (`InfernoLut.cs`) | CC0 |
| [Google's Turbo colour map](https://research.google/blog/turbo-an-improved-rainbow-colormap-for-visualization/) | The rainbow palette data (`TurboLut.cs`, generated from matplotlib's copy) | Apache-2.0, Google LLC |
| [WiX Toolset](https://wixtoolset.org) 5 | Builds the `.msi`. It is a build tool; its standard dialogs and custom actions are in the package. | MS-RL |
| [Zadig](https://zadig.akeo.ie/) | Binding WinUSB to the camera. A tool you run; not included. | GPL-3.0 |

Everything else was written for this project. The iron and black-hot palettes and the custom
palette's default colours match the owner's own, unpublished lab software (labbench), so both
show the same colours. Some of the code is a port of the owner's own, unpublished Python driver, which was itself written from the sources above; ported code is
therefore attributed to those sources. `THIRD_PARTY_NOTICES.txt` lists the origin of every file,
with the licence texts.

## Licence

[Apache License 2.0](LICENSE). Third-party parts keep their own licences (table above).

## Requirements

- Windows 11 x64. Virtual cameras do not exist on Windows 10.
- The **.NET 10 runtime, x64, installed system-wide** (`winget install Microsoft.DotNet.Runtime.10`).
  A per-user runtime does not work, because the media source runs inside a Windows service. To
  build, install the .NET 10 SDK instead (`winget install Microsoft.DotNet.SDK.10`).
- A TOPDON TC001 Max (USB `3474:4962`) on a USB 2.0 High-Speed or faster port. The stream is about
  35 MB/s.
- Administrator rights once, for the install.

## Install

1. **Bind WinUSB to the camera (once).**
   1. Run [Zadig](https://zadig.akeo.ie/) and tick *Options > List All Devices*.
   2. Pick the TC001 Max entry whose USB ID reads `3474` `4962` `00` (interface 0). Do not change
      any other device.
   3. Choose **WinUSB** and click *Install Driver*.

   To undo it: Device Manager > the camera > *Uninstall device*, tick *Attempt to remove the
   driver*, then replug the camera.
2. **Get the installer.** Download `TC001MaxWebcam-<version>-x64.msi` from the newest
   [release](https://github.com/MMImodular/TC001-Max-Webcam/releases/latest). GitHub builds each
   release from the source in this repository (`.github/workflows/release.yml`); the release notes
   give the installer's SHA-256.

   Or build it yourself (no admin). On the first build, NuGet downloads DirectNCore and WiX.

   ```powershell
   git clone https://github.com/MMImodular/TC001-Max-Webcam.git C:\src\TC001-Max-Webcam
   cd C:\src\TC001-Max-Webcam
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   ```

   `build.ps1` runs the self-test first (`tests\TC001MaxWebcam.Tests`, no hardware needed) and
   stops if it fails. It then publishes to `bin\publish\` and writes
   `TC001MaxWebcam-<version>-x64.msi` at the top of the repository (git ignores it). Options:
   `-SkipTests`, `-NoInstaller`, `-Configuration Debug`.
3. **Install.** Double-click the `.msi` and accept the one UAC prompt. The installer:
   - checks for the .NET runtime;
   - copies the program to `C:\Program Files\TC001MaxWebcam\` and adds that folder to `PATH`;
   - creates `C:\ProgramData\TC001MaxWebcam\` for the settings and the log;
   - registers the media source;
   - creates the virtual camera for all users;
   - adds *settings*, *status* and *Update* shortcuts to the Start menu;
   - starts the tray icon at every sign-in, and offers to start it now on its last page.

   Silent install, from an elevated prompt:
   `msiexec /i TC001MaxWebcam-<version>-x64.msi /qn /l*v install.log`. `install.ps1` and
   `uninstall.ps1` are an older scripted alternative; don't mix them with the `.msi`.

## Use

Pick **"TC001 Max Thermal Webcam"** as the camera in your app. In OBS, add a *Video Capture
Device* at 640x480.

- The first picture takes about 4 s while the camera starts.
- About once a minute the shutter clicks while the camera recalibrates, and the picture freezes
  for about half a second.
- A brief dimmed *CALIBRATING* badge means the USB stream stalled for a moment and is recovering.
  This is normal.
- Only one program can read the camera over USB at a time. If another program has it, the
  picture shows a *Camera in use by another program* card, and the webcam retries every 3 s.

## Settings

The settings are in `C:\ProgramData\TC001MaxWebcam\settings.json`. Changes apply within about a
second, even while streaming.

**The tray icon** is the easy way. Click the thermal icon in the notification area (it may be
under the ^ arrow). A small window opens above it and stays open while you change several
settings; click elsewhere or press Esc to close it. It has:

- the overlay switches: the whole overlay, the centre crosshair, the hot-spot marker, the
  temperature readouts (top bar) and the temperature scale (bottom bar);
- colour palette (a preview of each) and the custom palette's colours: click a colour to change
  it with Windows' colour picker, or use + / − to add or remove one (2 to 5, cold to hot);
- colour range and units;
- rotate, and mirror left-right or top-bottom;
- the automatic shutter;
- *Edit settings file* (for the manual range and the custom palette), *Open log* and *Exit*.

Right-click the icon for a short menu with the same last three. The window follows Windows' light
or dark mode, and the icon takes the colours of the current palette. If you exit it, start it
again from *Start menu > TC001 Max Thermal Webcam settings*; it also starts at every sign-in.

You can also use the command-line tool, which validates every value, or edit the file by hand.

```powershell
TC001MaxWebcam settings                                  # show the current settings
TC001MaxWebcam settings set rotate=180 mirror_horizontal=true
TC001MaxWebcam settings set units=F palette=iron
TC001MaxWebcam settings set range=manual range_min=20 range_max=45
TC001MaxWebcam settings set overlay=false                # the bare thermal picture
TC001MaxWebcam settings set crosshair=false scale=false
TC001MaxWebcam settings reset
```

| Key | Values | Default |
|---|---|---|
| `rotate` | `0`, `90`, `180`, `270`: clockwise, before mirroring; 90 and 270 are letterboxed | `0` |
| `mirror_horizontal`, `mirror_vertical` | `true`, `false` | `false` |
| `units` | `C`, `F` | `C` |
| `palette` | `inferno`, `iron`, `rainbow` (Google's turbo), `grey` (white hot), `black_hot`, `custom` | `inferno` |
| `custom_palette` | 2-5 colours `#rrggbb`, cold to hot | `#000040,#00a0ff,#ffff00,#ff0000` |
| `range` | `auto`: follows the scene, widens at once and narrows over a few seconds. `locked`: keeps the first frame's auto range. `manual`: uses `range_min`..`range_max`. | `auto` |
| `range_min`, `range_max` | numbers in `units`, within -100..600 °C | `20`, `40` |
| `auto_shutter` | `on`, `off`. `off` stops the click, but readings drift and fixed-pattern noise grows over time. | `on` |
| `idle_auto_shutter` | `off`, `on`, `unchanged`: what the shutter is left at when the camera closes | `off` |
| `overlay` | `true`, `false`: `false` hides every mark and bar below (status badges still show) | `true` |
| `crosshair` | `true`, `false`: the small x at the centre | `true` |
| `hotspot` | `true`, `false`: the green + on the hottest pixel, with its temperature | `true` |
| `readouts` | `true`, `false`: the top bar, `MAX` / `CENTRE` | `true` |
| `scale` | `true`, `false`: the bottom bar, the colour scale, range mode and time | `true` |

The `auto_shutter` command was found by experiment (`experiments/LOG.md`). TOPDON does not
document it.

## Update and uninstall

- **Update:** download the newer `.msi` from
  [Releases](https://github.com/MMImodular/TC001-Max-Webcam/releases) and double-click it.
  Or, after a new build or download into the same folder, use *Start menu > Update TC001 Max
  Thermal Webcam*, or run `TC001MaxWebcam update`. It installs the newest `.msi` in the folder the
  current version was installed from:
  - it waits until no app is streaming, then asks once for admin rights;
  - `--from <folder>` looks in another folder;
  - `--check` only reports whether an update is available.
- **Uninstall:** Settings > Apps > Installed apps > *TC001 Max Thermal Webcam*. It keeps
  `settings.json` and does not touch the WinUSB driver.

## Troubleshooting

Start with `TC001MaxWebcam status`. It is read-only and safe to run while streaming. It shows:

- whether the media source is registered;
- the settings, and any problems with them;
- the last lines of the log;
- whether the camera is present with WinUSB;
- the installed and available versions.

The log is `C:\ProgramData\TC001MaxWebcam\webcam.log`.

| Symptom | Fix |
|---|---|
| The camera is not listed in apps | Run `status`. If it says "NOT registered", repair the install from Installed apps. |
| *No thermal camera: not found* | Plug the camera in; Device Manager should list it. |
| *No thermal camera: no WinUSB driver* | Do install step 1 (Zadig, interface 0). |
| *Camera in use by another program* | Close the other program that reads the camera (the TOPDON app, a script). |
| The installer says files are in use | Close camera apps, or restart Windows after installing. |
| Black picture in one app | Try the Windows Camera app first. The camera offers 640x480 RGB32 and NV12. |

## Known limitations

- **Colour video only.** Apps get a colour picture; per-pixel temperatures are not available
  through the webcam.
- Fixed 640 x 480 at 25 fps. No audio, and no camera controls inside apps.
- The camera's visible-light picture is not decoded.
- x64 only, and needs the system-wide .NET 10 runtime.

## Repository layout

| Path | What |
|---|---|
| `src/TC001MaxWebcam.Source/` | The media source DLL. `Core/` is pure code shared with the self-test: protocol, reader state machine, settings, palettes, renderer and update rules. `Usb/` holds the WinUSB P/Invoke. The rest is the Media Foundation plumbing adapted from VCamNetSample. |
| `src/TC001MaxWebcam/` | `TC001MaxWebcam.exe`: `install`, `uninstall`, `status`, `run`, `settings`, `update`. |
| `src/TC001MaxWebcam.Tray/` | `TC001MaxWebcamTray.exe`: the tray icon and its settings window (plain Win32, no extra runtime). |
| `installer/` | The WiX v5 `.msi` package. |
| `tests/` | The console self-test, and real camera captures used as fixtures. |
| `docs/` | `PROTOCOL.md` (the USB protocol, with sources), `STATE_OF_THE_ART.md`. |
| `experiments/` | The Python experiments behind the auto-shutter and stall findings, with their logs. Not shipped. |
| `build.ps1`, `install.ps1`, `uninstall.ps1` | The build script, and the scripted install and uninstall. |
| `.github/workflows/` | GitHub Actions: `build.yml` builds and tests every push; `release.yml` (run by hand) publishes a release with the `.msi`. |
| `CHANGELOG.md` | What changed in each version; each section becomes that release's notes. |
