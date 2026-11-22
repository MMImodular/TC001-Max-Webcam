// TC001 Max Thermal Webcam: the TOPDON TC001 Max wire protocol (pure code, no I/O).
//
// Origin: a C# port of the author's earlier Python driver (not public), which was itself written
// from the sources below, so this file is adapted from them:
//  - command format, CRC, register reads and start_stream: jvdillon/p3-ir-camera P3_PROTOCOL.md
//    and p3_camera.py (Apache License 2.0, Copyright jvdillon and contributors);
//  - command transport, stream framing and the block finder (BlockParser): WendyOS PR #2621
//    go/internal/agent/usbthermal/protocol.go, parser.go, frame.go (Apache License 2.0,
//    Copyright Wendy Labs);
//  - the VDCMD command builder and the auto-shutter (auto-FFC) command ids:
//    xaionaro-go/thermalmaster command.go, command_ffc.go, shutter.go (CC0-1.0).
// Modified for this project: see THIRD_PARTY_NOTICES.txt "Origin of the code". The self-test in
// tests/TC001MaxWebcam.Tests checks this file against real camera bytes and against values that
// driver computed (tests/fixtures/reference_values.json). Every byte comes from the sources
// collected in docs/PROTOCOL.md ([REF]):
//
//  * Commands ([REF] "Commands"): 18 bytes sent as a vendor control OUT to interface 0
//    (bmRequestType 0x41, bRequest 0x20); the busy bit is polled with 0xC1/0x22 and the
//    response read with 0xC1/0x21 (WendyOS PR #2621 protocol.go; jvdillon/p3-ir-camera
//    P3_PROTOCOL.md "Control Transfers"). Layout: 0x01, command (0x01 read register, 0x2f stream
//    control), uint16 LE parameter, uint16 LE register, 6 zero bytes, uint16 LE response length,
//    2 zero bytes, uint16 LE CRC-16/XMODEM of bytes 0..15. The pre-computed bytes in
//    P3_PROTOCOL.md "Pre-computed Commands" are the test vectors; the owner's TC001 Max accepted
//    exactly these bytes on 2026-11-20.
//  * Stream ([REF] "Stream layout"): bulk IN 0x81, 12-byte UVC-style headers (0c 8c..8f ...)
//    start a composite frame; thermal blocks of 386 rows x 512 bytes float between them: rows
//    0-191 grey picture (YUYV, every chroma byte 0x80), rows 192-383 temperatures (uint16 LE,
//    1/64 K), row 384 metadata (word 64 = frame counter), row 385 the end row (ff 00 x 256). A
//    block straddling two composite frames has the next header inside it; it is spliced out.
//  * Temperature ([REF] "Temperature"): degC = raw / 64 - 273.15; raw 0 or 0xFFFF = no reading
//    (WendyOS frame.go; P3_PROTOCOL.md "Temperature Conversion").
//  * Picture leak (measured 2026-11-21, docs/PROTOCOL.md "Picture leak"): now and then the first
//    few to ~70 temperature words of a block hold grey-picture words (luma, chroma 0x80), which
//    read as 238.9..242.8 degC; RepairPictureLeak copies the scene from the row below.
//
// The block finder (BlockParser) is adapted from WendyOS PR #2621
// (go/internal/agent/usbthermal/parser.go, Copyright Wendy Labs, Apache License 2.0), translated
// from Go via the author's Python port and modified (see docs/PROTOCOL.md "Sources and licences");
// the command layout is from p3-ir-camera (Apache-2.0, Copyright jvdillon and contributors).
// See THIRD_PARTY_NOTICES.txt.

using System;
using System.Collections.Generic;

namespace TC001MaxWebcam.Core
{
    /// <summary>The frame cannot be a measurement (framing error, garbage).</summary>
    public sealed class FrameRejectedException(string message) : Exception(message);

    public readonly record struct StreamHeader(byte Info, uint PresentationTs, uint SourceClock, ushort FrameNumber);

    /// <summary>A thermal block decoded into arrays (sensor orientation, row-major 256 x 192).</summary>
    public sealed class DecodedBlock
    {
        public required float[] TemperatureC { get; init; }   // NaN = no reading
        public required byte[] Image { get; init; }           // the camera's grey picture
        public required ushort[] Raw { get; init; }           // 1/64 K counts
        public int Sequence { get; init; }
        /// <summary>Temperature words repaired by <see cref="Tc001Max.RepairPictureLeak"/> (0 = none).</summary>
        public int LeakRepaired { get; init; }
    }

