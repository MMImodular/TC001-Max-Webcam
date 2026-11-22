# Experiment log: can the TC001 Max's automatic shutter / NUC be switched off?

**Result (2026-11-20): yes.** `SetAutoFFCStatus(0)` (`100241000000000000000000000000000d3e`)
stops the automatic shutter; `SetAutoFFCStatus(1)` (`10024100010000000000000000000000783d`)
brings it back. No interval command was found or tried. The camera was left with the automatic
shutter **on** (read back `01`) and released at 20:28:54.

Owner's permission (2026-11-20): "Within the TC001max-webcam repository you have permission to
experiment with ways to disable the shutter". Owner's conditions: "Try not to brick the device":
only volatile commands documented for this chip family; nothing that writes flash, calibration,
firmware, IDs or a config save; static analysis only for firmware / vendor software; no tool
installs; no brute force.

Camera: TOPDON TC001 Max, USB 3474:4962, firmware register `1.01.000`, WinUSB on MI_00 (Zadig).
Scripts: `cam.py` (helpers: commands, start sequence, monitor), `run_baseline.py`, `run_ffc.py`,
`run_manual.py`. Event logs: `runs/*.jsonl` (stalls, firmware-log lines, frozen-frame runs,
summaries; the per-frame metadata noise and the console output stay in the git-ignored
`runs/raw/`). Python: `C:\Git\Automated_Testing\.venv\Scripts\python` (pyusb + libusb-package),
run from `experiments/`; the block finder is the labbench driver's `BlockParser` (read-only import).

## 1. Sources

