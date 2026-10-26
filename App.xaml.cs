using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using VelociHub.Services;
using VelociHub.UI;

namespace VelociHub
{
    public partial class App : Application
    {
        public const string WebDriverUrl = "https://hub.mloongplanet.com/#/device";
        public const string PairingUrl = "https://hub.mloongplanet.com/#/tools?pairing=MX300";

        /// <summary>作者主页与仓库地址，界面上「作者 GitHub」按钮、托盘菜单、诊断信息都用这两个。</summary>
        public const string AuthorHandle = "therehore";
        public const string AuthorUrl = "https://github.com/therehore";
        public const string RepoUrl = "https://github.com/therehore/VelociHub";

        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "VelociHub";

        /// <summary>改名前写进注册表的启动项名，启动时顺手清理，避免残留项指向已删除的 exe。</summary>
        private static readonly string[] LegacyRunValues = { "MX300Hub", "WaaaghHub" };

        private static Mutex? _mutex;
        private readonly HashSet<int> _warned = new();
        private FlyoutWindow? _flyout;
        private OsdWindow? _osd;
        private DateTime _flyoutHiddenAt = DateTime.MinValue;

        public static AppSettings Settings { get; private set; } = new();
        public static DeviceService Device { get; private set; } = null!;
        public static TrayService Tray { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, "MX300Hub_SingleInstance_v1", out bool created);
            if (!created)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                Trace("UI异常: " + args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Trace("未处理异常: " + args.ExceptionObject);

            base.OnStartup(e);

            Services.AppPaths.Ensure();   // 先建目录/迁移旧设置，再读配置
            Settings = AppSettings.Load();
            BrandIcon.Load();
            ApplyBrandIconArg(e.Args);
            EnsureStartupRegistry();
            // exe 改名后旧的自启项还指向老路径，开着自启就顺手修正
            if (Settings.StartWithWindows) ApplyStartup();

            // 启动/退出标记：日志里没有「正常退出」就说明进程是被杀或崩掉的。
            // CLR 致命错误（0x80131506 等）托管层抓不住，只能靠这对标记来判断。
            Trace($"===== 启动 v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} " +
                  $"pid={Environment.ProcessId} =====");

            Device = new DeviceService(Dispatcher);
            Device.BatteryUpdated += OnBattery;
            Device.DpiChanged += OnDpiChanged;
            Device.StateChanged += RefreshTray;
            Device.Log += Trace;

            Tray = new TrayService(Dispatcher);
            Tray.OpenRequested += ShowFlyout;
            Tray.RefreshRequested += () => Device.RequestBattery(force: true);
            Tray.Hover += OnTrayHover;
            Tray.ExitRequested += () => { Shutdown(); };
            Tray.WebDriverRequested += () => OpenUrl(WebDriverUrl);
            Tray.AuthorRequested += () => OpenUrl(AuthorUrl);
            Tray.OsdToggled += on =>
            {
                Settings.OsdEnabled = on;
                Settings.Save();
            };
            Tray.StartupToggled += on =>
            {
                Settings.StartWithWindows = on;
                Settings.Save();
                ApplyStartup();
            };

            Tray.SyncMenuState(Settings.OsdEnabled, Settings.StartWithWindows);
            Device.Start();
            RefreshTray();
            Trace("Veloci Hub 已启动");

            // 支持 --show 直接展开控制台（便于从快捷方式/命令行直接唤出）
            if (e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
                Dispatcher.BeginInvoke(new Action(ShowFlyoutInternal), DispatcherPriority.ApplicationIdle);

            // 调试用：--osd 启动后直接播一次预警 OSD（不改动真实电量判断）
            if (e.Args.Any(a => a.Equals("--osd", StringComparison.OrdinalIgnoreCase)))
            {
                int lv = Device is { BatteryLevel: >= 0 } d ? d.BatteryLevel : 18;
                Dispatcher.BeginInvoke(
                    new Action(() => ShowOsdOnUi(lv, false, "电量偏低", $"仅剩 {lv}%，已低于 20%")),
                    DispatcherPriority.ApplicationIdle);
            }

            // 调试用：--climit 启动后播一次充电上限提醒（验证 OSD 文案与配色）
            if (e.Args.Any(a => a.Equals("--climit", StringComparison.OrdinalIgnoreCase)))
            {
                int lv = Device is { BatteryLevel: >= 0 } d2 ? d2.BatteryLevel : 80;
                int limit = Math.Clamp(Settings.ChargeLimit, 50, 100);
                Dispatcher.BeginInvoke(
                    new Action(() => ShowOsdOnUi(lv, true, "已达充电上限",
                        $"已充到 {lv}%，建议取下保护电池", "充电保护")),
                    DispatcherPriority.ApplicationIdle);
            }
        }

        /// <summary>命令行：--logo &lt;图片路径|default&gt; 直接设定软件图标（不打开面板）。</summary>
        private static void ApplyBrandIconArg(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--logo", StringComparison.OrdinalIgnoreCase));
            if (i < 0 || i + 1 >= args.Length) return;

            string v = args[i + 1];
            if (v.Equals("default", StringComparison.OrdinalIgnoreCase)) BrandIcon.Reset();
            else if (!BrandIcon.SetFrom(v)) Trace("--logo 指定的图标载入失败: " + v);
        }

        // ================= 托盘 =================
        private void RefreshTray()        {
            if (Tray == null || Device == null) return;
            string state = Device.State switch
            {
                LinkState.Online => "在线",
                LinkState.Silent => "无线休眠",
                LinkState.Searching => "检测中",
                _ => "未连接"
            };
            Tray.Update(Device.BatteryLevel, Device.Charging, state);
        }

        // ================= 轮询提速（悬停托盘 / 打开窗口） =================
        // 后台按设置值（默认 120 秒）省电；鼠标移到托盘图标上或打开控制台时，
        // 临时压到 5 秒，保证看到的是新鲜数据，交互结束自动回落。
        private const int BoostPollSeconds = 5;
        private bool _hoverBoost;
        private bool _windowBoost;
        private System.Windows.Threading.DispatcherTimer? _hoverHold;
        private System.Drawing.Point _hoverPos;

        /// <summary>鼠标移到托盘图标上。指针没离开图标就一直保持 5 秒刷新。</summary>
        private void OnTrayHover()
        {
            try { _hoverPos = System.Windows.Forms.Cursor.Position; } catch { }

            if (!_hoverBoost)
            {
                _hoverBoost = true;
                UpdatePollBoost();
            }

            if (_hoverHold == null)
            {
                _hoverHold = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(6)
                };
                _hoverHold.Tick += (_, _) =>
                {
                    _hoverHold.Stop();
                    // 静止悬停时 NotifyIcon 不会再触发 MouseMove，
                    // 但指针原地不动就说明还在图标上，续期继续保温。
                    if (PointerStillOnIcon())
                    {
                        _hoverHold.Start();
                        return;
                    }
                    _hoverBoost = false;
                    UpdatePollBoost();
                };
            }
            _hoverHold.Stop();
            _hoverHold.Start();
        }