    public sealed class ParserStats
    {
        public long Bytes;
        public int Blocks;
        public int Rejected;
        public int Partial;
        public int Spliced;
        public int Lost;
        public int? LastSequence;
    }

    public static class Tc001Max
    {
        // -- USB ([REF] "Identity and USB layout"; WendyOS protocol.go; P3_PROTOCOL.md "Control Transfers")
        public const ushort VendorId = 0x3474;      // Yantai IRay / Raytron (InfiRay core)
        public const ushort ProductId = 0x4962;     // TC001 Max (Device Manager on the owner's PC)
        public const byte ReqTypeVendorOutInterface = 0x41;
        public const byte ReqTypeVendorInInterface = 0xC1;
        public const byte ReqTypeVendorOutDevice = 0x40;
        public const byte ReqCommand = 0x20;        // OUT: one 18-byte command
        public const byte ReqResponse = 0x21;       // IN: the command's response
        public const byte ReqStatus = 0x22;         // IN: 1 byte, bit 0 set while busy
        public const byte ReqStream = 0xEE;         // OUT to the device, wIndex 1, no data: enable the stream
        public const ushort ControlInterface = 0;
        public const ushort StreamInterface = 1;
        public const byte StreamAltIdle = 0;
        public const byte StreamAltOn = 1;
        public const byte StreamEndpoint = 0x81;
        public const int BulkReadBytes = 16 * 1024; // WendyOS reads 16 KiB pieces; p3-ir-camera FRAME_READ_CHUNK too

        // -- thermal block geometry ([REF] "Stream layout")
        public const int Width = 256;
        public const int Height = 192;
        public const int Pixels = Width * Height;
        public const int RowBytes = 2 * Width;                    // 512
        public const int PictureBytes = RowBytes * Height;        // 98,304
        public const int RawBytes = 2 * PictureBytes;             // 196,608
        public const int MetadataOffset = RawBytes;
        public const int BlockBytes = RawBytes + 2 * RowBytes;    // 197,632
        public const int HeaderBytes = 12;
        public const int MetadataSequenceWord = 64;               // frame counter word in the metadata row

        public const double KelvinPerCount = 1.0 / 64.0;          // [REF] "Temperature"

        /// <summary>Plausibility limits (not the camera's range): no real scene produces values outside them.</summary>
        public const double FloorC = -100.0;
        public const double CeilingC = 600.0;
        public const double MedianMinC = -40.0;
        public const double MedianMaxC = 150.0;

