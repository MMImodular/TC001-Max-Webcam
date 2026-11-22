# Test fixtures

`tc001max_*` are real bytes from the author's own TOPDON TC001 Max (firmware 1.01.000), captured
read-only on 2026-11-20 over WinUSB with the camera looking at a room (about 26-31 degC in
view). They contain only the camera's USB stream (thermal and visible-camera bytes of a room):

- `tc001max_block_window.bin.gz`: one whole thermal block (frame counter 456) with 4096 bytes of
  the surrounding stream (visible-camera bytes) before and after it, exactly as it arrived on
  bulk IN 0x81.
- `tc001max_spliced_window.bin.gz`: a block that straddled two composite frames, so the next
  composite frame's 12-byte header sits inside it (temperature row 56), with 4096 bytes of
  stream before and after.
- `tc001max_header_window.bin`: 2048 bytes of stream centred on a composite-frame header
  (header at offset 1024).

`reference_values.json` holds values computed on 2026-11-20 by the author's earlier Python
implementation of the same code (it is not part of this repository): the inferno table, the
orientation cases, 1st-percentile cases, a run of the auto-range smoother, statistics of the
two real blocks above, and the protocol commands. It is a frozen copy (entries that only
concerned the original application were removed); never edit it by hand. The self-test checks
the C# code against it.

See `docs/PROTOCOL.md` for the stream layout.
