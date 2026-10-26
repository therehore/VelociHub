using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VelociHub.Services
{
    public sealed class AppSettings
    {
        /// <summary>设置结构版本，用于一次性迁移（0 = 旧版未标记）。</summary>
        public int SchemaVersion { get; set; } = 0;

        /// <summary>后台轮询间隔（秒）。悬停托盘或打开窗口时会自动临时提速到 5 秒。</summary>
        public int PollSeconds { get; set; } = 120;
        public bool OsdEnabled { get; set; } = true;
        public int OsdSeconds { get; set; } = 6;
        public int WarnThreshold1 { get; set; } = 20;
        public int WarnThreshold2 { get; set; } = 10;
        public bool StartWithWindows { get; set; } = false;
        public string LastTab { get; set; } = "perf";

        /// <summary>窗口背景不透明度（0.15–0.90）。</summary>
        public double BgOpacity { get; set; } = 0.30;
        /// <summary>浅色模式。</summary>
        public bool LightMode { get; set; } = false;

        /// <summary>充电上限保护：充到设定电量后提醒取下鼠标。</summary>
        public bool ChargeLimitEnabled { get; set; } = true;
        /// <summary>充电上限百分比（55–95）。锂电长期满电存放会加速衰减。</summary>
        public int ChargeLimit { get; set; } = 80;

        public static string FilePath => AppPaths.Settings;

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<AppSettings>(json, Options);
                    if (s != null)
                    {
                        // v2：后台轮询从 60 秒改为 120 秒（交互时另有 5 秒提速），
                        // 透明度下限从 15% 放开到 0%。
                        if (s.SchemaVersion < 2)
                        {
                            s.PollSeconds = 120;
                            s.BgOpacity = Math.Clamp(s.BgOpacity, 0.0, 1.0);
                            s.SchemaVersion = 2;
                            s.Save();
                        }
                        return s;
                    }
                }
            }
            catch { }
            return new AppSettings { SchemaVersion = 2 };
        }

        public void Save()
        {
            try
            {
                AppPaths.Ensure();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
            }
            catch { }
        }
    }
}