        /// <summary>CRC-16/XMODEM (CCITT polynomial 0x1021, initial value 0, no reflection).</summary>
        public static ushort Crc16Xmodem(ReadOnlySpan<byte> data)
        {
            var crc = 0;
            foreach (var b in data)
            {
                crc ^= b << 8;
                for (var i = 0; i < 8; i++)
                    crc = ((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1) & 0xFFFF;
            }
            return (ushort)crc;
        }

        /// <summary>One 18-byte command (layout in the file header).</summary>
        public static byte[] BuildCommand(byte command, ushort parameter, ushort register, ushort responseLength)
        {
            var c = new byte[18];
            c[0] = 0x01;
            c[1] = command;
            c[2] = (byte)parameter; c[3] = (byte)(parameter >> 8);
            c[4] = (byte)register; c[5] = (byte)(register >> 8);
            c[12] = (byte)responseLength; c[13] = (byte)(responseLength >> 8);
            var crc = Crc16Xmodem(c.AsSpan(0, 16));
            c[16] = (byte)crc; c[17] = (byte)(crc >> 8);
            return c;
        }

        // [REF] "Commands": read model + start_stream verified on a TC001 Max by WendyOS PR #2621 and
        // on the owner's camera (2026-11-20); registers 0x02 (firmware) and 0x07 (serial) from
        // P3_PROTOCOL.md "Register Map", verified on the owner's camera. The gain commands are NOT
        // used here (UNVERIFIED on this model; the Python driver only sends them on request).
        public static readonly byte[] CmdReadModel = BuildCommand(0x01, 0x0081, 0x0001, 30);      // 0101810001000000000000001e0000004f90
        public static readonly byte[] CmdStartStream = BuildCommand(0x2F, 0x0081, 0x0000, 1);     // 012f81000000000000000000010000004930
        public static readonly byte[] CmdReadFirmware = BuildCommand(0x01, 0x0081, 0x0002, 12);   // 0101810002000000000000000c0000001f63
        public static readonly byte[] CmdReadSerial = BuildCommand(0x01, 0x0081, 0x0007, 64);     // 01018100070000000000000040000000104c

        /// <summary>The same 18-byte layout with both type bytes given: a "VDCMD" id {p, t1, t0} goes on
        /// the wire as t0, t1, p, 0, ... (xaionaro-go/thermalmaster command.go BuildVDCMD; the commands
        /// above are VDCMD 0x810101 and 0x812F01 in that notation).</summary>
        public static byte[] BuildCommand(byte type0, byte type1, ushort parameter, ushort register, ushort responseLength)
        {
            var c = BuildCommand(type1, parameter, register, responseLength);
            c[0] = type0;
            var crc = Crc16Xmodem(c.AsSpan(0, 16));
            c[16] = (byte)crc; c[17] = (byte)(crc >> 8);
            return c;
        }

        // [REF] "Automatic shutter": the auto-FFC switch of the camera's AC020 command library (VDCMD
        // category 0x0210; names and ids from xaionaro-go/thermalmaster command_ffc.go and shutter.go,
        // recovered from the Thermal Master app). Experimentally determined 2026-11-20 on one TC001 Max
        // (experiments/LOG.md): GetAutoFFCStatus answers 1 by default; SetAutoFFCStatus(0) stops the
        // automatic shutter (the camera's own log shows no shutter for 390 s, against one every ~60 s)
        // and stays off across a USB close / reopen; SetAutoFFCStatus(1) brings it back.
        // UNVERIFIED: that unplugging the camera restores "on" (believed: the same library has a
        // separate SaveSystemParams command, never sent here); the C# path on the real camera.
        public static readonly byte[] CmdGetAutoShutter = BuildCommand(0x10, 0x02, 0x0081, 0x0001, 1);  // 100281000100000000000000010000000d37
        public static readonly byte[] CmdAutoShutterOff = BuildCommand(0x10, 0x02, 0x0041, 0x0000, 0);  // 100241000000000000000000000000000d3e
        public static readonly byte[] CmdAutoShutterOn = BuildCommand(0x10, 0x02, 0x0041, 0x0001, 0);   // 10024100010000000000000000000000783d
        // [REF] "Commands": manual shutter / NUC (P3_PROTOCOL.md "Pre-computed Commands" shutter).
        public static readonly byte[] CmdShutter = BuildCommand(0x01, 0x36, 0x0043, 0x0000, 0);         // 01364300000000000000000000000000cd0b

        /// <summary>A composite-frame header at <paramref name="offset"/>, or null: length byte 0x0C, info
        /// byte 0x8C..0x8F, 11-bit frame number (&lt; 2048).</summary>
        public static StreamHeader? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
        {
            if (data.Length - offset < HeaderBytes)
                return null;
            if (data[offset] != HeaderBytes || (data[offset + 1] & 0xFC) != 0x8C)
                return null;
            var frame = (ushort)(data[offset + 10] | (data[offset + 11] << 8));
            if (frame >= 2048)
                return null;
            return new StreamHeader(data[offset + 1], BitConverter.ToUInt32(data.Slice(offset + 2, 4)),
                                    BitConverter.ToUInt32(data.Slice(offset + 6, 4)), frame);
        }

        /// <summary>The camera's frame counter from a block's metadata row (word 64).</summary>
        public static int BlockSequence(ReadOnlySpan<byte> block)
        {
            var off = MetadataOffset + 2 * MetadataSequenceWord;
            return block[off] | (block[off + 1] << 8);
        }

        public static double CountsToCelsius(int raw) => raw * KelvinPerCount - 273.15;

        /// <summary>degC to the nearest 1/64 K count (round half to even, like Python's round).</summary>
        public static int CelsiusToCounts(double celsius) => (int)Math.Round((celsius + 273.15) / KelvinPerCount, MidpointRounding.ToEven);

        /// <summary>Throws <see cref="FrameRejectedException"/> unless the counts can be a measurement
        /// (after WendyOS RawFrame.CheckTemperatures plus a ceiling; same rules as the Python driver).</summary>
        public static void ValidateTemperatures(ReadOnlySpan<ushort> raw)
        {
            var hist = new int[65536];
            var n = 0;
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var v in raw)
            {
                if (v == 0 || v == 0xFFFF)
                    continue;
                n++;
                hist[v]++;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (n < raw.Length / 2)
                throw new FrameRejectedException($"only {n} of {raw.Length} pixels have a reading");
            if (lo < CelsiusToCounts(FloorC))
                throw new FrameRejectedException($"a pixel reads {CountsToCelsius(lo):F1} degC (< {FloorC})");
            if (hi > CelsiusToCounts(CeilingC))
                throw new FrameRejectedException($"a pixel reads {CountsToCelsius(hi):F1} degC (> {CeilingC})");
            // numpy.median of the valid counts (mean of the two middle values), truncated to int
            var median = (int)MedianFromHistogram(hist, n);
            var med = CountsToCelsius(median);
            if (med < MedianMinC || med > MedianMaxC)
                throw new FrameRejectedException($"median {med:F1} degC outside ({MedianMinC}, {MedianMaxC})");
        }

        private static double MedianFromHistogram(int[] hist, int n)
        {
            int a = -1, b = -1;
            int ia = (n - 1) / 2, ib = n / 2;
            var seen = 0;
            for (var v = 0; v < hist.Length && b < 0; v++)
            {
                var c = hist[v];
                if (c == 0) continue;
                if (a < 0 && seen + c > ia) a = v;
                if (seen + c > ib) b = v;
                seen += c;
            }
            return (a + (double)b) / 2.0;
        }

        /// <summary>
        /// Repairs the camera's picture leak in place: a run of grey-picture words (high byte 0x80, so
        /// 238.9..242.8 degC) at the very start of the temperature rows. Seen on the owner's TC001 Max in
        /// a 25 h labbench run (2026-11-20/21): 43 frames, about one every 25 minutes, row 0 from x = 0,
        /// roughly 6 to 70 words long, the rest of the frame normal. Unrepaired, one such frame showed a
        /// 240 degC hot spot and widened the auto range to 240 degC, which then took ~20 s to narrow.
        /// Each leaked word gets the reading straight below the run (the scene is continuous there; a
        /// real 240 degC object in that corner reads about the same below). Returns the number of
        /// words repaired; a run over half the frame is not a measurement (FrameRejectedException).
        /// </summary>
        public static int RepairPictureLeak(ushort[] raw)
        {
            var n = 0;
            while (n < raw.Length && raw[n] >> 8 == LeakHighByte)
                n++;
            if (n == 0)
                return 0;
            if (n > raw.Length / 2)
                throw new FrameRejectedException($"{n} temperature words hold picture data");
            for (var i = 0; i < n; i++)
            {
                var j = i + Width;
                while (j < n)
                    j += Width;
                raw[i] = raw[j];
            }
            return n;
        }

        /// <summary>The high byte of a grey-picture word read as a temperature (chroma 0x80).</summary>
        public const int LeakHighByte = 0x80;

        /// <summary>Decode and validate one block from <see cref="BlockParser"/> (FrameRejectedException if bad).</summary>
        public static DecodedBlock DecodeBlock(ReadOnlySpan<byte> block)
        {
            if (block.Length != BlockBytes)
                throw new FrameRejectedException($"block is {block.Length} bytes, expected {BlockBytes}");
            var end = block.Slice(RawBytes + RowBytes);
            for (var i = 0; i < end.Length; i += 2)
            {
                if (end[i] != 0xFF || end[i + 1] != 0x00)
                    throw new FrameRejectedException("end row missing");
            }
            var image = new byte[Pixels];
            for (var i = 0; i < Pixels; i++)
            {
                if (block[2 * i + 1] != 0x80)
                    throw new FrameRejectedException("picture chroma is not neutral grey");
                image[i] = block[2 * i];
            }
            var raw = new ushort[Pixels];
            for (var i = 0; i < Pixels; i++)
                raw[i] = (ushort)(block[PictureBytes + 2 * i] | (block[PictureBytes + 2 * i + 1] << 8));
            var leak = RepairPictureLeak(raw);
            ValidateTemperatures(raw);
            var temp = new float[Pixels];
            const float k = (float)KelvinPerCount;
            for (var i = 0; i < Pixels; i++)
            {
                var r = raw[i];
                // float32 arithmetic, exactly as the Python driver (raw.astype(float32) * float32(1/64) - float32(273.15))
                temp[i] = r == 0 || r == 0xFFFF ? float.NaN : r * k - 273.15f;
            }
            return new DecodedBlock { TemperatureC = temp, Image = image, Raw = raw, Sequence = BlockSequence(block), LeakRepaired = leak };
        }

        /// <summary>A block in the camera's layout from 256 x 192 degC values (tests only; like the original
        /// Python synthetic_block: counts in steps of 4, metadata zero except the frame counter).</summary>
        public static byte[] SyntheticBlock(float[] temperatureC, int sequence = 0)
        {
            var b = new byte[BlockBytes];
            float min = float.MaxValue, max = float.MinValue;
            foreach (var t in temperatureC) { if (t < min) min = t; if (t > max) max = t; }
            var span = Math.Max(max - min, 1e-6);
            for (var i = 0; i < Pixels; i++)
            {
                var t = temperatureC[i];
                b[2 * i] = (byte)((t - min) / span * 255);
                b[2 * i + 1] = 0x80;
                var raw = Math.Clamp(Math.Round((t + 273.15) / KelvinPerCount / 4, MidpointRounding.ToEven) * 4, 1, 0xFFFE);
                var r = (ushort)raw;
                b[PictureBytes + 2 * i] = (byte)r;
                b[PictureBytes + 2 * i + 1] = (byte)(r >> 8);
            }
            var off = MetadataOffset + 2 * MetadataSequenceWord;
            b[off] = (byte)sequence;
            b[off + 1] = (byte)(sequence >> 8);
            for (var i = RawBytes + RowBytes; i < BlockBytes; i += 2)
            {
                b[i] = 0xFF;
                b[i + 1] = 0x00;
            }
            return b;
        }
    }