        private bool PointerStillOnIcon()
        {
            try
            {
                var p = System.Windows.Forms.Cursor.Position;
                return Math.Abs(p.X - _hoverPos.X) <= 2 && Math.Abs(p.Y - _hoverPos.Y) <= 2;
            }
            catch { return false; }
        }

        private void UpdatePollBoost()
        {
            if (Device == null) return;
            bool boost = _hoverBoost || _windowBoost;
            int? target = boost ? BoostPollSeconds : (int?)null;
            if (Device.InteractivePollSeconds == target) return;

            Device.InteractivePollSeconds = target;
            Trace($"轮询提速：{(boost ? BoostPollSeconds + " 秒（交互）" : Math.Max(3, Settings.PollSeconds) + " 秒（后台）")}");
            if (boost) Device.RequestBattery(force: true);   // 提速即刻先拉一帧
        }

        // ================= 低电预警 =================
        private void OnBattery(int level, bool charging)
        {
            RefreshTray();
            if (level < 0) return;

            CheckChargeLimit(level, charging);

            if (charging)
            {
                _warned.Clear();
                return;
            }

            int t1 = Math.Max(2, Settings.WarnThreshold1);
            int t2 = Math.Max(1, Math.Min(Settings.WarnThreshold2, t1 - 1));
            int maxThreshold = Math.Max(t1, t2);

            if (level > maxThreshold + 8) _warned.Clear();

            var thresholds = new[] { t1, t2 }.Distinct().OrderByDescending(x => x).ToArray();
            var crossed = thresholds.Where(t => level <= t).ToList();
            if (crossed.Count == 0) return;

            int severe = crossed.Min();
            if (_warned.Contains(severe)) return;

            foreach (var c in crossed) _warned.Add(c);

            string title = level <= t2 ? "电量严重不足" : "电量偏低";
            string body = level <= 5
                ? $"仅剩 {level}%，请立即充电"
                : $"仅剩 {level}%，已低于 {severe}%";
            Trace($"低电预警: {level}% (阈值 {severe}%)");

            if (Settings.OsdEnabled) ShowOsdOnUi(level, charging, title, body);
        }

        public static void ShowOsd(int level, bool charging, string title, string? body = null)
        {
            (Current as App)?.ShowOsdOnUi(level, charging, title, body);
        }

        // ================= 充电上限保护 =================
        // MX300 的磁吸底座是纯硬件充电回路，固件没有充电阈值命令，
        // 软件无法真的断电。这里做软保护：充到设定值就用 OSD 提醒取下鼠标，
        // 避免锂电池长期停在 100% 加速衰减。
        private int _chargeLimitNotifiedAt = -1;

