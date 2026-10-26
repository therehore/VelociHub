using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mx300Hub.Hid;
using Mx300Hub.Protocol;

namespace Mx300Hub.Services
{
    public enum LinkState
    {
        Disconnected,
        Searching,
        Silent,      // 已连接接口，但设备无应答（多为鼠标无线休眠）
        Online
    }

    /// <summary>负责设备发现、指令收发与状态缓存。</summary>
    public sealed class DeviceService : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<byte, DateTime> _pending = new();
        private readonly System.Windows.Threading.Dispatcher? _dispatcher;
        private readonly System.Timers.Timer _pollTimer = new(1000) { AutoReset = true };
        private readonly System.Timers.Timer _reconnectTimer = new(4000) { AutoReset = true };

        private HidChannel? _channel;
        private DateTime _lastRxAt = DateTime.MinValue;
        private DateTime _lastBatteryAt = DateTime.MinValue;
        private DateTime _lastBatteryRequestAt = DateTime.MinValue;
        private DateTime _lastBatteryLogAt = DateTime.MinValue;
        private DateTime _lastDpiPollAt = DateTime.MinValue;
        private int _silentStreak;

        // 电量读数防抖：设备电量计是电压估算，磁吸充电时电压抬升会让百分比来回跳
        private readonly List<int> _battRaw = new();
        private bool _battChargingSeen;

        // DPI 变更去重
        private int _lastDpiLevel = -1;
        private int _lastDpiValue = -1;
        private string _lastDpiSig = "";
        private DateTime _lastDpiLogAt = DateTime.MinValue;
        private DateTime _lastDpiRxAt = DateTime.MinValue;
        private int _lastActiveStage = -2;   // -2 表示尚未见过心跳里的活跃档位

        /// <summary>
        /// 交互态的临时轮询间隔（秒）。悬停托盘图标或打开控制台时由 App 设为 5，
        /// 交互结束后置回 null，恢复设置里的后台间隔。
        /// </summary>
        public int? InteractivePollSeconds { get; set; }

        public LinkState State { get; private set; } = LinkState.Disconnected;
        public HidInterfaceInfo? Interface { get; private set; }
        public List<HidInterfaceInfo> AllInterfaces { get; private set; } = new();
        public DeviceConfig? Config { get; private set; }
        public DpiSnapshot? Dpi { get; private set; }
        public string? Firmware { get; private set; }
        public int BatteryLevel { get; private set; } = -1;
        public bool Charging { get; private set; }
        public DateTime BatteryTime => _lastBatteryAt;
        public DateTime LastRxTime => _lastRxAt;
        public string LinkName { get; private set; } = "未连接";

        public event Action? StateChanged;
        public event Action<string>? Log;
        public event Action<int, bool>? BatteryUpdated;
        public event Action<int, int>? DpiChanged;   // (档位序号 0 起, DPI 值)
        public event Action? ReportsChanged;

        public DeviceService(System.Windows.Threading.Dispatcher? dispatcher = null)
        {
            _dispatcher = dispatcher;
            _pollTimer.Elapsed += (_, _) => PollTick();
            _reconnectTimer.Elapsed += (_, _) => { if (State == LinkState.Disconnected) Connect(); };
        }

        public void Start()
        {
            Connect();
            _pollTimer.Start();
            _reconnectTimer.Start();
        }

        // ---------------- 连接 ----------------
        public void Connect()
        {
            lock (_gate)
            {
                if (_channel != null) return;
            }

            try
            {
                var found = HidDeviceFinder.FindVendorControl(out var all);
                Raise(() =>
                {
                    AllInterfaces = all;
                    if (found != null)
                    {
                        Interface = found;
                        LinkName = found.VendorId == 0xA8A5 ? "2.4G 无线" : "USB 有线";
                    }
                });

                if (found == null)
                {
                    SetState(LinkState.Disconnected);
                    Emit("未发现 MX300 接收器（VID_A8A4/A8A5 · 0xFF01/0x10）");
                    return;
                }

                var ch = new HidChannel(found);
                ch.ReportReceived += OnReport;
                ch.ErrorOccurred += msg => Emit("通道:" + msg);
                ch.Start();

                lock (_gate) { _channel = ch; }
                Emit($"已打开接口 {found.VidPidText} usage {found.UsageText} in={found.InputReportLength} out={found.OutputReportLength} MI_{found.InterfaceNumber:00}");
                Emit($"枚举到 {all.Count} 个接口：" + string.Join(" | ", all.Select(i => $"MI_{i.InterfaceNumber:00}/{i.UsagePage:X4}:{i.Usage:X4}")));
                SetState(LinkState.Silent);

                // 首轮读取：版本 → 配置 → DPI → 电量
                Task.Run(async () =>
                {
                    Send(Mx300Protocol.GetFirmware());
                    await Task.Delay(160);
                    Send(Mx300Protocol.GetConfig());
                    await Task.Delay(160);
                    Send(Mx300Protocol.GetDualDpi());
                    await Task.Delay(160);
                    RequestBattery(force: true);
                });
                return;
            }

            catch (Exception ex)
            {
                Emit("连接失败: " + ex.Message);
                SetState(LinkState.Disconnected);
            }
        }

