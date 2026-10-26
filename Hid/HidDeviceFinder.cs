using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mx300Hub.Native;

namespace Mx300Hub.Hid
{
    public sealed class HidInterfaceInfo
    {
        public string Path = "";
        public ushort VendorId;
        public ushort ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InputReportLength;
        public ushort OutputReportLength;
        public string ProductName = "";
        public string Manufacturer = "";
        public int InterfaceNumber = -1;

        public string VidPidText => $"VID_{VendorId:X4} / PID_{ProductId:X4}";
        public string UsageText => $"0x{UsagePage:X4} / 0x{Usage:X4}";

        public override string ToString() =>
            $"{VidPidText}  usage {UsageText}  in={InputReportLength} out={OutputReportLength}  {ProductName}";
    }

    /// <summary>
    /// 枚举 HID 设备集合，定位 MX300 / MX301 系列接收器的厂商控制接口。
    /// 参考逆向结果：0xFF01 / 0x10 为厂商控制集合；VID 0xA8A4 为有线，0xA8A5 为 2.4G。
    /// </summary>
    public static class HidDeviceFinder
    {
        private static readonly Guid HidInterfaceGuid =
            new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        public static readonly ushort[] KnownVendorIds = { 0xA8A4, 0xA8A5, 0xA8A6, 0xA8A7 };

        public static List<HidInterfaceInfo> EnumerateAll(ushort[]? vendorFilter = null)
        {
            var result = new List<HidInterfaceInfo>();
            Guid guid = HidInterfaceGuid;
            IntPtr set = NativeMethods.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

            try
            {
                uint index = 0;
                while (true)
                {
                    var did = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
                    did.cbSize = Marshal.SizeOf(did);
                    if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref did))
                        break;
                    index++;

                    var info = QueryInterface(set, ref did);
                    if (info == null) continue;

                    if (vendorFilter != null)
                    {
                        bool match = false;
                        foreach (var v in vendorFilter)
                            if (v == info.VendorId) { match = true; break; }
                        if (!match) continue;
                    }
                    result.Add(info);
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        private static HidInterfaceInfo? QueryInterface(IntPtr set, ref NativeMethods.SP_DEVICE_INTERFACE_DATA did)
        {
            int required;
            NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required <= 0) return null;

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                Marshal.WriteInt32(buffer, 0, IntPtr.Size == 8 ? 8 : 6);
                if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, required, out required, IntPtr.Zero))
                    return null;
                string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? "";
                if (path.Length == 0) return null;

                using var handle = NativeMethods.CreateFile(path,
                    NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
                if (handle.IsInvalid) return null;

                var attrs = new NativeMethods.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>() };
                if (!NativeMethods.HidD_GetAttributes(handle, ref attrs)) return null;
                if (!NativeMethods.HidD_GetPreparsedData(handle, out IntPtr preparsed)) return null;

                var caps = new NativeMethods.HIDP_CAPS { Reserved = new ushort[17] };
                try
                {
                    if (NativeMethods.HidP_GetCaps(preparsed, ref caps) != 0x00110000) return null;
                }
                finally
                {
                    NativeMethods.HidD_FreePreparsedData(preparsed);
                }

                var info = new HidInterfaceInfo
                {
                    Path = path,
                    VendorId = attrs.VendorID,
                    ProductId = attrs.ProductID,
                    UsagePage = caps.UsagePage,
                    Usage = caps.Usage,
                    InputReportLength = caps.InputReportByteLength,
                    OutputReportLength = caps.OutputReportByteLength,
                    ProductName = ReadString(handle, true),
                    Manufacturer = ReadString(handle, false),
                };

                int mi = path.ToLowerInvariant().IndexOf("mi_", StringComparison.Ordinal);
                if (mi > 0 && mi + 5 <= path.Length &&
                    int.TryParse(path.Substring(mi + 3, 2), System.Globalization.NumberStyles.HexNumber, null, out int num))
                    info.InterfaceNumber = num;

                return info;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadString(SafeFileHandle handle, bool product)
        {
            try
            {
                var buf = new byte[512];
                bool ok = product
                    ? NativeMethods.HidD_GetProductString(handle, buf, buf.Length)
                    : NativeMethods.HidD_GetManufacturerString(handle, buf, buf.Length);
                if (!ok) return "";
                string s = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                return s;
            }
            catch { return ""; }
        }

        /// <summary>定位厂商控制集合（0xFF01 / 0x10）；找不到时回退到任意 0xFF01 集合。</summary>
        public static HidInterfaceInfo? FindVendorControl(out List<HidInterfaceInfo> all)
        {
            all = EnumerateAll(KnownVendorIds);
            HidInterfaceInfo? exact = null, fallback = null;
            foreach (var d in all)
            {
                if (d.UsagePage == 0xFF01 && d.Usage == 0x10 && d.OutputReportLength >= 8 && exact == null)
                    exact = d;
                if (d.UsagePage == 0xFF01 && fallback == null && d.OutputReportLength >= 8)
                    fallback = d;
            }
            return exact ?? fallback;
        }
    }
}
