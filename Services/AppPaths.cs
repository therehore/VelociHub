using System;
using System.IO;

namespace VelociHub.Services
{
    /// <summary>应用数据目录与文件路径集中定义，避免各处重复拼接。</summary>
    internal static class AppPaths
    {
        private const string FolderName = "VelociHub";

        /// <summary>历史目录名。改过名，升级时把设置和自定义图标搬过来，用户无感。</summary>
        private static readonly string[] LegacyFolders = { "MX300Hub" };

        public static string Dir { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);

        public static string Settings => Path.Combine(Dir, "settings.json");
        public static string Log => Path.Combine(Dir, "app.log");

        /// <summary>用户自定义软件图标（统一转存为 PNG）。</summary>
        public static string BrandImage => Path.Combine(Dir, "brand.png");

        private static bool _migrated;

        /// <summary>建目录并做一次旧目录迁移。启动第一时间调用，早于任何读写。</summary>
        public static void Ensure()
        {
            try { Directory.CreateDirectory(Dir); } catch { }
            MigrateOnce();
        }

        private static void MigrateOnce()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string? root = Path.GetDirectoryName(Dir);
                if (root == null) return;
                foreach (string legacy in LegacyFolders)
                {
                    string old = Path.Combine(root, legacy);
                    if (!Directory.Exists(old) || string.Equals(old, Dir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    CopyIfMissing(Path.Combine(old, "settings.json"), Settings);
                    CopyIfMissing(Path.Combine(old, "brand.png"), BrandImage);
                }
            }
            catch { }
        }

        private static void CopyIfMissing(string from, string to)
        {
            try
            {
                if (File.Exists(to) || !File.Exists(from)) return;
                File.Copy(from, to);
            }
            catch { }
        }
    }
}