        private void CheckChargeLimit(int level, bool charging)
        {
            if (!charging)
            {
                // 离开充电座即视为一轮充电结束，下次放回去重新计一次
                _chargeLimitNotifiedAt = -1;
                return;
            }

            if (!Settings.ChargeLimitEnabled) return;

            int limit = Math.Clamp(Settings.ChargeLimit, 50, 100);
            if (level < limit) return;

            // 同一轮充电只在首次达到上限时提醒；若没取下、电量又涨了 5% 以上再提醒一次
            if (_chargeLimitNotifiedAt >= 0 && level < _chargeLimitNotifiedAt + 5) return;
            _chargeLimitNotifiedAt = level;

            string title = level >= 95 ? "充电已满" : "已达充电上限";
            string body = level >= 95
                ? $"{level}%，建议取下鼠标"
                : level <= limit + 1
                    ? $"已充到 {level}%，建议取下保护电池"
                    : $"{level}% 已超上限 {limit}%，建议取下";
            Trace($"充电上限保护：{level}% (上限 {limit}%)");

            if (Settings.OsdEnabled) ShowOsdOnUi(level, true, title, body, "充电保护");
        }

        // ================= DPI 切换提示 =================
        private void OnDpiChanged(int stage, int dpi)
        {
            if (!Settings.OsdEnabled) return;
            Trace($"DPI OSD：第 {stage + 1} 档 {dpi} DPI");
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => OnDpiChanged(stage, dpi));
                return;
            }
            _osd ??= new OsdWindow();
            // 控制台开着时把 OSD 挪到窗口左侧，避免互相遮挡
            bool besideFlyout = _flyout?.IsVisible == true;
            _osd.ShowDpi(stage, dpi, Math.Min(Settings.OsdSeconds, 3), besideFlyout);
        }

        private void ShowOsdOnUi(int level, bool charging, string title, string? body, string? tag = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => ShowOsdOnUi(level, charging, title, body, tag));
                return;
            }
            _osd ??= new OsdWindow();
            _osd.ShowFor(level, charging, title, Settings.OsdSeconds, body, tag);
        }

        // ================= 主窗 =================
        public static void ShowFlyout()
        {
            (Current as App)?.ShowFlyoutInternal();
        }

        private void ShowFlyoutInternal()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(ShowFlyoutInternal);
                return;
            }
            try
            {
                _osd?.HideNow();
                if (_flyout == null)
                {
                    _flyout = new FlyoutWindow();
                    _flyout.Closed += (_, _) =>
                    {
                        _flyout = null;
                        _windowBoost = false;
                        UpdatePollBoost();
                    };
                    _flyout.IsVisibleChanged += (_, _) =>
                    {
                        if (_flyout is { IsVisible: false })
                        {
                            _flyoutHiddenAt = DateTime.UtcNow;
                            _windowBoost = false;
                        }
                        else if (_flyout is { IsVisible: true })
                        {
                            _windowBoost = true;
                        }
                        UpdatePollBoost();
                    };
                }
                if (_flyout.IsVisible)
                {
                    _flyout.Hide();
                    return;
                }
                // 刚因为点击窗口外部被收起，同一次点击不要再把它弹回来
                if ((DateTime.UtcNow - _flyoutHiddenAt).TotalMilliseconds < 450) return;
                _flyout.ShowNearTray();
            }
            catch (Exception ex)
            {
                Trace("显示窗口失败: " + ex);
            }
        }

        // ================= 开机自启 =================
        public static void ApplyStartup()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;
                if (Settings.StartWithWindows)
                {
                    string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (exe.Length > 0) key.SetValue(RunValueName, "\"" + exe + "\"");
                }
                else
                {
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                    foreach (string legacy in LegacyRunValues)
                        key.DeleteValue(legacy, throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Trace("写入启动项失败: " + ex.Message);
            }
        }

        private void EnsureStartupRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;
                bool present = key.GetValue(RunValueName) != null;
                bool legacy = LegacyRunValues.Any(v => key.GetValue(v) != null);
                Settings.StartWithWindows = present || legacy;
                if (!legacy) return;

                // 旧名字的自启项：删掉旧值，用当前 exe 路径重新登记
                foreach (string v in LegacyRunValues) key.DeleteValue(v, throwOnMissingValue: false);
                string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (exe.Length > 0) key.SetValue(RunValueName, "\"" + exe + "\"");
            }
            catch { }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ================= 日志 =================
        public static void Trace(string message)
        {
            Debug.WriteLine(message);
            try
            {
                AppPaths.Ensure();
                string path = AppPaths.Log;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Trace("===== 正常退出 =====");
            try { Tray?.Dispose(); } catch { }
            try { Device?.Dispose(); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
