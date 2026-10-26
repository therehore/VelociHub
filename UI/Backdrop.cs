using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Mx300Hub.Native;

namespace Mx300Hub.UI
{
    /// <summary>
    /// Windows 11 系统级亚克力/云母背景；失败时退化为 Win10 的 Acrylic 强调色模糊。
    /// </summary>
    internal static class Backdrop
    {
        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        public static string Apply(Window window, byte alpha = 0x66, uint tint = 0x00201A18, bool dark = true)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return "无窗口句柄";

            var source = HwndSource.FromHwnd(hwnd);
            if (source?.CompositionTarget != null)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;

            int darkFlag = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag, sizeof(int));

            int round = 2; // DWMWCP_ROUND
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            if (IsWindows11)
            {
                // 系统 backdrop 在部分 WPF 组合下会被客户区绘成不透明黑，
                // 这里直接走 SetWindowCompositionAttribute 的 Acrylic 模糊，
                // 兼容性更好（Win10 1803+ / Win11 全版本均可用）。
                ApplyAccent(hwnd, alpha, tint);
                return $"Acrylic 模糊 (build {Environment.OSVersion.Version.Build})";
            }

            var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            ApplyAccent(hwnd, alpha, tint);
            return $"Win10 Acrylic 回退 (build {Environment.OSVersion.Version.Build})";
        }

        public static void ApplyAccent(IntPtr hwnd, byte alpha = 0x66, uint tint = 0x00201A18)
        {
            try
            {
                var policy = new NativeMethods.AccentPolicy
                {
                    AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                    AccentFlags = 2,
                    GradientColor = (int)((uint)(alpha << 24) | (tint & 0x00FFFFFF)),
                    AnimationId = 0
                };
                int size = Marshal.SizeOf(policy);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, ptr, false);
                    var data = new NativeMethods.WindowCompositionAttributeData
                    {
                        Attribute = NativeMethods.WCA_ACCENT_POLICY,
                        SizeOfData = size,
                        Data = ptr
                    };
                    NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch { }
        }
    }
}