    /// <summary>
    /// Reassembles validated thermal blocks from the raw bulk stream (port of the original Python BlockParser).
    /// Synchronises on the block's own structure: the end row ff 00 x 256 at the end of a run of ff 00
    /// pairs, the exact block length before it, and a grey picture (every chroma byte 0x80). A block with
    /// exactly one composite-frame header inside it is accepted with the header removed; anything else
    /// that fails is rejected and counted. Memory is bounded (about one block plus 1 MiB).
    /// </summary>
    public sealed class BlockParser
    {
        private const int MaxRun = 4 * Tc001Max.RowBytes;   // longer ff00 runs cannot end a block
        private const int CompactBytes = 1 << 20;
        private static readonly byte[] EndRow = BuildEndRow();

        /// <summary>The block's end row, ff 00 x 256 (protocol END_ROW).</summary>
        public static ReadOnlySpan<byte> EndRowPattern => EndRow;

        private byte[] _buf = new byte[2 * Tc001Max.BlockBytes];
        private int _len;
        private int _scan;

        public BlockParser(bool skipFirst = false, ParserStats? stats = null)
        {
            SkipFirst = skipFirst;
            Stats = stats ?? new ParserStats();
        }

        public ParserStats Stats { get; }

        /// <summary>Drop the first valid block (the camera's buffered, stale frame after a stream start).</summary>
        public bool SkipFirst { get; set; }

