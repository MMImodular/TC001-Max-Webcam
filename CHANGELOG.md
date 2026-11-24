# Changelog

Each `## <version>` section becomes the notes of that GitHub release
(`.github/workflows/release.yml`). Add the section before running the release.

## 1.5.0

- **Updates from the tray icon.** Once a day the tray icon asks GitHub for the latest release.
  When a newer one is out, Windows shows a notification and the settings window gets an
  *Update* button (new section Updates; the right-click menu has it too). One click downloads the
  `.msi`, checks its size and the SHA-256 that GitHub lists, and installs it through
  `TC001MaxWebcam update`. That still asks once for administrator rights: the webcam runs inside
  a Windows service, so an install for one user only is not possible.
- *Check now* in the settings window, and *Check for updates* in the right-click menu.
- New setting `update_check` (default `true`) to turn the daily check off. The check sends only the
  request; the tray writes what it did to `%LOCALAPPDATA%\TC001MaxWebcam\tray.log`.
- `TC001MaxWebcam update --no-remember`: install from `--from` without keeping it as the update source.
- Updates work from 1.5.0 on, so 1.5.0 itself is installed by hand. Each new version reaches
  installed PCs once its GitHub release is published (Actions > Release > Run workflow).

## 1.4.0

- **Denoise** (off by default). A switch in the settings window under Camera, or
  `settings set denoise=true`. It works on the temperatures, so the readouts are steadier too:
  - a motion-adaptive temporal filter: still areas are averaged over about 16 frames, while
    anything that changes by more than about 0.1 K passes straight through, so moving edges stay
    sharp;
  - whole-frame flicker removal: the small level jumps that shift every pixel at once;
  - shutter-table averaging: each shutter click stores a new fixed-pattern table with about
    30 mK of random error. The filter measures each click's change over the next second and
    averages the last four tables, so the pattern no longer reshuffles at every click. Clicks
    while the scene moves are skipped.
  Measured with this code on recordings (an even plate, 232 shutters, and a moving scene):
  noise on still pixels from 31 mK to 7 mK, whole-frame flicker from 13 mK to 0.4 mK, and the
  fine fixed pattern from 39 mK to 28 mK. Moving pixels trail the camera by a median of 10 mK.
  It costs about 0.6 ms of CPU a frame. The prototype is in `experiments/denoise/`.
  Own code; the temporal filter follows Dubois and Sabri (1984) and Brailean et al. (1995), and
  the shutter and flicker stages come from this project's noise study. Full credits are in the
  README under Sources and attribution, and in `THIRD_PARTY_NOTICES.txt`.

## 1.3.4

- **Fixed: the picture sometimes jumped sideways for one frame** (about every 10 s). When the
  USB stream's 12-byte frame header landed in the last bytes of a thermal block, the block was
  read up to 20 bytes too early: every pixel moved left by up to 10, and the first pixels read
  about 240 degC (the "picture leak" that 1.2.0 patched over). With the header elsewhere in that
  row, the frame was dropped. Both are fixed in the block finder, with a test that puts a header
  at every position in a block.
- Noise study tooling in `experiments/noise/` and `docs/NOISE_AND_CALIBRATION.md` (not shipped).

## 1.3.3

- **Auto range speed.** Choose how fast the auto colour range tightens after something hot
  leaves the picture: Slow (about 25 s), Normal (about 12 s, as before), Fast (about 3 s) or
  Instant (the next frame). It always widens at once. In the settings window under Range, or
  `TC001MaxWebcam settings set range_speed=fast`.

## 1.3.2

- **Tray icon with a settings window.** Click the thermal icon by the clock to open a small
  window that stays open while you change settings. Every change shows in the live picture
  within about a second. It follows Windows' light or dark mode. Right-click the icon for a
  short menu. It starts at every sign-in.
- **Overlay switches.** Turn off the whole overlay for a bare thermal picture, or just the
  centre crosshair, the hot-spot marker, the temperature readouts (top bar) or the temperature
  scale (bottom bar). New settings: `overlay`, `crosshair`, `hotspot`, `readouts`, `scale`.
- **Palettes match labbench.** Iron uses labbench's colours, rainbow is now Google's turbo, and
  there are white-hot and new black-hot palettes. The custom palette's default colours are
  labbench's.
- **Custom palette colours** can be picked in the settings window (2 to 5 colours, cold to hot).
- The updater closes the tray icon before installing and starts it again afterwards.

## 1.2.0

- Repair for a camera glitch ("picture leak") that read about 240 °C and upset the auto range.
- One-click updater: `TC001MaxWebcam update` and a Start menu shortcut.

## 1.1.0

- Native `.msi` installer.
- Takes turns with labbench on the camera (camera claims).

## 1.0

- The TOPDON TC001 Max as a Windows 11 virtual camera.