| What | Source | Used for |
|---|---|---|
| P3 protocol, register map, pre-computed commands (incl. manual shutter `01364300...cd0b`), firmware log in the status register | [jvdillon/p3-ir-camera `P3_PROTOCOL.md`](https://github.com/jvdillon/p3-ir-camera/blob/main/P3_PROTOCOL.md) (Apache-2.0) | C1-C4 |
| The AC020 command library's FFC/shutter commands: `CmdSetAutoFFCStatus` 0x410210, `CmdGetAutoFFCStatus` 0x810210, `CmdGetAutoFFCCurrentParams` 0x820210, `CmdGetShutterStatus` 0x830210, `CmdManualFFCUpdate` 0x430210; VDCMD id to wire mapping (`BuildVDCMD`); `SetAutoFFCEnabled` puts 0/1 in the register field | [xaionaro-go/thermalmaster](https://github.com/xaionaro-go/thermalmaster) `pkg/thermalmaster/command_ffc.go`, `shutter.go`, `command.go` (CC0-1.0) | R1-R3, W1 |
| That table comes from static analysis of the Thermal Master Android app; its "AC020" SDK and command library serve the X3, X2 Pro, X2L, X15, X919, P1, **P2L**, P3 and P4; the same library has `SaveSystemParams` 0x511010 / `RestoreSystemParams` 0x521010, `ResetToRom`, `ResetToBootloader` (all **never sent**) | thermalmaster [`doc/reverse-engineering.md`](https://github.com/xaionaro-go/thermalmaster/blob/main/doc/reverse-engineering.md), `command_device_control.go`, `command_system.go` | chip identity; volatility argument |
| TC001 Max = device type 15 with P3, P1, Omni One, AC001 | thermalmaster `device_type.go` | same command family |
| The vendor apps expose an auto-shutter toggle | TOPDON [TopInfrared user manual](https://web-file.topdon.com/topdon-web/information_download/TopInfrared-User-Manual.pdf); Thermal Master P3 app ([Gough Lui's review](https://goughlui.com/2025/10/07/review-thermal-master-p3-android-ios-windows-usb-c-thermal-camera-256x192-25hz-35mk/)) | a command exists |
| InfiRay convention: auto-shutter parameters (switch, min/max interval 5-655 s, ...) are RAM settings until `spi_config_save` writes them to flash | InfiRay Tiny1-C SDK `ASIC_2121W_RTOS_SDK_V1.0.0` `VDCMD/falcon_cmd.h` (`prop_auto_shutter_params`, `spi_config_save`), published in [danjulio/iCam `Tiny1C_documentation/`](https://github.com/danjulio/iCam/tree/main/Tiny1C_documentation) | volatility argument (different chip: reference only, none of its commands sent) |
| No auto-shutter disable in P3_PROTOCOL.md, WendyOS PR #2621, ferrolho/thermal-master-viewer (manual shutter only) | as linked in `docs/STATE_OF_THE_ART.md` | |

Downloaded files (thermalmaster sources, p3-ir-camera files, the Tiny1-C SDK zip) were kept in a
scratch folder outside the repository and only read as text (Python `-I`); nothing was executed,
nothing vendor-made is committed. No firmware was read from or written to the camera.

**Chips (owner's request).** No teardown, FCC internal photos (TOPDON grantee 2AVYW has no
TC001 filing on [fccid.io](https://fccid.io/2AVYW)) or datasheet were found. From the camera
itself (C1): part number register 0x06 `P2L-D`, hardware register 0x0A `DVS-00.01`, firmware
`1.01.000`; the firmware log names modules `std cmd`, `EXT_CMD`, `shutter`, `ffc_svc`, `isp` and
prints a chip id at start-up. So the core is most likely an InfiRay AC020-family module of the
Thermal Master P2L class (inferred; details in `docs/PROTOCOL.md` "Hardware" and
`docs/STATE_OF_THE_ART.md`). USBPcap capture of the TOPDON app toggling the switch would have
been the next route; it was not needed (and needs an admin driver install, not done).

## 2. Commands sent (planned and logged here before sending)

| # | Name | Bytes | Source | Why | Worst case | Result |
|---|---|---|---|---|---|---|
| C1 | read model / fw / part / hw / model_long | `0101810001…4f90`, `…0002…1f63`, `…0006…654f`, `…000a…1959`, `…000f…b857` | P3_PROTOCOL.md "Pre-computed Commands" | identity of the core (which SDK applies) | none: register reads | `TC001 Max`, `1.01.000`, `P2L-D`, `DVS-00.01`, 4-byte chip id + `00.01` |
| C2 | extended status read (128 B on 0xC1/0x22) | control IN, no command | P3_PROTOCOL.md "Extended Status Register (Debug Logs)" | the camera's own log = an objective shutter indicator | none: the busy-status read with a longer length | works: latest log line from byte 64 (polled every 0.5 s) |
| C3 | start_stream + start sequence | `012f81000000000000000000010000004930` | P3_PROTOCOL.md, verified here before | baseline | none | reply `01`, 25.0 fps |
| R1 | GetAutoFFCStatus | `100281000100000000000000010000000d37` | thermalmaster `CmdGetAutoFFCStatus` (VDCMD 0x810210; `GetAutoFFCEnabled`: non-zero = on) | read the switch | a read | `01` by default; `00` after W1 off; `01` after W1 on. Status byte 0x02 |
| R2 | GetAutoFFCCurrentParams | `10028200010000000000000001000000aeba` | thermalmaster `CmdGetAutoFFCCurrentParams` (0x820210) | an interval / parameter? | a read | `01`, then `00` while off: follows R1. Busy status 0x04 (meaning unknown) |
| R3 | GetShutterStatus | `10028300010000000000000001000000cfc1` | thermalmaster `CmdGetShutterStatus` (0x830210) | shutter state | a read | always `01`. The log shows `I/EXT_CMD: ext : 10 2 83` (the firmware calls type `10 xx` "ext" commands) |
| W1 | SetAutoFFCStatus 0 / 1 | `100241000000000000000000000000000d3e` (off), `10024100010000000000000000000000783d` (on) | thermalmaster `CmdSetAutoFFCStatus` (0x410210), `SetAutoFFCEnabled` | the switch | a run-time flag; believed volatile (the library has a separate, never-sent `SaveSystemParams`; InfiRay's Tiny1-C keeps its auto-shutter switch in RAM until `spi_config_save`). Not a flash, calibration, firmware or ID write | **works**, see section 3. Status byte after the write 0x00 |
| C4 | manual shutter | `01364300000000000000000000000000cd0b` | P3_PROTOCOL.md `shutter`; thermalmaster `CmdShutter` | does a manual NUC still work while the auto shutter is off? | one shutter cycle | works: `I/ffc_svc: ffc b update done` right after it, auto shutter still off |

Nothing else was sent: no gain, no save / restore / reset / reboot / bootloader / calibration /
DPC / K-value command, no write to any register, no opcode search.

## 3. Measured timelines

Indicators, all logged with the time since streaming started:
- **firmware log**: `I/shutter: shutter close|open` + `I/ffc_svc: ffc b update done` (sometimes
  `I/isp: b average value ...`) = one automatic shutter / NUC cycle;
- **frozen**: 15 consecutive identical temperature frames (0.6 s): the camera repeats the last
  frame while its shutter is closed (indicator added from the "on" run onwards);
- **stall**: EPIPE on bulk IN 0x81, cleared with `clear_halt` (0.000-0.015 s), stream continued;
- frames per second, median scene temperature (decoded with the labbench `BlockParser`).
- (`jump` events in the files: a frame-to-frame change above 8x the running median. They turned
  out to be single odd frames in all conditions, with and without the shutter, so they are not
  used as an indicator.)

| Run (file) | Auto shutter | Length | Shutter events (firmware log) | Frozen runs | Stalls | fps / median |
|---|---|---|---|---|---|---|
| baseline (`baseline.jsonl`), default start sequence, 20:08:51 | on (default) | 330 s | 58.0, 118.5-119.5, 179.5, 240.0, 300.5 s: **every 60.6 s** | (not measured) | 75.5, 133.3 s | 24.99 fps, 26.5-26.9 °C |
| off (`off.jsonl`), W1 off right after the start, 20:14:56 | **off** (R1 = 00) | 390 s | **none** (6 expected) | (not measured) | 61.9, 251.7, 268.3, 321.1, 385.6 s | 24.98 fps, 26.2-26.5 °C |
| off_reopened (`off_reopened.jsonl`): new process, interfaces re-claimed, start sequence, nothing set, 20:21:37 | **still off** (R1 = 00 before the start) | 200 s | **none** (3 expected) | **none** | none | 25.0 fps, 26.5-26.6 °C |
| manual (`manual_then_on.jsonl` pre_manual / after_manual), 20:25:13 | off | 10 + 60 s | C4 at 10 s: `ffc b update done` at once; no other | none | 53.1 s after C4 | 25.0 fps, 26.4-26.5 °C |
| on (`manual_then_on.jsonl` on), W1 on at 20:26:24 | **on** (R1 = 01) | 150 s | 0.5 s (switching on triggers one), 61.0-61.5 s, 121.5-122.0 s | 0.8, 61.4, 122.0 s (coincide with the log) | none | 24.99 fps, 26.4-26.6 °C |

Total with the switch off: 660 s (about 11 expected shutter intervals) with no automatic shutter
event in either indicator; with it on, an event every 60-61 s in both. The stream decoded
normally throughout (25 fps, plausible room-temperature medians, no reconnect).

## 4. Conclusions

1. **The automatic shutter of the TC001 Max can be switched off** with the AC020 command
   library's `SetAutoFFCStatus(0)` and back on with `SetAutoFFCStatus(1)`; `GetAutoFFCStatus`
   reads it. Experimentally determined 2026-11-20 on one camera, firmware `1.01.000`. A manual
   shutter (P3 `shutter` command) still works while it is off.
2. **The switch survives a USB close / reopen** (release of both interfaces, a new process, the
   full start sequence): whoever switches it off must switch it back on. Whether a **power cycle
   (unplug / replug) restores "on" was not tested** (no one was at the camera). It is believed
   (not proven) because the library has a separate save command that was never sent and InfiRay's
   Tiny1-C stores the same kind of switch only on an explicit save. **To check: unplug and replug
   once after a session that left it off, then run R1** (`run_ffc.py reads 5 runs/x.jsonl`
   prints `get_auto_ffc_status`).
3. **No interval control was found.** R2 ("current params") only mirrors the switch on this
   firmware. The Tiny1-C has min/max interval parameters (5-655 s), but no AC020 equivalent is
   known, and none was tried (no guessing).
4. **The EPIPE stalls are not the shutter.** They happened with the switch off (5 in 390 s) and
   did not coincide with shutter events with it on (17 s and 14 s after them in the baseline;
   none in the 150 s "on" run). The earlier assumption (labbench driver, `docs/PROTOCOL.md`) is
   refuted; their cause is unknown. Recovery with `clear_halt` stays correct.
5. **Safety.** Only reads, the documented manual shutter and one run-time switch were sent. The
   camera answered every command with a normal status, kept streaming and ended in its default
   state (R1 = `01`, an automatic shutter seen at 61 s and 122 s after switching on). Camera time:
   20:07-20:29.

## 5. Trade-off (for the webcam's `auto_shutter=off`)

Without the shutter the per-pixel offsets are not refreshed: fixed-pattern noise grows and the
readings drift with the camera's own temperature. Relative readings within a frame stay usable
for a while; long sessions drift. Not quantified here (the scene was a flat, stable wall; the
scene median stayed within 26.2-26.6 °C over 13 min with the switch off).