        private void CloseChannel()
        {
            HidChannel? ch;
            lock (_gate) { ch = _channel; _channel = null; }
            if (ch != null)
            {
                try { ch.Dispose(); } catch { }
            }
        }

        // ---------------- 发送 ----------------
        public bool Send(byte[] frame, byte? expectCmd = null)
        {
            var ch = _channel;
            if (ch == null)
            {
                Connect();
                ch = _channel;
                if (ch == null) return false;
            }

            lock (_gate)
            {
                if (expectCmd.HasValue) _pending[expectCmd.Value] = DateTime.UtcNow;
            }

            bool ok = ch.Send(frame);
            // 电量轮询很频繁，抑制重复日志，避免刷爆通信面板
            if (expectCmd != Mx300Protocol.CmdBattery ||
                (DateTime.UtcNow - _lastBatteryLogAt).TotalSeconds >= 30)
            {
                if (expectCmd == Mx300Protocol.CmdBattery) _lastBatteryLogAt = DateTime.UtcNow;
                Emit($"TX {Mx300Protocol.Hex(frame, 10)}");
            }
            return ok;
        }

        public void RequestBattery(bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _lastBatteryRequestAt).TotalMilliseconds < 1500) return;
            _lastBatteryRequestAt = now;
            Send(Mx300Protocol.GetBattery(), Mx300Protocol.CmdBattery);
        }

        /// <summary>
        /// 全量回读。逐条发送并留出间隔——无线链路是串行处理的，
        /// 背靠背连发 5 帧会让设备只应答第一条，后面几条直接丢。
        /// </summary>
        public void RefreshAll()
        {
            Task.Run(async () =>
            {
                Send(Mx300Protocol.GetFirmware());
                await Task.Delay(120);
                Send(Mx300Protocol.GetConfig());
                await Task.Delay(120);
                Send(Mx300Protocol.GetDualDpi());
                await Task.Delay(120);
                Send(Mx300Protocol.GetMouseStatus());
                await Task.Delay(120);
                RequestBattery(force: true);
            });
        }

        // ---------------- 写入 ----------------
        /// <summary>写后回读确认：MX300 无线休眠很激进，写入常常落空，这里自动重试几次。</summary>
        private async Task<bool> WriteAndConfirmAsync(byte[] write, byte writeCmd, byte[] read, byte readCmd, int attempts = 3)
        {
            for (int i = 0; i < attempts; i++)
            {
                Send(write, writeCmd);
                await Task.Delay(130);
                Send(read, readCmd);
                if (await WaitForResponseAsync(readCmd, 900)) return true;
                if (i < attempts - 1) await Task.Delay(280);
            }
            return false;
        }

        private async Task<bool> WaitForResponseAsync(byte cmd, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate) { if (!_pending.ContainsKey(cmd)) return true; }
                await Task.Delay(40);
            }
            return false;
        }

        public async Task<bool> UpdateConfigAsync(Action<DeviceConfig> mutate)
        {
            if (Config == null) return false;
            var cfg = Config.Clone();
            mutate(cfg);
            bool ok = await WriteAndConfirmAsync(
                Mx300Protocol.BuildConfig(cfg), Mx300Protocol.CmdSetConfig,
                Mx300Protocol.GetConfig(), Mx300Protocol.CmdGetConfig);
            if (!ok) Emit("写入配置无应答（设备可能已休眠）");
            return ok;
        }

        public async Task<bool> UpdateDpiAsync(Action<DpiSnapshot> mutate)
        {
            if (Dpi == null) return false;
            var snap = new DpiSnapshot
            {
                ProtocolMode = Dpi.ProtocolMode,
                UiMode = Dpi.UiMode,
                XyDifferent = Dpi.XyDifferent,
                CurrentLevel = Dpi.CurrentLevel,
                Mode1 = (int[])Dpi.Mode1.Clone(),
                Mode2 = (int[])Dpi.Mode2.Clone()
            };
            mutate(snap);
            bool ok = await WriteAndConfirmAsync(
                Mx300Protocol.BuildDualDpi(snap), Mx300Protocol.CmdSetDualDpi,
                Mx300Protocol.GetDualDpi(), Mx300Protocol.CmdGetDualDpi);
            if (!ok) Emit("写入 DPI 无应答（设备可能已休眠）");
            return ok;
        }

        // ---------------- 接收 ----------------
        private void OnReport(byte[] raw)
        {
            _lastRxAt = DateTime.UtcNow;
            var rep = Mx300Protocol.Parse(raw);
            if (rep == null)
            {
                Emit($"RX {Mx300Protocol.Hex(raw, 12)} (未识别)");
                return;
            }

            bool batteryChanged = false;
            lock (_gate) { _pending.Remove(rep.Command); }

            switch (rep.Kind)
            {
                case ReportKind.Battery:
                    _lastBatteryAt = DateTime.UtcNow;
                    _silentStreak = 0;
                    Emit($"RX 电量 {rep.Level}%{(rep.Charging ? " 充电中" : "")} · off={rep.HeaderOffset} · {Mx300Protocol.Hex(raw, 12)}");
                    BatteryLevel = StabilizeBattery(rep.Level, rep.Charging);
                    Charging = rep.Charging;
                    batteryChanged = true;
                    break;

                case ReportKind.Config:
                    if (rep.Config != null) Config = rep.Config;
                    Emit("RX 配置 " + Mx300Protocol.Hex(raw, 14));
                    CheckDpiChanged();
                    break;

                case ReportKind.DualDpi:
                    _lastDpiRxAt = DateTime.UtcNow;
                    if (rep.Dpi != null)
                    {
                        Dpi = rep.Dpi;
                        // 轮询时会反复读到同一份配置，只在内容变化时打日志
                        string sig = $"{rep.Dpi.CurrentLevel}|{rep.Dpi.UiMode}|" +
                                     string.Join(",", rep.Dpi.Mode1) + "|" + string.Join(",", rep.Dpi.Mode2);
                        if (sig != _lastDpiSig ||
                            (DateTime.UtcNow - _lastDpiLogAt).TotalSeconds >= 60)
                        {
                            _lastDpiSig = sig;
                            _lastDpiLogAt = DateTime.UtcNow;
                            Emit("RX DPI " + Mx300Protocol.Hex(raw, 14));
                        }
                    }
                    CheckDpiChanged();
                    break;

                case ReportKind.Firmware:
                    Firmware = rep.FirmwareVersion;
                    Emit("RX 固件 " + (rep.FirmwareVersion ?? "未知"));
                    break;

                case ReportKind.Status:
                    // 接收器心跳（0xD0）表示无线链路刚活跃：立刻补拉一次电量
                    Emit("RX 状态/心跳");
                    // 心跳里带有「当前活跃 DPI 档位」。按下鼠标实体 DPI 键时设备不会
                    // 主动推报文，但下一帧心跳会带出新档位，据此立即回读一次实现秒级响应。
                    // 注意：同一字段在部分心跳帧里是别的量（实测出现过 88/90 这类值），
                    // 所以只有落在 0–5 的才当成档位，避免误触发无谓回读。
                    if (rep.ActiveStage != _lastActiveStage)
                    {
                        _lastActiveStage = rep.ActiveStage;
                        bool isStage = rep.ActiveStage >= 0 && rep.ActiveStage <= 5;
                        if (isStage && _lastDpiLevel >= 0 && rep.ActiveStage != _lastDpiLevel)
                        {
                            Emit($"心跳档位 {rep.ActiveStage + 1}（本地记录 {_lastDpiLevel + 1}）");
                            Task.Run(() => { Thread.Sleep(40); Send(Mx300Protocol.GetDualDpi()); });
                        }
                    }
                    if ((DateTime.UtcNow - _lastBatteryAt).TotalSeconds > 20)
                        Task.Run(() => { Thread.Sleep(60); RequestBattery(); });
                    break;

                case ReportKind.KeyEvent:
                    Emit("RX 按键事件（板载切换）");
                    Task.Run(() => { Thread.Sleep(120); RefreshAll(); });
                    break;

                default:
                    Emit($"RX 0x{rep.Command:X2} {Mx300Protocol.Hex(raw, 12)}");
                    break;
            }

            SetState(LinkState.Online);

            if (batteryChanged)
                Raise(() => BatteryUpdated?.Invoke(BatteryLevel, Charging));
            Raise(() => ReportsChanged?.Invoke());
        }

        // ---------------- 轮询 ----------------
        private void PollTick()
        {
            var now = DateTime.UtcNow;

            // 有请求超时未响应
            bool timeouts;
            lock (_gate)
            {
                timeouts = _pending.Any(kv => (now - kv.Value).TotalSeconds > 2.0);
                if (timeouts)
                {
                    foreach (var k in _pending.Where(kv => (now - kv.Value).TotalSeconds > 2.0).Select(kv => kv.Key).ToList())
                        _pending.Remove(k);
                }
            }

            if (timeouts)
            {
                _silentStreak++;
                if (_silentStreak >= 2 && State == LinkState.Online)
                {
                    SetState(LinkState.Silent);
                    Emit("设备无应答（无线休眠或未唤醒）");
                }
                else if (_silentStreak >= 6 && State != LinkState.Silent)
                {
                    SetState(LinkState.Silent);
                }
            }

            // 电量轮询：后台走设置值（默认 120 秒），
            // 悬停托盘/打开窗口期间由 App 临时压到 5 秒，保证看到的是新鲜数据。
            // PollTick 是 1 秒粒度，判定时提前半秒，否则「每 5 秒」实际会变成 6 秒。
            var interval = Math.Max(3, InteractivePollSeconds ?? App.Settings.PollSeconds);
            if ((now - _lastBatteryAt).TotalSeconds >= interval - 0.55 ||
                (_lastBatteryAt == DateTime.MinValue && (now - _lastBatteryRequestAt).TotalSeconds >= 3))
            {
                RequestBattery();
            }

            // DPI 档位轮询。
            // 鼠标实体的 DPI 键是纯硬件切换，设备既不发按键报文也不发状态帧，
            // 只能主动回读 0x61 才能发现档位变化——这正是不轮询就没有 OSD 的原因。
            // 心跳（0xFA）里带了活跃档位，正常切换由心跳即时触发回读；
            // 这里只是兜底，所以间隔放宽，避免无谓占用无线链路和妨碍鼠标休眠。
            if (State != LinkState.Disconnected)
            {
                double idleSec = (DateTime.UtcNow - _lastDpiRxAt).TotalSeconds;
                int dpiPollMs = idleSec > 10 ? 5000 : 2500;
                if ((now - _lastDpiPollAt).TotalMilliseconds >= dpiPollMs)
                {
                    _lastDpiPollAt = now;
                    Send(Mx300Protocol.GetDualDpi());
                }
            }

            // 链路断开检测
            if (_lastRxAt != DateTime.MinValue && (now - _lastRxAt).TotalSeconds > 90 && State == LinkState.Online)
            {
                SetState(LinkState.Silent);
            }
        }

        // ---------------- 工具 ----------------
        /// <summary>
        /// 电量防抖。MX300 的电量计按电压估算，磁吸底座充电时电压被抬高，
        /// 会出现 72% → 80% → 77% 这类来回跳动。这里取最近三次读数的中位数，
        /// 既压掉毛刺又不至于像滑动平均那样迟钝。
        /// </summary>
        private int StabilizeBattery(int raw, bool charging)
        {
            // 充电状态切换时清空样本，避免把两种状态下的读数混在一起
            if (charging != _battChargingSeen)
            {
                _battChargingSeen = charging;
                _battRaw.Clear();
            }

            _battRaw.Add(raw);
            while (_battRaw.Count > 3) _battRaw.RemoveAt(0);

            if (_battRaw.Count == 1) return raw;

            var sorted = _battRaw.OrderBy(x => x).ToArray();
            int median = sorted[(sorted.Length - 1) / 2];
            if (median != raw)
                Emit($"电量平稳：本帧 {raw}%，输出 {median}%（原始 {string.Join("/", _battRaw)}）");
            return median;
        }

        /// <summary>当前生效 DPI 发生变化时（改设置或按鼠标实体键）触发 OSD。</summary>
        private void CheckDpiChanged()
        {
            var d = Dpi;
            if (d == null) return;

            int level = Math.Clamp(d.CurrentLevel, 0, 5);
            var stages = d.UiMode == 2 ? d.Mode2 : d.Mode1;
            int value = (level >= 0 && level < stages.Length) ? stages[level] : 0;
            if (value <= 0) return;

            if (level == _lastDpiLevel && value == _lastDpiValue) return;

            bool first = _lastDpiValue < 0;
            _lastDpiLevel = level;
            _lastDpiValue = value;
            if (first) return;   // 首次读取只记基线，不弹窗

            Emit($"DPI 变更：第 {level + 1} 档 {value} DPI");
            Raise(() => DpiChanged?.Invoke(level, value));
        }

        private void SetState(LinkState s)
        {
            if (State == s) return;
            State = s;
            Raise(() => StateChanged?.Invoke());
        }

        private void Emit(string msg)
        {
            Raise(() => Log?.Invoke($"{DateTime.Now:HH:mm:ss.fff}  {msg}"));
        }

        private void Raise(Action action)
        {
            try
            {
                if (_dispatcher != null && !_dispatcher.CheckAccess())
                    _dispatcher.BeginInvoke(action);
                else
                    action();
            }
            catch { }
        }

        public void Dispose()
        {
            _pollTimer.Stop();
            _reconnectTimer.Stop();
            CloseChannel();
        }
    }
}
