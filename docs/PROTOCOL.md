# TOPDON TC001 Max: USB protocol reference

The code cites this file as **[REF]** (`Tc001MaxProtocol.cs`, `CameraSession.cs`,
`WinUsbLink.cs`). TOPDON publishes no protocol documentation. Everything here comes from
community reverse engineering (sources below) and from read-only checks on one TC001 Max
(firmware 1.01.000) on 2026-11-20, marked **Verified here**. Anything else is marked
**UNVERIFIED**. The content was carried over from the author's earlier Python driver, which
the C# code ports.

## Sources and licences

| Source | What it gives | Licence |
|---|---|---|
| [WendyOS PR #2621](https://github.com/wendylabsinc/WendyOS/pull/2621) (`go/internal/agent/usbthermal/`: `protocol.go`, `parser.go`, `frame.go`, `doc.go`) | A Go driver for exactly 3474:4962. Start sequence checked on a TC001 Max. Stream layout measured from a 3.5 MB capture: composite frames, floating thermal blocks, end row, splicing of straddling headers, plausibility limits. | Apache-2.0, (c) Wendy Labs |
| [jvdillon/p3-ir-camera](https://github.com/jvdillon/p3-ir-camera) `P3_PROTOCOL.md`, `p3_camera.py` | The protocol for the Thermal Master P3 (3474:45A2) and P1 (3474:45C2): control requests, 18-byte commands with pre-computed CRCs (our test vectors), register map, start sequence, 1/64 K scale, gain modes. | Apache-2.0 |
| [xaionaro-go/thermalmaster](https://github.com/xaionaro-go/thermalmaster) `pkg/thermalmaster/device_type.go`, `command.go`, `command_ffc.go`, `shutter.go`, `doc/reverse-engineering.md` | Its device-type table puts "TC001 Max" in type 15 with the P3, P1, Omni One and AC001. The AC020 command library's ids (from the Thermal Master app), among them the auto-FFC switch used here, and the VDCMD id to wire-byte mapping. | CC0-1.0 |
| InfiRay Tiny1-C SDK (`ASIC_2121W_RTOS_SDK_V1.0.0`, `VDCMD/falcon_cmd.h`), published in [danjulio/iCam](https://github.com/danjulio/iCam/tree/main/Tiny1C_documentation) | Another InfiRay chip: its auto-shutter parameters are RAM settings until an explicit save to flash. Reference for conventions only; none of its commands are used. | vendor SDK, read only |
| [leswright1977/PyThermalCamera](https://github.com/leswright1977/PyThermalCamera) | The original TC001, a UVC camera with a 256x384 frame: picture on top, temperatures below. The first 384 rows of a TC001 Max block use the same layout. | see repo |

The block finder (`BlockParser` in `Tc001MaxProtocol.cs`) is **adapted from WendyOS
`parser.go`**: it was translated from Go to Python (the author's earlier driver) and then to C#,
and keeps that file's structure (the same steps, constants, statistics and control flow). Our
changes: no header/phase counting; added skipping of the first block, a 600 degC plausibility
ceiling, a histogram median and the picture-leak repair. WendyOS is Copyright Wendy Labs,
Apache License 2.0. The P3 command layout and start sequence are from p3-ir-camera (Copyright
jvdillon and contributors, Apache License 2.0); the commands' bytes and CRCs were re-derived and
checked against its test vectors. The test fixtures in `tests/fixtures/` are our own captures.

## Identity and USB layout

| Item | Value | Status |
|---|---|---|
| VID:PID | `3474:4962` (Yantai IRay / Raytron, InfiRay core) | Verified here |
| USB strings | manufacturer `TOPDON TECHNOLOGY Co., Ltd.`, product `TC001 Max` | Verified here |
| Model register 0x01 (30 bytes) | `TC001 Max` | Verified here |
| Firmware register 0x02 (12 bytes) | `1.01.000` | Verified here (P3 register map) |
| Serial register 0x07 (64 bytes) | a 12-digit number | Read here. Whether it is unique per unit is unknown. |
| Interfaces | 0: vendor 0xFF/0xF0 (commands go over ep0). 1: alt 0 idle, alt 1 = bulk IN 0x81 stream. One interface association. | WendyOS `doc.go`. Claiming both worked here. |
| Windows | One composite function, `USB\VID_3474&PID_4962&MI_00`. WinUSB bound to it with Zadig. libusb (and `WinUsbLink.cs`) then opens interface 0 and, through it, interface 1. | Verified here (with libusb) |
| USB speed | High-Speed (USB 2.0) was enough: 34.5-36.2 MB/s measured. | Verified here |
| Part number register 0x06 (64 bytes) | `P2L-D` | Read here (2026-11-20, P3 register map `read_part_number`) |
| Hardware register 0x0A (64 bytes) | `DVS-00.01` | Read here (`read_hw_version`) |
| Register 0x0F (64 bytes) | 4 binary bytes (the chip id the firmware log prints, little-endian) + `00.01` | Read here (`read_model_long`) |

## Hardware

No teardown, datasheet or FCC internal photos of the TC001 Max could be found (TOPDON's FCC grantee
code 2AVYW lists no TC001 filing; details and links in `docs/STATE_OF_THE_ART.md`). What is known:

- **Thermal core: most likely an InfiRay "AC020"-family module of the Thermal Master P2L class**
  (inferred). The camera's part-number register reads `P2L-D`, and it answers the AC020 command
  library's commands (below). The Thermal Master app uses one "AC020" SDK and command library
  for the X3, X2 Pro, X2L, X15, X919, P1, P2L, P3 and P4 (xaionaro-go/thermalmaster
  `doc/reverse-engineering.md`, from static analysis of the app). Its device-type table puts the
  TC001 Max with the P3 (thermalmaster `device_type.go`). No public AC020 documentation exists.
- **Firmware** `1.01.000`, hardware `DVS-00.01`. Its log (the extended status read below) names
  the modules `std cmd` (commands of type `01 xx`), `EXT_CMD` (type `10 xx`), `shutter` and
  `ffc_svc`, and prints a chip id at start-up.
- **USB vendor id 0x3474** is listed by `lsusb` as "Raysentek Co.,Ltd" for another TOPDON camera
  (libmtp bug #1950); not confirmed for this one.
- The visible-light camera, the USB bridge and the sensor part number are not identified.

Unlike the original TC001 (a UVC camera, said to use an InfiRay Tiny1 core), the TC001 Max is a
vendor-class device. The Tiny1-C's published SDK (`falcon_cmd.h` in danjulio/iCam) is a different
chip and command set; it was only used as a reference for InfiRay's conventions (auto-shutter
parameters live in RAM until an explicit "save config to flash").

### Firmware log (extended status read)

Reading more than one byte with the busy-status request (`0xC1/0x22`, 128 bytes) returns the
camera's latest log line from byte 64 on (P3_PROTOCOL.md "Extended Status Register"; verified
here). With the automatic shutter on, every ~60 s it shows `I/shutter: shutter close` (or
`shutter open`) followed by `I/ffc_svc: ffc b update done`. It is the only objective shutter
indicator found: frames keep arriving during a shutter (no gap in the stream), and the stalls
below are not tied to it.

## Commands (control transfers)

| Step | bmRequestType | bRequest | wValue | wIndex | Data |
|---|---|---|---|---|---|
| Send command | 0x41 | 0x20 | 0 | 0 | 18-byte command |
| Busy status | 0xC1 | 0x22 | 0 | 0 | 1 byte IN. Bit 0 = busy. Seen here: 0x02 after a write, 0x03 after a read. |
| Read response | 0xC1 | 0x21 | 0 | 0 | n bytes IN |
| Enable stream | 0x40 | 0xEE | 0 | 1 | none |

18-byte command: `01`, command byte (`01` read register, `2f` stream control), uint16 LE
parameter (`0x0081`), uint16 LE register, 6 zero bytes, uint16 LE response length, 2 zero
bytes, uint16 LE CRC-16/XMODEM (polynomial 0x1021, init 0) over bytes 0..15. The P3 notes say
the camera does not check the CRC; we send the correct one anyway.

| Command | Bytes | Status |
|---|---|---|
| read model (reg 0x01, 30 bytes) | `0101810001000000000000001e0000004f90` | Verified here and by WendyOS |
| read firmware (reg 0x02, 12 bytes) | `0101810002000000000000000c0000001f63` | Verified here |
| read serial (reg 0x07, 64 bytes) | `01018100070000000000000040000000104c` | Verified here |
| start_stream (1-byte reply) | `012f81000000000000000000010000004930` | Verified here. Reply `0x35` on the first call (the camera was already acquiring), `0x01` on the second. |
| gain high / low | `012f41000100000000000000000000004939` / ... | **UNVERIFIED** on the TC001 Max (P3 only). Never sent by this code. |
| shutter / NUC (manual) | `01364300000000000000000000000000cd0b` | P3_PROTOCOL.md. Experimentally determined 2026-11-20: one shutter cycle (`ffc b update done` in the firmware log). Defined in the code, not sent by it. |
| GetAutoFFCStatus (1-byte reply) | `100281000100000000000000010000000d37` | See "Automatic shutter". Experimentally determined 2026-11-20: `01` = on (default), `00` = off. |
| SetAutoFFCStatus off / on | `100241000000000000000000000000000d3e` / `10024100010000000000000000000000783d` | See "Automatic shutter". Experimentally determined 2026-11-20. Sent only with `auto_shutter=off`. |

Commands of type `10 xx` (first byte 0x10) are what the firmware log calls `EXT_CMD`; xaionaro-go/thermalmaster
writes them as 3-byte "VDCMD" ids `{param, type1, type0}` (`command.go` `BuildVDCMD`: wire bytes
`type0 type1 param 00 ...`), e.g. 0x410210 = `10 02 41 00`. The commands above are 0x810101 and
0x812F01 in that notation.

**Start sequence**, used on every (re)connect (WendyOS `protocol.go` `start`, P3_PROTOCOL.md
steps 3-8): claim interfaces 0 and 1. Read the model (and, best effort, firmware and serial).
Send start_stream. Wait 1 s. Set interface 1 to alt 1. Send vendor request 0xEE (wIndex 1).
Wait 2 s. Do one throwaway 16 KiB bulk read (a timeout is normal). Send start_stream again.
Connecting took 3.8-4.2 s here.
**Stop**: set interface 1 back to alt 0, release both interfaces. This runs on every exit path.

## Stream layout

Bulk IN 0x81, read in 16 KiB pieces. Two streams are multiplexed: the visible camera and the
thermal core.

- A 12-byte UVC-style header (`0c 8c..8f`, uint32 presentation time = 0, uint32 source clock,
  uint16 USB frame number < 2048) starts a **composite frame**. Verified here: headers 1,777,664
  bytes apart, as WendyOS found.
- **Thermal blocks** float between the headers, at offsets that change from frame to frame.
  Each block is 386 rows of 512 bytes (197,632 bytes):
  - rows 0-191: grey picture, 256x192 YUYV. Every chroma byte is 0x80; the low byte is the
    camera's own AGC grey level. This is a thermal picture, not visible light.
  - rows 192-383: temperatures, 256x192 uint16 LE, in 1/64 K.
  - row 384: metadata. Word 64 is the frame counter. The row ends in a run of `ff 00` pairs.
  - row 385: end row, `ff 00` x 256.
- A block can straddle a composite-frame boundary. The next header then sits **inside** the
  block and must be cut out (WendyOS: about 1 frame in 15; 10 % of blocks here).
- How it is found (`BlockParser`): look for the end row at the end of an `ff 00` run, take
  exactly one block length before it, and require a grey picture. A candidate 12 bytes longer
  is accepted only with exactly one header inside it, which is removed. Anything else is
  rejected. Resync happens by itself.

Measured here: 25.0 thermal frames per second. After a start, the first block is stale: it is
the camera's buffered frame from before the restart (frame counter jump of 53, about 2 s), so it
is dropped (`BlockParser(skipFirst: true)`).

## Automatic shutter

The camera closes its shutter about every 60 s on its own (measured: every 60.6 s; P3_PROTOCOL.md
says about 90 s for the P3) to refresh its per-pixel offsets (NUC / flat-field correction).
During a cycle the stream does not pause: the camera repeats the last frame for about 0.6 s
(15 identical frames) and its log shows `I/shutter: shutter close|open` and
`I/ffc_svc: ffc b update done`.

**Switching it off (experimentally determined 2026-11-20 on one TC001 Max, firmware 1.01.000;
`experiments/LOG.md`):**

| Command | Bytes | Effect measured |
|---|---|---|
| GetAutoFFCStatus | `100281000100000000000000010000000d37` | reply `01` by default |
| SetAutoFFCStatus(0) | `100241000000000000000000000000000d3e` | no automatic shutter for 660 s (11 expected cycles) by the firmware log, and none in the 270 s of it also checked by frame content; stream normal (25 fps). GetAutoFFCStatus then `00` |
| SetAutoFFCStatus(1) | `10024100010000000000000000000000783d` | one cycle at once, then every ~60 s again |

Source of the commands: xaionaro-go/thermalmaster `pkg/thermalmaster/command_ffc.go` and
`shutter.go` (`CmdSetAutoFFCStatus` 0x410210, `CmdGetAutoFFCStatus` 0x810210, value in the
register field), recovered from the Thermal Master app's AC020 command library. Neither TOPDON nor
InfiRay documents them.

- The switch **survives a USB close / reopen** (verified). The convention shared with labbench
  (since 2026-11-21): every program sets the switch to what it wants when it opens the camera,
  and leaves it at its idle setting when it closes it cleanly. The webcam switches it on at the
  start when it finds it off while `auto_shutter=on`, and at a clean close (the last app
  stopped, or it lends the camera to labbench) leaves it at `idle_auto_shutter` (default `off`,
  the same as labbench's `idle_auto_shutter`: the camera does not click while nothing streams).
  After a failed link nothing is sent (the next open sets it again).
- **UNVERIFIED: that unplugging the camera restores "on".** Believed, because the same command
  library has a separate save command (`SaveSystemParams` 0x511010, never sent) and InfiRay's
  Tiny1-C keeps its auto-shutter switch in RAM until `spi_config_save`.
- The manual shutter (`01364300...cd0b`) still works while it is off.
- **No interval control is known.** `GetAutoFFCCurrentParams` (`10028200010000000000000001000000aeba`)
  only mirrored the switch here.

## Stalls

**Seen here (2026-11-20):** while streaming, the bulk IN read on 0x81 sometimes fails with EPIPE
(`LIBUSB_ERROR_PIPE`, the endpoint halted): 2 in 330 s in one run, 5 in 390 s in another, none in
350 s in two others. Treating it as a disconnect cost about 7 s each.

**They are not the shutter** (measured later the same day, `experiments/LOG.md`): they happened
with the automatic shutter switched off, and with it on they did not coincide with shutter cycles
(14-17 s after them). The first reading ("at the moments the shutter clicks", from log times at
1 s resolution) was wrong. Their cause is unknown. The code still calls the state `calibrating`
and shows a "CALIBRATING (SHUTTER)" badge; the name is historical.

**Handling** (none of the sources documents this; our design):
1. Enter state `calibrating` (the picture shows a "CALIBRATING (SHUTTER)" badge).
2. Clear the halt: `ClearFeature(ENDPOINT_HALT)` on 0x81 (USB 2.0 spec 9.4.5), which is libusb
   `clear_halt` and `WinUsb_ResetPipe`. Then keep reading. The first block after the pause is
   dropped (as after a start).
3. If nothing arrives for 3 s, re-send start_stream once.
4. After 8 s, fall back to a full reconnect.
5. If more than 20 stalls come in a row without a frame, reconnect too.

**Reference behaviour, verified on the real camera on 2026-11-20 with the Python driver**
(libusb on the same WinUSB driver): the EPIPE on bulk IN 0x81 was cleared with `clear_halt` in
0.05 s and the stream continued, with no reconnect and no camera error (and again in the
shutter experiments: 8 stalls, each cleared in 0.000-0.015 s, about 2 frames lost each).
**UNVERIFIED:** the same recovery through the C# code (WinUSB reporting the stall as
`ERROR_GEN_FAILURE`, `WinUsb_ResetPipe` clearing it); steps 3-5 (tested against fakes only);
what causes the stalls.

## Temperature

`degC = raw / 64 - 273.15`, where raw 0 or 0xFFFF means no reading (WendyOS `frame.go`; P3
"Temperature Conversion"; the original TC001 uses the same scale). Every value seen here was a
multiple of 4 counts (1/16 K), as WendyOS also found.

**Validation** (`ValidateTemperatures`, after WendyOS `CheckTemperatures`): a frame is rejected
if any reading is below -100 degC or above 600 degC, if fewer than half the pixels have a
reading, or if the median is outside -40..150 degC.

**Picture leak** (`RepairPictureLeak`, measured 2026-11-21): now and then the first few to ~70
temperature words of a block (row 0 from x = 0) hold grey-picture words instead: luma in the low
byte, chroma 0x80 in the high byte, which read as 238.9..242.8 degC. The rest of the frame is
normal, so validation passes it. In labbench's 25 h AA characterization run on the owner's
camera (`thermal.csv`, one row per second) it showed up 43 times, about once every 25 minutes:
the hot spot jumped from ~25 degC to ~240 degC at (0..68, 0) for a single row. Unrepaired, the
webcam drew a 240 degC hot spot and its auto range widened to 240 degC, then took ~20 s to
narrow again (4 s time constant over a ~215 degC gap). The decoder now replaces each word of
that leading run with the reading straight below it (the next row past the run) and counts it
(the log says *repaired a frame* for the first three per stream, then every 100th). A run over
half the frame is rejected. Which byte offset the camera writes the picture from, and whether a
leak can start anywhere other than word 0, is not known: no raw leaked block has been kept yet.

**Accuracy is not verified.** In a room said to be about 20 degC the scene read a median of
26-28 degC. Check against a contact thermometer on a matte, high-emissivity surface (tape)
before trusting absolute values.

## Windows: reading the camera from the virtual camera

`Usb/WinUsbLink.cs` does with P/Invoke what libusb does on WinUSB: SetupAPI finds
`USB\VID_3474&PID_4962&MI_00\...`; its `DeviceInterfaceGUIDs` registry value (Zadig/libwdi
writes a random GUID per install, so it is read, not hard-coded) and
`CM_Get_Device_Interface_List` give the path; then `CreateFile` (overlapped), `WinUsb_Initialize`
(interface 0), `WinUsb_GetAssociatedInterface` (interface 1), `WinUsb_ControlTransfer`,
`WinUsb_SetCurrentAlternateSetting`, `WinUsb_ReadPipe` (bulk IN 0x81, 16 KiB,
`PIPE_TRANSFER_TIMEOUT` 1 s), `WinUsb_ResetPipe` (clears a stall) and `WinUsb_AbortPipe` (ends a
blocked read when the last app stops streaming). Win32 errors map as in libusb's WinUSB backend:
`ERROR_SEM_TIMEOUT` = timeout, `ERROR_GEN_FAILURE` = stall, `ERROR_OPERATION_ABORTED` =
cancelled.

**Can the Frame Server open a Zadig WinUSB device? (finding, 2026-11-20, UNVERIFIED)** The
media source runs inside the Windows Camera Frame Server, a service under **LOCAL SERVICE**
(Microsoft Learn, "Frame Server Custom Media Source"; `sc qc FrameServer` shows
`NT AUTHORITY\LocalService`, `sc qsidtype FrameServer` shows `UNRESTRICTED`). Whether it can
open the camera is decided by the device object's security descriptor:
- The Zadig/libwdi INF has no `Security` directive, and neither the device nor the setup class
  had a security override on the PC checked (`SPDRP_SECURITY_SDS` not set).
- Without an override, the device gets the default device-object descriptor: SYSTEM and
  Administrators full access, everyone else (World) read / write / execute (Microsoft Learn,
  "Controlling Device Access"). LOCAL SERVICE is in World, so it **should** be able to open
  the device.
- WinUSB gives one process at a time a handle (a second `CreateFile` fails), so only one
  program can read the camera at a time.
- Not tried: no open was ever attempted from LOCAL SERVICE. If it fails, `webcam.log` says
  "in use by another program ... the camera service may not be allowed to open it" while
  nothing else holds the camera. The fix would be a device security override that allows
  LOCAL SERVICE (an admin step: a re-made INF with an `HKR,,Security,,<SDDL>` AddReg line, then
  reinstalling the driver). Not tested.

## Unverified on this model

- The gain commands, and which gain the camera starts in.
- Absolute accuracy and the camera's built-in emissivity assumption.
- What causes the EPIPE stalls (not the shutter, see "Stalls"), and the soft-restart and reopen steps.
- That unplugging the camera switches a disabled automatic shutter back on; the auto-shutter
  commands sent by the C# code (only the Python experiments ran on the camera).
- Unplug and replug on Windows (the code retries every 3 s; tested against fakes only).
- The visible-light camera's bytes in the composite frame. They are not decoded.
- The C# reader on the real camera: the Win32 error codes WinUSB returns for a stall, a timeout
  and a second opener; that `WinUsb_AbortPipe` ends a synchronous read in another thread
  (otherwise the 1 s pipe timeout does); that WinUSB passes the 0xEE device request with
  wIndex 1 unchanged; and that LOCAL SERVICE may open the device (above).
