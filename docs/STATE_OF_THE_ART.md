# TOPDON TC001 Max: state of the art before this repository (as of 2026-11-20)

What was publicly known about the TOPDON TC001 Max, its vendor software and the open-source code
that talks to it or to its relatives, before the work in this repository. Every claim has a link.
**Documented** means a source says it; **inferred** means it follows from sources or from
read-only checks but nobody documents it. Links were checked on 2026-11-20.

## 1. The device

- **What it is.** A USB-C thermal camera for phones and PCs with a 256 x 192 thermal sensor and a
  second, visible-light camera, launched by TOPDON USA in February 2026 (documented:
  [TOPDON USA press release, via Amerisurv](https://amerisurv.com/2026/02/10/topdon-usas-new-tc001-max-blends-high-resolution-thermal-and-visual-imaging-for-clearer-actionable-images/),
  [TOPDON product page](https://www.topdon.com/products/tc001-max),
  [user manual](https://web-file.topdon.com/topdon-web/information_download/TC001-Max-User-Manual-EN.pdf)).
  TOPDON does not publish the USB protocol or the accuracy of its temperature readings for this
  model ([computingforgeeks comparison](https://computingforgeeks.com/best-thermal-camera/):
  accuracy "not published").
- **USB identity.** `3474:4962`, strings `TOPDON TECHNOLOGY Co., Ltd.` / `TC001 Max`, two
  vendor-specific interfaces (commands on ep0, a bulk IN stream on interface 1 alt 1). It is **not
  a UVC webcam**: Windows and Linux camera stacks cannot use it without a userspace driver
  (documented: [WendyOS PR #2621](https://github.com/wendylabsinc/WendyOS/pull/2621) `doc.go`).
  The vendor id 0x3474 shows up in `lsusb` as "Raysentek Co.,Ltd" for another TOPDON camera
  (documented for the TC004 Mini: [libmtp bug #1950](https://sourceforge.net/p/libmtp/bugs/1950));
  that it is the same company for the TC001 Max is inferred.
- **How it differs from the original TC001.** The original TC001 is a UVC camera that sends a
  256 x 384 frame (picture on top, temperatures below) which OpenCV can read directly (documented:
  [leswright1977/PyThermalCamera](https://github.com/leswright1977/PyThermalCamera)). The
  TC001 Max uses the vendor protocol of the Thermal Master P-series instead, with the thermal
  block (picture, temperatures, metadata row) floating inside larger composite frames that also
  carry the visible camera (documented: WendyOS PR #2621 `parser.go` description).
- **How it relates to the Thermal Master P3 / P1.** Same command layout (18-byte commands with
  CRC-16, `0x41/0x20` control transfers), same start sequence, same 386-row thermal block and the
  same 1/64 K temperature scale as the P3 (`3474:45A2`) and P1 (`3474:45C2`) (documented:
  [jvdillon/p3-ir-camera P3_PROTOCOL.md](https://github.com/jvdillon/p3-ir-camera/blob/main/P3_PROTOCOL.md);
  for the TC001 Max: WendyOS PR #2621). The device-type table of the Thermal Master app puts
  "TC001 Max" in the same type as the P3, P1, Omni One and AC001 (documented:
  [xaionaro-go/thermalmaster `device_type.go`](https://github.com/xaionaro-go/thermalmaster/blob/main/pkg/thermalmaster/device_type.go)).
  Unlike the P3, the TC001 Max's stream multiplexes the visible camera, so P3 code that reads one
  fixed-size frame per transfer does not work on it (documented: WendyOS PR #2621).

### Chips inside

- **No teardown and no FCC internal photos were found.** TOPDON's FCC grantee code (2AVYW) lists
  several thermal cameras but no TC001 / TC001 Max filing
  ([fccid.io/2AVYW](https://fccid.io/2AVYW)); a USB-only device without a radio may not need one.
  No forum teardown was found ([EEVblog thermal imaging forum](https://www.eevblog.com/forum/thermal-imaging/)
  searched for TC001 Max, P2L and P3 teardowns).
- **What the camera says about itself** (read-only register reads on one TC001 Max, firmware
  `1.01.000`, 2026-11-20; see `experiments/LOG.md`): part-number register 0x06 = `P2L-D`,
  hardware-version register 0x0A = `DVS-00.01`; its firmware log names the modules `std cmd`,
  `EXT_CMD`, `shutter`, `ffc_svc` and `isp` and prints a chip id. The register map itself is documented
  for the P3 (P3_PROTOCOL.md "Register Map").
- **Inferred: an InfiRay "AC020"-family core, the same class as the Thermal Master P2L.** The
  Thermal Master app's native libraries use one "AC020" SDK and command library for the X3, X2
  Pro, X2L, X15, X919, P1, **P2L**, P3 and P4 (documented by static analysis of the app:
  [thermalmaster `doc/reverse-engineering.md`](https://github.com/xaionaro-go/thermalmaster/blob/main/doc/reverse-engineering.md)).
  The TC001 Max reports part number `P2L-D` and answers that library's command set (section 4),
  so it is most likely a P2L-class module in a TOPDON housing. InfiRay publishes no AC020
  datasheet or SDK documentation that could be found; a developer using InfiRay's "AC020 Android
  SDK 1.1.5" for the Tiny2C reports temperature, FFC and emissivity working through it
  ([delphicchen/MAG160_ThermalCam release notes](https://github.com/delphicchen/MAG160_ThermalCam/releases/tag/android-v2.7-alpha1)).
- **The original TC001** is said to use an InfiRay Tiny1 core, like the InfiRay P2
  (a forum recollection, not a teardown:
  [EEVblog "New Software For InfiRay, HTI, UNI-T & TOPDON"](https://www.eevblog.com/forum/thermal-imaging/new-software-for-infiray-hti-uni-t-topdon-series-thermal-cameras/)).
  The Tiny1-C's own SDK (InfiRay "ASIC 2121W", command header `falcon_cmd.h`) is published in
  [danjulio/iCam](https://github.com/danjulio/iCam/tree/main/Tiny1C_documentation); it is a
  different command set and transport from the AC020's.

## 2. Vendor software

- **TopInfrared** (TOPDON; Android `com.topdon.topInfrared`, iOS, per
  [App Store](https://apps.apple.com/us/app/topinfrared/id6452017744) and
  [Similarweb](https://similarweb.com/app/google/com.topdon.topInfrared)) and TOPDON's PC
  software. The TopInfrared manual documents an **Auto Shutter** icon that switches the automatic
  shutter on and off ([TopInfrared user manual](https://web-file.topdon.com/topdon-web/information_download/TopInfrared-User-Manual.pdf));
  TOPDON's FAQ explains the clicking as the automatic shutter and gives the menu path for the
  TC001 / TC002 ([TOPDON TC001/TC002 FAQ](https://service.topdon.com/portal/en/kb/articles/tc001-tc002-faq-10-4-2024)).
  That the same toggle exists for the TC001 Max is inferred (same app).
- **Thermal Master app** (for the P-series): its image settings can disable the automatic
  shutter; it also offers a manual shutter, burn protection, emissivity profiles and temperature
  units (documented for the P3: [Gough Lui's P3 review](https://goughlui.com/2025/10/07/review-thermal-master-p3-android-ios-windows-usb-c-thermal-camera-256x192-25hz-35mk/)).
  Range (gain) switching is offered by the vendor app and triggers a shutter afterwards
  (documented by [OpenRMH-IRCAM PR #2](https://github.com/Cyclotronic/OpenRMH-IRCAM/pull/2)).
- **Windows drivers.** The vendor PC software for the P-series appears to use WinUSB / libusbK
  (documented for the P3 in Gough Lui's review). Open-source programs on Windows need WinUSB
  bound to the camera, usually with [Zadig](https://zadig.akeo.ie/) (documented:
  [ferrolho/thermal-master-viewer](https://github.com/ferrolho/thermal-master-viewer)).

## 3. Open-source projects

| Project | Device(s) | Platform / transport | Licence | Supports | Maturity (2026-11-20) |
|---|---|---|---|---|---|
| [WendyOS PR #2621](https://github.com/wendylabsinc/WendyOS/pull/2621) (`go/internal/agent/usbthermal/`) | **TC001 Max** `3474:4962` | Linux, usbfs ioctls, Go | repo Apache-2.0 | start sequence, stream framing (composite frames, floating thermal blocks, spliced headers), raw temperatures | open draft PR (Oct-Nov 2026); author: driver "has not run against the camera yet", tested on captured bytes. No shutter, gain or emissivity control. |
| [jvdillon/p3-ir-camera](https://github.com/jvdillon/p3-ir-camera) | Thermal Master P3, P1 | Python, PyUSB / libusb | Apache-2.0 | protocol write-up (P3_PROTOCOL.md), viewer, register reads, gain high/low, manual shutter, firmware log via the status register | active (pushed Nov 2026), ~100 stars; the reference everyone cites. No auto-shutter control. |
| [xaionaro-go/thermalmaster](https://github.com/xaionaro-go/thermalmaster) | P3, P1 (type 15 includes TC001 Max, untested) | Go, libusb; Linux v4l2loopback output | CC0-1.0 | streaming, PNG capture, v4l2loopback, colormaps; a large command table recovered from the vendor app (palette, gain, **FFC/shutter including `SetAutoFFCStatus`**, emissivity, ...) | pushed Sep 2026, few stars. The README does not say which commands were tried on a camera; the zoom commands are documented as accepted but ignored. Its CLI only uses the manual shutter. |
| [ferrolho/thermal-master-viewer](https://github.com/ferrolho/thermal-master-viewer) | P3 (P1 untested) | WebUSB in Chrome/Edge (Windows needs WinUSB via Zadig) | Apache-2.0 | live view, temperatures, palettes, emissivity, export, manual shutter, gain, firmware log | small (8 commits, Aug 2026). No auto-shutter setting. |
| [Cyclotronic/OpenRMH-IRCAM PR #2](https://github.com/Cyclotronic/OpenRMH-IRCAM/pull/2) | P3 | Windows, WinUSB, C++ | see repo | range switching, shutter trigger | pull request. |
| [leswright1977/PyThermalCamera](https://github.com/leswright1977/PyThermalCamera) | original TC001 (UVC) | Linux / Raspberry Pi, OpenCV | Apache-2.0 | view, temperatures, recording | popular (330 stars), last push 2023. Does not apply to the TC001 Max. |
| [92es/Thermal-Camera-Redux](https://github.com/92es/Thermal-Camera-Redux) | original TC001 and clones | OpenCV | see repo | live and offline thermal data | does not apply to the TC001 Max. |
| [LeoDJ/P2Pro-Viewer](https://github.com/LeoDJ/P2Pro-Viewer), [stanelie/p2-viewer](https://github.com/stanelie/p2-viewer) | InfiRay P2 Pro (Tiny1-C, UVC extension unit) | Python / ESP32 | MIT / see repo | viewer, commands incl. auto-shutter on/off for the P2 Pro | different chip and command transport; useful only as InfiRay naming context. |

No project offered a Windows webcam (virtual camera) for the TC001 Max, and only WendyOS
targeted the TC001 Max itself.

## 4. Protocol knowledge, and what was unknown

Covered by the sources above (documented): the control transfers, the 18-byte command layout and
CRC, register reads (model, firmware, part number, serial, hardware), start_stream and the start
sequence, the stream layout of the TC001 Max (WendyOS), the 1/64 K temperature scale, the P3's
manual shutter command (`01364300...cd0b`), the P3's gain commands, the firmware log in the
status register, and that the P3 fires its shutter about every 90 s on its own (P3_PROTOCOL.md).

Unknown or unverified before 2026-11-20:

- **Auto-shutter control.** The vendor apps can switch it off (documented), but no open-source
  project used or tested a command for it on any P-series camera; thermalmaster lists
  `SetAutoFFCStatus` (VDCMD 0x410210) from the app's command library without a test. Whether such
  a switch is volatile was undocumented.
- **A NUC / shutter flag in the metadata row.** None documented (P3_PROTOCOL.md calls rows
  192-193 "metadata" only; WendyOS only uses the frame counter).
- **Stream stalls.** That bulk IN 0x81 sometimes answers with a STALL and how to recover: not
  documented anywhere (WendyOS ends the session on any read error).
- **Gain behaviour on the TC001 Max** (which gain it starts in, whether the P3 commands apply).
- **Absolute accuracy and emissivity handling.** No published accuracy; the camera's built-in
  emissivity assumption is undocumented (viewers apply their own correction in software).
- **Windows.** Only WebUSB / WinUSB via Zadig was known to work for the P3; nothing on whether a
  Windows service (the camera Frame Server) may open a Zadig-bound device.

## 5. Firmware and vendor-software reverse engineering

- Static analysis of the Thermal Master Android app's native libraries: device-type table, the
  "AC020" command library's JNI table (156+ commands), and the VDCMD id to wire-format mapping
  ([thermalmaster `doc/reverse-engineering.md`](https://github.com/xaionaro-go/thermalmaster/blob/main/doc/reverse-engineering.md)).
- USB captures of the vendor Windows tool for the P3 (the pre-computed commands in P3_PROTOCOL.md;
  the [Awesome-REA issue #2264](https://github.com/reacher-z/Awesome-REA/issues/2264) summarises
  them).
- For the P2 Pro (a different chip), opcodes recovered from the Android app's `libircmd.so`
  ([stanelie/p2-viewer](https://github.com/stanelie/p2-viewer)).
- No public firmware dump or disassembly of the TC001 Max / AC020 firmware was found.

## 6. What this repository adds (2026-11-20)

- A **Windows 11 virtual camera** (Media Foundation, runs in the Camera Frame Server) for the
  TC001 Max, with its own WinUSB reader (SetupAPI + WinUSB P/Invoke, no libusb), false-colour
  rendering, hot-spot and temperature overlay, and a validated `settings.json`.
- **Stall recovery measured on the real camera:** the occasional STALL on bulk IN 0x81 is cleared
  with ClearFeature(ENDPOINT_HALT) in 0.05 s or less and the stream continues without a
  reconnect. The stalls are **not** the shutter (they also occur with the automatic shutter off);
  their cause is unknown.
- **The core's identity** read from the camera (part number `P2L-D`, hardware `DVS-00.01`) and the
  firmware log used as an objective shutter indicator, together with a frame-content one (the
  camera repeats its last frame for about 0.6 s while the shutter is closed).
- **The automatic shutter can be switched off and on** with the AC020 library's
  `SetAutoFFCStatus` (`10 02 41 00 <0|1> 00 ...`) and read with `GetAutoFFCStatus`: measured
  on one TC001 Max (no automatic shutter in 660 s with it off, against one every 60.6 s with it
  on; the stream unaffected). The switch survives a USB close / reopen; whether a power cycle
  resets it was not tested. No interval control was found. Details: `experiments/LOG.md`,
  `docs/PROTOCOL.md` "Automatic shutter".
- An opt-in `auto_shutter=off` setting in the webcam, which restores the camera's default when
  the stream stops.
