using System;
using System.IO;

namespace Mx300Hub.Services
{
    /// <summary>应用数据目录与文件路径集中定义，避免各处重复拼接。</summary>
    internal static class AppPaths
    {
        public static string Dir { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MX300Hub");

        public static string Settings => Path.Combine(Dir, "settings.json");
        public static string Log => Path.Combine(Dir, "app.log");

        /// <summary>用户自定义品牌图标（统一转存为 PNG）。</summary>
        public static string BrandImage => Path.Combine(Dir, "brand.png");

        public static void Ensure() => Directory.CreateDirectory(Dir);
    }
}