        /// <summary>Bytes held (for the memory bound tests).</summary>
        public int BufferedLength => _len;

        private static byte[] BuildEndRow()
        {
            var r = new byte[Tc001Max.RowBytes];
            for (var i = 0; i < r.Length; i += 2) r[i] = 0xFF;
            return r;
        }

        /// <summary>Adds stream bytes; returns every complete valid block they finish, in order.</summary>
        public List<byte[]> Feed(ReadOnlySpan<byte> chunk)
        {
            Stats.Bytes += chunk.Length;
            Append(chunk);
            var output = new List<byte[]>();
            while (true)
            {
                var buf = _buf.AsSpan(0, _len);
                var rel = _scan < _len ? buf.Slice(_scan).IndexOf(EndRow) : -1;
                if (rel < 0)
                {
                    _scan = Math.Max(_scan, _len - Tc001Max.RowBytes + 1);
                    break;
                }
                var m = _scan + rel;
                var e = m + Tc001Max.RowBytes;
                while (e + 1 < _len && buf[e] == 0xFF && buf[e + 1] == 0x00 && e - m < MaxRun)
                    e += 2;
                if (e - m >= MaxRun)
                {
                    Stats.Rejected++;
                    _scan = e;
                    continue;
                }
                if (e + 1 >= _len)
                {
                    _scan = m; // the run may continue in bytes not read yet
                    break;
                }
                if (!FindBlock(m, e, out var start, out var hdr))
                {
                    _scan = e;
                    continue;
                }
                var end = start + Tc001Max.BlockBytes + (hdr >= 0 ? Tc001Max.HeaderBytes : 0);
                if (SkipFirst)
                {
                    SkipFirst = false;
                    _scan = end;
                    continue;
                }
                var block = Assemble(start, hdr, Tc001Max.BlockBytes);
                if (hdr >= 0)
                    Stats.Spliced++;
                NoteSequence(block);
                Stats.Blocks++;
                _scan = end;
                output.Add(block);
            }
            Compact();
            return output;
        }

