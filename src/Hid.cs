// Minimal Windows HID access: enumerate interfaces, open one, write output reports and read input reports.
// Reads and writes are overlapped with a timeout. The buffer and OVERLAPPED live in unmanaged memory, because
// Windows may still write into them after ReadFile/WriteFile return (a managed array could have moved by then).
// Note: C# 5 only (built with the .NET Framework csc), so no string interpolation / ?. / expression bodies.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace DacDeck
{
    internal sealed class HidEntry
    {
        public string Path;
        public ushort Vid, Pid, UsagePage, Usage, InLen, OutLen, FeatLen;
        public string Product;
    }

    internal static class Hid
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string DevicePath; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage; public ushort UsagePage; public ushort InputReportByteLength;
            public ushort OutputReportByteLength; public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes; public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps; public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps; public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices; public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps; public ushort NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
        [DllImport("hid.dll", CharSet = CharSet.Unicode)] private static extern bool HidD_GetProductString(SafeFileHandle h, StringBuilder buf, int len);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA data, ref SP_DEVICE_INTERFACE_DETAIL_DATA detail, int detailSize, out int reqSize, IntPtr devInfoData);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        public static List<HidEntry> Enumerate()
        {
            var list = new List<HidEntry>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr info = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (info == IntPtr.Zero || info.ToInt64() == -1) return list;
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    var det = new SP_DEVICE_INTERFACE_DETAIL_DATA { cbSize = IntPtr.Size == 8 ? 8 : 6 };
                    int req;
                    if (!SetupDiGetDeviceInterfaceDetail(info, ref ifData, ref det, Marshal.SizeOf(det), out req, IntPtr.Zero)) continue;
                    using (var h = CreateFile(det.DevicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref attr)) continue;
                        var prod = new StringBuilder(128);
                        HidD_GetProductString(h, prod, 128);
                        IntPtr pp;
                        if (!HidD_GetPreparsedData(h, out pp)) continue;
                        HIDP_CAPS caps;
                        try { HidP_GetCaps(pp, out caps); }
                        finally { HidD_FreePreparsedData(pp); }
                        list.Add(new HidEntry
                        {
                            Path = det.DevicePath, Vid = attr.VendorID, Pid = attr.ProductID,
                            UsagePage = caps.UsagePage, Usage = caps.Usage,
                            InLen = caps.InputReportByteLength, OutLen = caps.OutputReportByteLength,
                            FeatLen = caps.FeatureReportByteLength, Product = prod.ToString().Trim()
                        });
                    }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(info); }
            return list;
        }

        public static HidPort Open(HidEntry e)
        {
            var h = CreateFile(e.Path, 0xC0000000u, 3, IntPtr.Zero, 3, 0x40000000u, IntPtr.Zero); // read/write, overlapped
            if (h.IsInvalid) { h.Dispose(); return null; }
            return new HidPort(h, Math.Max((int)e.InLen, 64), Math.Max((int)e.OutLen, 64));
        }
    }

    // One open HID interface. Not thread-safe: use it from one thread at a time.
    internal sealed class HidPort : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle h, IntPtr buf, uint toRead, out uint read, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle h, IntPtr buf, uint toWrite, out uint written, IntPtr ov);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] private static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ov, out uint xfer, bool wait);

        private const int ERROR_IO_PENDING = 997;
        private readonly SafeFileHandle _h;
        private readonly ManualResetEvent _ev = new ManualResetEvent(false);
        private readonly IntPtr _ov, _buf;
        private readonly int _bufLen;
        public readonly int InLen, OutLen;

        internal HidPort(SafeFileHandle h, int inLen, int outLen)
        {
            _h = h;
            InLen = inLen;
            OutLen = outLen;
            _bufLen = Math.Max(inLen, outLen);
            _buf = Marshal.AllocHGlobal(_bufLen);
            _ov = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
        }

        private bool Io(bool write, int len, uint timeoutMs, out int done)
        {
            done = 0;
            _ev.Reset();
            Marshal.StructureToPtr(new NativeOverlapped { EventHandle = _ev.SafeWaitHandle.DangerousGetHandle() }, _ov, false);
            uint n;
            bool ok = write ? WriteFile(_h, _buf, (uint)len, out n, _ov) : ReadFile(_h, _buf, (uint)len, out n, _ov);
            if (!ok)
            {
                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING) return false;
                if (WaitForSingleObject(_ev.SafeWaitHandle.DangerousGetHandle(), timeoutMs) != 0)
                {
                    CancelIoEx(_h, _ov);
                    GetOverlappedResult(_h, _ov, out n, true); // wait until Windows is done with _buf / _ov
                    return false;
                }
                if (!GetOverlappedResult(_h, _ov, out n, false)) return false;
            }
            done = (int)n;
            return true;
        }

        // Output report: report[0] is the report id; padded with zeros to the interface's output length
        public bool Write(byte[] report, uint timeoutMs)
        {
            var buf = new byte[OutLen];
            Array.Copy(report, buf, Math.Min(report.Length, OutLen));
            Marshal.Copy(buf, 0, _buf, OutLen);
            int n;
            return Io(true, OutLen, timeoutMs, out n);
        }

        // Next input report (report id first), or null on timeout
        public byte[] Read(uint timeoutMs)
        {
            int n;
            if (!Io(false, InLen, timeoutMs, out n) || n <= 0) return null;
            var r = new byte[n];
            Marshal.Copy(_buf, r, 0, n);
            return r;
        }

        public void Dispose()
        {
            _h.Dispose(); // closing the handle cancels anything still pending
            _ev.Dispose();
            Marshal.FreeHGlobal(_buf);
            Marshal.FreeHGlobal(_ov);
        }
    }
}
