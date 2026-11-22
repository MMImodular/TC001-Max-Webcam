// TC001 Max Thermal Webcam: the TC001 Max over WinUSB (P/Invoke; no extra packages).
//
// Origin: written for this project from the Microsoft Learn pages cited below. It does what libusb's
// WinUSB backend does (libusb, LGPL-2.1, was the behavioural reference through pyusb); it is not a
// translation of libusb's code.
//
// The same link as the author's earlier Python driver (pyusb + libusb on the Zadig-installed
// WinUSB driver), done with the Windows APIs libusb itself uses on WinUSB:
//  * find: SetupAPI lists present USB devices; ours is USB\VID_3474&PID_4962&MI_00\... (the IAD
//    function that holds interfaces 0 and 1, where Zadig installed WinUSB - docs/PROTOCOL.md
//    "Identity and USB layout"). Its device interface GUID is the "DeviceInterfaceGUIDs"
//    (REG_MULTI_SZ) value Zadig/libwdi writes under the device's hardware key (libwdi picks a random
//    GUID per install, so it is read, never hard-coded); CM_Get_Device_Interface_List gives the path.
//    Microsoft Learn: "SetupDiGetClassDevsW", "SetupDiOpenDevRegKey", "CM_Get_Device_Interface_ListW".
//  * open: CreateFile(GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
//    FILE_FLAG_OVERLAPPED) then WinUsb_Initialize (interface 0) and WinUsb_GetAssociatedInterface(0)
//    (interface 1). Microsoft Learn: "How to Access a USB Device by Using WinUSB Functions",
//    "WinUsb_Initialize", "WinUsb_GetAssociatedInterface". WinUSB is exclusive: while another
//    program has the device open, CreateFile fails (Access denied) - that is how "busy" is detected.
//  * control transfers: WinUsb_ControlTransfer with the 8-byte setup packet of [REF] "Commands";
//    interface: WinUsb_SetCurrentAlternateSetting; bulk IN 0x81: WinUsb_ReadPipe (synchronous,
//    Overlapped = NULL) with PIPE_TRANSFER_TIMEOUT; stall: WinUsb_ResetPipe ("resets the data toggle
//    and clears the stall condition"), which libusb's WinUSB backend also uses for clear_halt;
//    cancel a blocked read: WinUsb_AbortPipe; close: WinUsb_Free both handles, then the file.
//    Microsoft Learn: "WinUsb_ControlTransfer", "WinUsb_ReadPipe", "WinUsb_SetPipePolicy",
//    "WinUsb_ResetPipe", "WinUsb_AbortPipe", "WinUsb_QueryDeviceInformation".
//  * errors: Win32 codes are mapped as libusb's WinUSB backend maps them (windows_winusb.c):
//    ERROR_SEM_TIMEOUT (121) = timeout, ERROR_GEN_FAILURE (31) = stall (libusb's EPIPE, what the
//    Python driver sees during the shutter), ERROR_OPERATION_ABORTED (995) = cancelled,
//    ERROR_FILE_NOT_FOUND (2) / ERROR_DEVICE_NOT_CONNECTED (1167) / ERROR_BAD_COMMAND (22) = gone.
//    UNVERIFIED: not yet checked on the real camera with this C# code (the Python driver sees the stall
//    through libusb on the same driver).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TC001MaxWebcam.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace TC001MaxWebcam.Usb
{
    /// <summary>What SetupAPI knows about the camera (no handle is opened).</summary>
    public sealed record UsbDeviceInfo(string InstanceId, string? InterfaceGuid, string? Path);

    public static class UsbDiscovery
    {
        /// <summary>USB\VID_3474&amp;PID_4962&amp;MI_00\ (docs/PROTOCOL.md).</summary>
        public static readonly string InstancePrefix = $"USB\\VID_{Tc001Max.VendorId:X4}&PID_{Tc001Max.ProductId:X4}&MI_00\\";

        private const uint DigcfPresent = 0x2;
        private const uint DigcfAllClasses = 0x4;
        private const uint DicsFlagGlobal = 0x1;
        private const uint DiregDev = 0x1;
        private const uint KeyRead = 0x20019;
        private const uint CmGetDeviceInterfaceListPresent = 0x0;

        /// <summary>Every present TC001 Max interface-0 function (normally one).</summary>
        public static List<UsbDeviceInfo> Find()
        {
            var found = new List<UsbDeviceInfo>();
            var set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            if (set == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed");
            try
            {
                var data = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                var id = new char[512];
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, (uint)id.Length, out var len))
                        continue;
                    var instance = new string(id, 0, (int)Math.Max(len, 1) - 1);
                    if (!instance.StartsWith(InstancePrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var guid = InterfaceGuid(set, ref data);
                    found.Add(new UsbDeviceInfo(instance, guid, guid == null ? null : InterfacePath(guid, instance)));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return found;
        }

        private static string? InterfaceGuid(IntPtr set, ref SpDevinfoData data)
        {
            var hkey = SetupDiOpenDevRegKey(set, ref data, DicsFlagGlobal, 0, DiregDev, KeyRead);
            if (hkey == new IntPtr(-1))
                return null;
            using var key = RegistryKey.FromHandle(new SafeRegistryHandle(hkey, ownsHandle: true));
            return key.GetValue("DeviceInterfaceGUIDs") switch
            {
                string[] { Length: > 0 } many => many[0],
                string one when one.Length > 0 => one,
                _ => key.GetValue("DeviceInterfaceGUID") as string,
            };
        }

        private static string? InterfacePath(string guidText, string instance)
        {
            if (!Guid.TryParse(guidText, out var guid))
                return null;
            if (CM_Get_Device_Interface_List_SizeW(out var size, ref guid, instance, CmGetDeviceInterfaceListPresent) != 0 || size <= 1)
                return null;
            var buffer = new char[size];
            if (CM_Get_Device_Interface_ListW(ref guid, instance, buffer, size, CmGetDeviceInterfaceListPresent) != 0)
                return null;
            var first = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            return first.Length > 0 ? first[0] : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public uint CbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

        [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SpDevinfoData data, [Out] char[] id, uint size, out uint required);

        [DllImport("setupapi", SetLastError = true)]
        private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref SpDevinfoData data, uint scope, uint hwProfile, uint keyType, uint access);

        [DllImport("setupapi", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid guid, string deviceId, uint flags);

        [DllImport("cfgmgr32", CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string deviceId, [Out] char[] buffer, uint length, uint flags);
    }

    public sealed class WinUsbLink : IUsbLink
    {
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x80;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint PipeTransferTimeout = 0x03;   // WinUsb_SetPipePolicy, winusbio.h
        private const uint DeviceSpeed = 0x01;           // WinUsb_QueryDeviceInformation, winusbio.h

        private const int ErrorFileNotFound = 2;
        private const int ErrorAccessDenied = 5;
        private const int ErrorBadCommand = 22;
        private const int ErrorGenFailure = 31;
        private const int ErrorSharingViolation = 32;
        private const int ErrorSemTimeout = 121;
        private const int ErrorOperationAborted = 995;
        private const int ErrorDeviceNotConnected = 1167;

        private readonly object _lock = new();
        private SafeFileHandle? _file;
        private IntPtr _if0;
        private IntPtr _if1;
        private int _pipeTimeoutMs = -1;
        private volatile bool _closed;

        public string Speed { get; private set; } = "unknown";
        public string DevicePath { get; private set; } = "";

        public void Open()
        {
            List<UsbDeviceInfo> devices;
            try
            {
                devices = UsbDiscovery.Find();
            }
            catch (Win32Exception e)
            {
                throw new CameraUnavailableException($"listing USB devices failed: {e.Message}");
            }
            if (devices.Count == 0)
            {
                throw new CameraUnavailableException("TC001 Max not found (USB 3474:4962)",
                    "Plug it into a USB 3 port; Device Manager should list it.");
            }
            var dev = devices[0];
            if (dev.InterfaceGuid == null || dev.Path == null)
            {
                throw new CameraUnavailableException($"the TC001 Max ({dev.InstanceId}) has no WinUSB driver",
                    "Install WinUSB on 'TC001 Max (Interface 0)' with Zadig (README, step 2).");
            }
            DevicePath = dev.Path;
            var file = CreateFileW(dev.Path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero,
                                   OpenExisting, FileAttributeNormal | FileFlagOverlapped, IntPtr.Zero);
            if (file.IsInvalid)
            {
                var err = Marshal.GetLastWin32Error();
                file.Dispose();
                // UNVERIFIED: the code WinUSB returns to a second opener; libusb treats ACCESS_DENIED as
                // "access" and the Python driver as busy. Sharing violation is accepted too.
                if (err is ErrorAccessDenied or ErrorSharingViolation)
                {
                    throw new CameraUnavailableException(
                        $"the TC001 Max is in use by another program (CreateFile: {new Win32Exception(err).Message})",
                        "WinUSB lets one program open it at a time. If nothing else uses it, the camera service " +
                        "may not be allowed to open it: see README, Troubleshooting.", busy: true);
                }
                throw new CameraUnavailableException($"cannot open the TC001 Max: {new Win32Exception(err).Message} ({err})");
            }
            _file = file;
            _closed = false;
            if (!WinUsb_Initialize(file, out _if0))
            {
                var err = Marshal.GetLastWin32Error();
                Close();
                throw new CameraUnavailableException($"WinUsb_Initialize failed: {new Win32Exception(err).Message} ({err})");
            }
            if (!WinUsb_GetAssociatedInterface(_if0, 0, out _if1))
            {
                var err = Marshal.GetLastWin32Error();
                Close();
                throw new CameraUnavailableException($"interface 1 not available: {new Win32Exception(err).Message} ({err})",
                    "WinUSB must be installed on the function that holds interfaces 0 and 1 (MI_00).");
            }
            uint len = 1;
            Speed = WinUsb_QueryDeviceInformation(_if0, DeviceSpeed, ref len, out var speed) ? speed switch
            {
                1 => "low",
                2 => "full",
                3 => "high", // WinUSB reports SuperSpeed as HighSpeed (winusbio.h has no SuperSpeed value)
                _ => "unknown",
            } : "unknown";
        }

        private IntPtr Iface(ushort index) => index == 0 ? _if0 : _if1;

        private void CheckOpen()
        {
            if (_closed || _if0 == IntPtr.Zero)
                throw new UsbErrorException("the camera link is closed");
        }

        private static Exception Error(string what, int err)
        {
            var text = $"{what}: {new Win32Exception(err).Message} ({err})";
            return err switch
            {
                ErrorSemTimeout => new UsbTimeoutException(text),
                ErrorGenFailure => new UsbStallException(text),
                ErrorOperationAborted => new UsbErrorException(text + " - cancelled"),
                ErrorFileNotFound or ErrorDeviceNotConnected or ErrorBadCommand => new UsbErrorException(text + " - device gone"),
                _ => new UsbErrorException(text),
            };
        }

        private static WinUsbSetupPacket Setup(byte type, byte request, ushort value, ushort index, int length) =>
            new() { RequestType = type, Request = request, Value = value, Index = index, Length = (ushort)length };

        // Control requests go through interface 0's handle. UNVERIFIED: WinUSB forwards the 0xEE device
        // request with wIndex 1 unchanged (the Python driver sends the same packet through libusb, which
        // also uses WinUsb_ControlTransfer on this driver).
        public int ControlOut(byte requestType, byte request, ushort value, ushort index, byte[] data, int timeoutMs)
        {
            CheckOpen();
            SetControlTimeout(timeoutMs);
            if (!WinUsb_ControlTransfer(_if0, Setup(requestType, request, value, index, data.Length), data, (uint)data.Length, out var n, IntPtr.Zero))
                throw Error($"control OUT 0x{requestType:x2}/0x{request:x2}", Marshal.GetLastWin32Error());
            return (int)n;
        }

        public byte[] ControlIn(byte requestType, byte request, ushort value, ushort index, int length, int timeoutMs)
        {
            CheckOpen();
            SetControlTimeout(timeoutMs);
            var buf = new byte[length];
            if (!WinUsb_ControlTransfer(_if0, Setup(requestType, request, value, index, length), buf, (uint)length, out var n, IntPtr.Zero))
                throw Error($"control IN 0x{requestType:x2}/0x{request:x2}", Marshal.GetLastWin32Error());
            return n == length ? buf : buf.AsSpan(0, (int)n).ToArray();
        }

        private int _controlTimeoutMs = -1;

        private void SetControlTimeout(int ms)
        {
            if (ms == _controlTimeoutMs)
                return;
            var v = (uint)ms;
            if (WinUsb_SetPipePolicy(_if0, 0x00, PipeTransferTimeout, 4, ref v))
                _controlTimeoutMs = ms;
        }

        public void SetAlt(ushort iface, byte alt)
        {
            CheckOpen();
            if (!WinUsb_SetCurrentAlternateSetting(Iface(iface), alt))
                throw Error($"set interface {iface} alt {alt}", Marshal.GetLastWin32Error());
            _pipeTimeoutMs = -1; // the pipes are new after an alternate setting change
        }

        public int BulkRead(byte endpoint, byte[] buffer, int timeoutMs)
        {
            CheckOpen();
            if (timeoutMs != _pipeTimeoutMs)
            {
                var v = (uint)timeoutMs;
                if (WinUsb_SetPipePolicy(_if1, endpoint, PipeTransferTimeout, 4, ref v))
                    _pipeTimeoutMs = timeoutMs;
            }
            if (!WinUsb_ReadPipe(_if1, endpoint, buffer, (uint)buffer.Length, out var n, IntPtr.Zero))
                throw Error($"bulk read 0x{endpoint:x2}", Marshal.GetLastWin32Error());
            return (int)n;
        }

        public void ClearHalt(byte endpoint)
        {
            CheckOpen();
            if (!WinUsb_ResetPipe(_if1, endpoint))
                throw Error($"clear halt 0x{endpoint:x2}", Marshal.GetLastWin32Error());
        }

        public void Abort()
        {
            lock (_lock)
            {
                if (_closed || _if1 == IntPtr.Zero)
                    return;
                try
                {
                    // UNVERIFIED: that AbortPipe also ends a synchronous ReadPipe in another thread; if not,
                    // the 1 s pipe timeout ends it.
                    WinUsb_AbortPipe(_if1, Tc001Max.StreamEndpoint);
                }
                catch
                {
                    // never throws
                }
            }
        }

        public void Close()
        {
            lock (_lock)
            {
                _closed = true;
                if (_if1 != IntPtr.Zero)
                    WinUsb_Free(_if1);
                if (_if0 != IntPtr.Zero)
                    WinUsb_Free(_if0);
                _if0 = _if1 = IntPtr.Zero;
                _file?.Dispose();
                _file = null;
                _pipeTimeoutMs = _controlTimeoutMs = -1;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WinUsbSetupPacket
        {
            public byte RequestType;
            public byte Request;
            public ushort Value;
            public ushort Index;
            public ushort Length;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeFileHandle device, out IntPtr iface);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_GetAssociatedInterface(IntPtr iface, byte index, out IntPtr associated);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr iface);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_QueryDeviceInformation(IntPtr iface, uint type, ref uint length, out byte value);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr iface, WinUsbSetupPacket setup, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_SetCurrentAlternateSetting(IntPtr iface, byte setting);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr iface, byte pipe, uint policy, uint length, ref uint value);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr iface, byte pipe, byte[] buffer, uint length, out uint transferred, IntPtr overlapped);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_ResetPipe(IntPtr iface, byte pipe);

        [DllImport("winusb", SetLastError = true)]
        private static extern bool WinUsb_AbortPipe(IntPtr iface, byte pipe);
    }
}
