# Experiment log: why does the TC001 Max's USB stream stall?

**Result (2026-11-20): not the shutter, not the host, cause UNVERIFIED (camera firmware or
cable / hub).** Bulk IN 0x81 fails with EPIPE (`[Errno 32] Pipe error`, async
`LIBUSB_TRANSFER_STALL`) at random times; `clear_halt` brings the stream back within 0.1 s. Nothing
on the read side changed the rate. labbench now calls these "USB stalls" (branch
`tc001-usb-stalls`, `docs/reference/topdon_tc001max.md` "USB stalls (EPIPE): not the shutter").

Owner's request (2026-11-20, relayed): "Please debug the camera feed stalls since they do not
relate to the shutter." Camera lent by the lithium run (run host thermal monitor off) from
21:08:26 to 21:41:22. Safety: only the documented start sequence, reads, GET_STATUS, ClearFeature
/ ResetPipe and the documented `get_auto_ffc_status` read; no writes of any kind. The automatic
shutter was left **on** (read back `01` at the end of every probe run). Host checks were read-only
(no settings, power plans, registry or drivers changed).

Camera: TOPDON TC001 Max, USB 3474:4962, firmware `1.01.000`, WinUSB (Zadig), USB 2.0
High-Speed, bcdUSB 2.00, alone on the USB 2 side of an ASMedia hub (`VID_174C&PID_2074`) on the AMD
xHCI controller (`DEV_149C`). Python: `C:\Git\Automated_Testing\.venv\Scripts\python` (PyUSB 1.3.1,
libusb-package 1.0.30.1), run from `experiments/stalls/`.

Scripts:
- `probe.py`: start the camera like labbench, read for N seconds, log every stall with the last
  reads' timing, GET_STATUS of the endpoint before / after the clear, optionally a read without
  ClearFeature first (`--retry-first`), injected read pauses (`--inject 1,2,5,...`), and an
  asynchronous mode (`--mode async --size 262144 --n 8`, our own ctypes code on the libusb DLL
  that libusb-package ships; `--raw-io` sets the WinUSB RAW_IO pipe policy).
- `run_driver.py`: run the labbench driver itself (`PYTHONPATH` = a labbench `src`) and count.
- `runs/probe.jsonl`, `runs/driver.jsonl`: events and summaries; console output in the
  git-ignored `runs/raw/`.

## 1. Passive analysis (run host log, 2026-11-20)

- 214 EPIPE stalls in 6 h 15 min of the lithium run: 145 on the old driver (reconnect, ~8 s gap
  each), 69 from 18:18 on with the `clear_halt` recovery (0.0-0.1 s each).
- Intervals between the 69 recovered stalls: 4.5-531 s, median 67 s, mean 113 s, SD 125 s
  (SD close to the mean: a memoryless, random process; no period). The rate varied about 3x
  over the evening (~1/min 18:40-19:00, ~0.3/min after 19:20).
- With the auto shutter off (earlier experiment, `experiments/LOG.md`): 5 stalls in 390 s.
  With it on, stalls came 14-17 s after shutter cycles, never at one.
- No link to the ESR pulses (`events.csv`) or other bench activity.
- WinUSB has no idle / selective-suspend values on the camera (`Device Parameters` hold only
  `DeviceInterfaceGUIDs`); Kernel-PnP had no event for the camera before 21:25 (below).
- Metadata across a stall: the frame counter (word 64 / 180) advances 2-3; words 68, 72, 75 vary
  anyway. Lead, not followed up: word 65 (slowly varying, 7505-7558) was higher during the busy
  stretch; a camera temperature?

## 2. Runs on the camera

| Run (label) | Reads | Duration | Stalls | MB/s | Notes |
|---|---|---|---|---|---|
| inject_sync16k | sync 16 KiB | 100 s | 0 | 38.8 | pauses of 1, 2, 5, 10, 20, 50, 100 ms between reads (16 pauses): frames lost, no stall |
| smoke_async | async 8 x 256 KiB | 30 s | 0 | 44.2 | injected 20 / 50 / 100 ms event-loop pauses |
| base_sync16k | sync 16 KiB | 360 s | 3 | 38.9 | read before clear timed out 3/3; GET_STATUS `0000` |
| async8x256k | async 8 x 256 KiB | 360 s | 4 | 44.3 | no host gap at all (7 transfers in flight at each stall) |
| driver_queued8x256k | labbench driver, queued async | 49 s | 1 | - | stall at 21:25:07, then camera dropped off the bus |
| driver_sync16k | labbench driver, sync 16 KiB | 360 s | 0 | 36.9 | 25.0 fps, CPU 16.6 % of one core |
| retry_sync16k | sync 16 KiB | 300 s | 0 | 39.0 | (meant to test a 1 s retry without clear; no stall came) |
| async_rawio | async 8 x 256 KiB + RAW_IO | 300 s | 2 | 44.3 | max transfer 2 MiB |

Totals: synchronous 16 KiB, 3 stalls in 1120 s (0.16/min); asynchronous, 7 in 740 s (0.57/min).
If anything the fast, gap-free reads stalled more (p about 0.05), but the rate drifts with time
anyway (section 1), so the honest reading is "no better".

Observations at the stalls:
- The reads right before each sync stall were back-to-back (0.41 ms each, gaps < 0.01 ms).
- The stalls hit random offsets inside the composite frame (0, 0.79, 1.05 MB in).
- GET_STATUS (USB 2.0 9.4.5) of 0x81 read `0000` (no halt bit) after the stall, after a retry,
  and after the clear.
- A read without ClearFeature timed out (200 ms, 3/3); after `clear_halt` (WinUSB
  `WinUsb_ResetPipe`, 1-3 ms) data flowed at once.
- 21:25:07.629 stall, 21:25:07.653 "device disconnected": Kernel-PnP event 1010 (the camera
  "surprise removed", the only such event ever logged for it). It re-enumerated by itself and
  streamed normally 3 s later. The queued-read driver was dropped after this.
- The camera offers 44.3 MB/s (25 composite frames/s: 6 x 262144 + 197644 + 12 bytes each); 16 KiB
  sync reads fetch ~39 MB/s (~20 composite fps, the camera drops visible-camera data). The thermal
  rate is 25.0 fps either way.

## 3. Conclusions

- Not the shutter / NUC, not host read gaps (FIFO overflow), not the read size, queueing or
  RAW_IO, not shared bandwidth.
- On Windows libusb maps every WinUSB `ERROR_GEN_FAILURE` to a stall (`windows_common.c`), so the
  EPIPE may be a STALL handshake from the camera or a bus transaction error. The camera staying
  silent until ClearFeature and the one bus drop point at the camera / cable side.
- labbench keeps synchronous 16 KiB reads and the `clear_halt` recovery; wording, option names
  (`stall_timeout_s`, `skip_after_stall`) and a `usb_stalls` count changed.

## 4. Open: needs the owner

1. Another USB port without the ASMedia hub, and another cable: `python probe.py other_port --seconds 600` and
   compare the stall rate (0.16-0.6/min here).
2. Or an elevated USB ETW trace (Microsoft-Windows-USB-USBXHCI, Microsoft's "USB Event Tracing for
   Windows" guide) across a few stalls: the xHCI completion code says *Stall Error* or *USB
   Transaction Error*.
