# Changelog

Each `## <version>` section becomes the notes of that GitHub release
(`.github/workflows/release.yml`). Add the section before running the release.

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