        private void Append(ReadOnlySpan<byte> chunk)
        {
            if (_len + chunk.Length > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + chunk.Length));
            chunk.CopyTo(_buf.AsSpan(_len));
            _len += chunk.Length;
        }

        private bool FindBlock(int m, int e, out int start, out int hdr)
        {
            start = hdr = -1;
            for (var end = e; end > m + Tc001Max.RowBytes - 1; end -= 2)
            {
                var s = end - Tc001Max.BlockBytes;
                if (s < 0)
                {
                    Stats.Partial++;
                    return false;
                }
                if (Grey(s, -1) && FindHeader(s, end) < 0)
                {
                    start = s;
                    return true;
                }
                s -= Tc001Max.HeaderBytes; // the same block with a header inside it
                if (s < 0)
                    continue;
                var h = FindHeader(s, end);
                if (h < 0 || FindHeader(h + Tc001Max.HeaderBytes, end) >= 0)
                    continue;
                if (Grey(s, h))
                {
                    start = s;
                    hdr = h;
                    return true;
                }
            }
            Stats.Rejected++;
            return false;
        }

        /// <summary>Every chroma byte of the (assembled) picture is 0x80.</summary>
        private bool Grey(int start, int hdr)
        {
            var buf = _buf;
            var head = hdr < 0 || hdr >= start + Tc001Max.PictureBytes ? Tc001Max.PictureBytes : hdr - start;
            for (var j = 1; j < Tc001Max.PictureBytes; j += 2)
            {
                var i = j < head ? start + j : start + j + Tc001Max.HeaderBytes;
                if (buf[i] != 0x80)
                    return false;
            }
            return true;
        }

        /// <summary>Index of the first header wholly inside buf[start..end), or -1.</summary>
        private int FindHeader(int start, int end)
        {
            var last = end - Tc001Max.HeaderBytes;
            var span = _buf.AsSpan(0, _len);
            var pos = start;
            while (pos <= last)
            {
                var rel = span.Slice(pos, last + 1 - pos).IndexOf((byte)0x0C);
                if (rel < 0)
                    return -1;
                var i = pos + rel;
                if (Tc001Max.ParseHeader(span, i) != null)
                    return i;
                pos = i + 1;
            }
            return -1;
        }

        private byte[] Assemble(int start, int hdr, int length)
        {
            var output = new byte[length];
            if (hdr < 0 || hdr >= start + length)
            {
                Buffer.BlockCopy(_buf, start, output, 0, length);
                return output;
            }
            var head = hdr - start;
            Buffer.BlockCopy(_buf, start, output, 0, head);
            Buffer.BlockCopy(_buf, hdr + Tc001Max.HeaderBytes, output, head, length - head);
            return output;
        }

        private void NoteSequence(byte[] block)
        {
            var seq = Tc001Max.BlockSequence(block);
            if (Stats.LastSequence is int last)
            {
                var gap = (seq - last - 1) & 0xFFFF;
                if (gap > 0 && gap < 64) // bigger jumps are a camera restart, not a loss
                    Stats.Lost += gap;
            }
            Stats.LastSequence = seq;
        }

        private void Compact()
        {
            var keep = _scan - (Tc001Max.BlockBytes - Tc001Max.RowBytes);
            if (keep < CompactBytes)
                return;
            Buffer.BlockCopy(_buf, keep, _buf, 0, _len - keep);
            _len -= keep;
            _scan -= keep;
        }
    }
}
