using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Mx300Hub.Protocol;
using Mx300Hub.Services;

namespace Mx300Hub.UI
{
    public partial class FlyoutWindow : Window
    {
        private readonly StringBuilder _log = new();
        // 构造函数期间（XAML 加载时）滑块/开关的默认值会触发 Changed 事件，
        // 此时后续控件尚未创建，用 _syncing 屏蔽掉这些回调。
        private bool _syncing = true;
        private readonly List<DpiRowUi> _dpiRows = new();
        private int[] _dpiEdit = new int[6];
        private int _dpiActive;
        private int _dpiUiMode = 1;
        private DpiSnapshot? _lastDpiSnapshot;
        private DateTime _shownAt = DateTime.MinValue;

        /// <summary>true 时失焦/点击窗口外自动收起；由 --show 启动时置为 false。</summary>
        public bool AutoHide { get; set; } = true;

        // 点击窗口外部关闭：轮询全局鼠标状态，比依赖 Deactivated 可靠
        // （无边框 + Topmost 的窗口经常抢不到前台焦点，Activated/Deactivated 会漏事件）
        private readonly System.Windows.Threading.DispatcherTimer _outsideClick =
            new() { Interval = TimeSpan.FromMilliseconds(90) };
        private DateTime _outsidePressAt = DateTime.MinValue;

        public FlyoutWindow()
        {
            InitializeComponent();
            SourceInitialized += (_, _) => ApplyBackdrop();
            Deactivated += (_, _) =>
            {
                if (!AutoHide) return;
                if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 400) return;
                if (!IsPointerInside()) Hide();
            };
            _outsideClick.Tick += (_, _) => CheckOutsideClick();
            PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) { _outsidePressAt = DateTime.MinValue; _outsideClick.Start(); }
                else _outsideClick.Stop();
            };
            BuildDpiRows();
            LoadSettingsToUi();
            ApplyAppearance();
            ApplyTab(App.Settings.LastTab);
            SubscribeDevice();
            SyncUi();
        }

        /// <summary>检测「在窗口外面完成的一次点击」，完成即收起。</summary>
        private void CheckOutsideClick()
        {
            if (!IsVisible || !AutoHide) return;
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 400) return;

            // 用坐标判断而不是 IsMouseOver：列表重绘、控件重建时 IsMouseOver 会短暂为 false，
            // 用它会把「在窗口里点一下」误判成点击外部。
            if (IsPointerInside())
            {
                _outsidePressAt = DateTime.MinValue;
                return;
            }

            bool pressed = Mouse.LeftButton == MouseButtonState.Pressed
                        || Mouse.RightButton == MouseButtonState.Pressed
                        || Mouse.MiddleButton == MouseButtonState.Pressed;

            if (pressed)
            {
                if (_outsidePressAt == DateTime.MinValue) _outsidePressAt = DateTime.UtcNow;
                return;
            }

            if (_outsidePressAt != DateTime.MinValue)
            {
                // 按下与抬起都在窗口外，且间隔合理 → 视为一次点击外部
                bool complete = (DateTime.UtcNow - _outsidePressAt).TotalMilliseconds < 1500;
                _outsidePressAt = DateTime.MinValue;
                if (complete) Hide();
            }
        }

        private bool IsPointerInside()
        {
            if (IsMouseOver) return true;
            var p = Mouse.GetPosition(this);
            return p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight;
        }

        private void ApplyTab(string? key)
        {
            switch (key)
            {
                case "dpi": TabDpi.IsChecked = true; break;
                case "light": TabLight.IsChecked = true; break;
                case "device": TabDevice.IsChecked = true; break;
                default: TabPerf.IsChecked = true; break;
            }
            PanelPerf.Visibility = TabPerf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelDpi.Visibility = TabDpi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelLight.Visibility = TabLight.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelDevice.Visibility = TabDevice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private DeviceService Dev => App.Device;

        // ================= 生命周期 =================
        public void ShowNearTray()
        {
            var area = SystemParameters.WorkArea;
            double h = Math.Min(Height, area.Height - 32);
            Height = h;
            Left = area.Right - Width - 12;
            Top = area.Bottom - h - 12;
            _shownAt = DateTime.UtcNow;
            Show();
            Activate();
            Topmost = true;
            Dev?.RefreshAll();
            App.Trace($"Flyout 定位 Left={Left:F1} Top={Top:F1} W={ActualWidth:F1} H={ActualHeight:F1} " +
                      $"WorkArea={area.Left:F0},{area.Top:F0},{area.Right:F0},{area.Bottom:F0}");
        }

        private void SubscribeDevice()
        {
            if (Dev == null) return;
            Dev.StateChanged += () => SyncUi();
            Dev.ReportsChanged += () => SyncUi();
            Dev.BatteryUpdated += (_, _) => SyncUi();
            Dev.Log += AppendLog;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Hide();

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Dev?.RefreshAll();
            AppendLog("手动刷新全部状态");
        }

        private void ReadBattery_Click(object sender, RoutedEventArgs e) => Dev?.RequestBattery(force: true);

        private void TestOsd_Click(object sender, RoutedEventArgs e)
        {
            int level = Dev?.BatteryLevel >= 0 ? Dev!.BatteryLevel : Math.Max(1, App.Settings.WarnThreshold2);
            App.ShowOsd(level, false, "预警测试",
                $"低于 {App.Settings.WarnThreshold1}% 时自动弹出");
        }

        // ================= 导航 =================
        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PanelPerf == null) return;
            PanelPerf.Visibility = TabPerf.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelDpi.Visibility = TabDpi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelLight.Visibility = TabLight.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            PanelDevice.Visibility = TabDevice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

            App.Settings.LastTab = TabPerf.IsChecked == true ? "perf"
                : TabDpi.IsChecked == true ? "dpi"
                : TabLight.IsChecked == true ? "light" : "device";
        }

        // ================= 性能 =================
        private async void Rate_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            if (!int.TryParse(tag, out int rate)) return;
            int code = Mx300Protocol.CodeFromPolling(rate);
            await ApplyConfigAsync($"回报率 {rate}Hz", c => c.PollingCode = code);
        }

        private async void Sensor_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            byte flags = 0;
            if (SwAngle.IsChecked == true) flags |= 0x01;
            if (SwRipple.IsChecked == true) flags |= 0x10;
            if (SwMotionSync.IsChecked == true) flags |= 0x20;
            await ApplyConfigAsync($"传感器标志 0x{flags:X2}", c => c.SensorFlags = flags);
        }

        private async void Scroll_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            bool on = SwScroll.IsChecked == true;
            await ApplyConfigAsync("滚轮反向 " + (on ? "开" : "关"), c => c.ScrollReverse = on);
        }

        private async void Lod_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            int lod = tag == "2" ? 2 : 1;
            await ApplyConfigAsync($"LOD {lod}mm", c => c.Lod = lod);
        }

        private async void Debounce_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing || Dev?.Config == null) return;
            int v = (int)Math.Round(e.NewValue);
            DebounceText.Text = $"{v} ms";
            await ApplyConfigAsync($"按键响应 {v}ms", c => c.Debounce = v);
        }

        private async void Sleep_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing || Dev?.Config == null) return;
            int v = (int)Math.Round(e.NewValue);
            SleepText.Text = v == 0 ? "从不休眠" : $"{v} 分钟";
            await ApplyConfigAsync(v == 0 ? "从不休眠" : $"休眠 {v} 分钟", c => c.SleepTime = v);
        }

        private async void HighSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            bool on = SwHighSpeed.IsChecked == true;
            await ApplyConfigAsync("高速模式 " + (on ? "开" : "关"), c => c.HighSpeed = on);
        }

        private async void Wake_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            int mode = tag == "1" ? 1 : 0;
            await ApplyConfigAsync("唤醒方式 " + (mode == 1 ? "移动" : "点击"), c => c.WakeMode = mode);
        }

        // ================= DPI =================
        private sealed class DpiRowUi
        {
            public Border Host = null!;
            public RadioButton Active = null!;
            public TextBox Value = null!;
        }

        private void BuildDpiRows()
        {
            for (int i = 0; i < 6; i++)
            {
                int index = i;
                var host = new Border
                {
                    Style = (Style)FindResource("Card"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var active = new RadioButton
                {
                    GroupName = "dpiactive",
                    Style = (Style)FindResource("SegBtn"),
                    Width = 24,
                    Height = 22,
                    Padding = new Thickness(0),
                    MinWidth = 0,
                    ToolTip = "设为当前生效档位",
                    Content = new TextBlock { Text = (i + 1).ToString(), FontSize = 11 }
                };
                active.Click += (_, _) => { _dpiActive = index; RefreshDpiRowStyles(); };
                Grid.SetColumn(active, 0);
                grid.Children.Add(active);

                var value = new TextBox
                {
                    Text = "--",
                    Width = 92,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    FontFamily = (FontFamily)FindResource("MonoFont"),
                    FontSize = 17,
                    TextAlignment = TextAlignment.Right,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                // 文字色随主题走（浅色模式下白色数字会看不见）
                value.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                value.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);                value.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { CommitDpiRow(index); Keyboard.ClearFocus(); }
                };
                value.LostFocus += (_, _) => CommitDpiRow(index);
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);

                var minus = MakeStepButton("−");
                minus.Click += (_, _) => StepDpi(index, -1);
                Grid.SetColumn(minus, 2);
                grid.Children.Add(minus);

                var plus = MakeStepButton("+");
                plus.Click += (_, _) => StepDpi(index, +1);
                plus.Margin = new Thickness(4, 0, 0, 0);
                Grid.SetColumn(plus, 3);
                grid.Children.Add(plus);

                host.Child = grid;
                DpiList.Children.Add(host);
                _dpiRows.Add(new DpiRowUi { Host = host, Active = active, Value = value });
            }
        }

        private Button MakeStepButton(string text)
        {
            return new Button
            {
                Content = text,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                FontSize = 15,
                Style = (Style)FindResource("GhostBtn")
            };
        }

        private void StepDpi(int index, int direction)
        {
            int v = _dpiEdit[index];
            if (v <= 0) v = 800;
            int step = v < 1000 ? 50 : v < 5000 ? 100 : 500;
            v = Math.Clamp(v + direction * step, 100, 26000);
            _dpiEdit[index] = v;
            _dpiRows[index].Value.Text = v.ToString(CultureInfo.InvariantCulture);
        }

        private void CommitDpiRow(int index)
        {
            if (_syncing) return;
            var txt = _dpiRows[index].Value.Text.Trim();
            if (int.TryParse(txt, out int v))
            {
                _dpiEdit[index] = Math.Clamp(v, 100, 26000);
            }
            _dpiRows[index].Value.Text = _dpiEdit[index] > 0
                ? _dpiEdit[index].ToString(CultureInfo.InvariantCulture) : "--";
        }

        private void RefreshDpiRowStyles()
        {
            for (int i = 0; i < _dpiRows.Count; i++)
            {
                var row = _dpiRows[i];
                row.Active.IsChecked = i == _dpiActive;
                bool has = _dpiEdit[i] > 0;
                row.Host.Opacity = has ? 1.0 : 0.45;
                if (i == _dpiActive)
                {
                    row.Host.BorderBrush = (Brush)FindResource("AccentBrush");
                    row.Host.Background = (Brush)FindResource("AccentSoftBrush");
                }
                else
                {
                    row.Host.BorderBrush = (Brush)FindResource("StrokeBrush");
                    row.Host.Background = (Brush)FindResource("CardBrush");
                }
            }
        }

        private async void DpiApply_Click(object sender, RoutedEventArgs e)
        {
            if (Dev?.Dpi == null) { AppendLog("尚未读取到 DPI，无法写入"); return; }
            var values = (int[])_dpiEdit.Clone();
            int active = _dpiActive;
            int mode = _dpiUiMode;
            bool ok = await Dev.UpdateDpiAsync(s =>
            {
                s.UiMode = mode;
                s.CurrentLevel = active;
                if (mode == 2) s.Mode2 = values; else s.Mode1 = values;
            });
            AppendLog(ok
                ? $"已写入 DPI 模式{mode} 当前档{active + 1}：{string.Join(" / ", values)}"
                : "写入 DPI 失败：设备无应答，请先移动鼠标唤醒后再试");
        }

        private void DpiReload_Click(object sender, RoutedEventArgs e)
        {
            Dev?.Send(Mx300Protocol.GetDualDpi(), Mx300Protocol.CmdGetDualDpi);
        }

        private async void DpiMode_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            int mode = tag == "2" ? 2 : 1;
            _dpiUiMode = mode;
            LoadDpiFromDevice();
            if (Dev?.Dpi != null)
                await Dev.UpdateDpiAsync(s => { s.UiMode = mode; });
        }

        private void LoadDpiFromDevice()
        {
            var dpi = Dev?.Dpi;
            _syncing = true;
            if (dpi != null)
            {
                var src = _dpiUiMode == 2 ? dpi.Mode2 : dpi.Mode1;
                for (int i = 0; i < 6; i++) _dpiEdit[i] = src.Length > i ? src[i] : 0;
                _dpiActive = Math.Clamp(dpi.CurrentLevel, 0, 5);
            }
            for (int i = 0; i < 6; i++)
                _dpiRows[i].Value.Text = _dpiEdit[i] > 0 ? _dpiEdit[i].ToString(CultureInfo.InvariantCulture) : "--";
            ModeShoot.IsChecked = _dpiUiMode != 2;
            ModeHigh.IsChecked = _dpiUiMode == 2;
            DpiHint.Text = dpi == null ? "未读取" : $"模式 {_dpiUiMode} · 当前第 {_dpiActive + 1} 档";
            RefreshDpiRowStyles();
            _syncing = false;
        }

        // ================= 灯效 =================
        private async void Light_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            bool on = SwLight.IsChecked == true;
            byte fx = on ? (byte)(Dev.Config.LightEffect == 0 ? 1 : Dev.Config.LightEffect) : (byte)0;
            await ApplyConfigAsync("灯效开关 " + (on ? "开" : "关"), c => c.LightEffect = fx);
        }

        private async void LightFx_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing || Dev?.Config == null) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            byte fx = byte.Parse(tag);
            await ApplyConfigAsync("灯效效果 " + fx, c => c.LightEffect = fx);
        }

        /// <summary>统一处理配置写入：成功/失败都给出明确日志。</summary>
        private async Task ApplyConfigAsync(string label, Action<DeviceConfig> mutate)
        {
            bool ok = await Dev.UpdateConfigAsync(mutate);
            AppendLog(ok ? $"已应用：{label}" : $"写入失败：{label}（设备无应答，请先移动鼠标唤醒）");
        }

        private void OpenWebDriverLight_Click(object sender, RoutedEventArgs e) => OpenUrl(App.WebDriverUrl);

        // ================= 设置 =================
        private void LoadSettingsToUi()
        {
            _syncing = true;
            var s = App.Settings;
            Warn1Slider.Value = s.WarnThreshold1;
            Warn2Slider.Value = s.WarnThreshold2;
            PollSlider.Value = s.PollSeconds;
            SwOsd.IsChecked = s.OsdEnabled;
            SwStartup.IsChecked = s.StartWithWindows;
            Warn1Text.Text = $"{s.WarnThreshold1}%";
            Warn2Text.Text = $"{s.WarnThreshold2}%";
            PollText.Text = FormatPoll(s.PollSeconds);
            VerLine.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

            int pct = (int)Math.Round(Math.Clamp(s.BgOpacity, 0.0, 1.0) * 100);
            BgOpacitySlider.Value = pct;
            BgOpacityText.Text = $"{pct}%";
            SegLight.IsChecked = s.LightMode;
            SegDark.IsChecked = !s.LightMode;

            SwChargeLimit.IsChecked = s.ChargeLimitEnabled;
            ChargeLimitSlider.Value = Math.Clamp(s.ChargeLimit, 55, 95);
            ChargeLimitText.Text = $"{Math.Clamp(s.ChargeLimit, 55, 95)}%";

            _syncing = false;
            UpdateChargeLimitState();
            UpdateLogoState();
        }

        // ================= 外观（不透明度 / 深浅色） =================
        /// <summary>外部（如托盘菜单/命令行）改了外观设置后调用：同步控件再重绘。</summary>
        public void ReloadAppearance()
        {
            int pct = (int)Math.Round(Math.Clamp(App.Settings.BgOpacity, 0.0, 1.0) * 100);
            _syncing = true;
            SegLight.IsChecked = App.Settings.LightMode;
            SegDark.IsChecked = !App.Settings.LightMode;
            BgOpacitySlider.Value = pct;
            BgOpacityText.Text = $"{pct}%";
            _syncing = false;
            ApplyAppearance();
        }

        private void ApplyAppearance()
        {
            Theme.Apply(App.Settings.LightMode);
            ApplyBackground();
            // 电量环与品牌标的取色是手绘的，换肤后要主动重绘
            try { Ring?.InvalidateVisual(); } catch { }
            UpdateLogoState();
        }

        /// <summary>只改变背景浓度：重建窗口渐变 + 同步亚克力 tint。</summary>
        private void ApplyBackground()
        {
            double op = Math.Clamp(App.Settings.BgOpacity, 0.0, 1.0);
            if (Root != null) Root.Background = Theme.WindowBackground(op, App.Settings.LightMode);
            ApplyBackdrop();
        }

        private void ApplyBackdrop()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            double op = Math.Clamp(App.Settings.BgOpacity, 0.0, 1.0);
            bool light = App.Settings.LightMode;
            App.Trace("背景: " + Backdrop.Apply(this, Theme.AcrylicAlpha(op), Theme.Tint(light), !light) +
                      $" · 不透明度 {op * 100:F0}% · {(light ? "浅色" : "深色")}");
        }

        private void BgOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            int pct = (int)Math.Round(e.NewValue);
            if (BgOpacityText != null) BgOpacityText.Text = $"{pct}%";
            if (_syncing) return;
            App.Settings.BgOpacity = pct / 100.0;
            App.Settings.Save();
            ApplyBackground();
        }

        private void ThemeMode_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            bool light = SegLight.IsChecked == true;
            if (App.Settings.LightMode == light) return;
            App.Settings.LightMode = light;
            App.Settings.Save();
            ApplyAppearance();
            AppendLog(light ? "已切换到浅色模式" : "已切换到深色模式");
        }

        private void Warn1_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing) return;
            int v = (int)Math.Round(e.NewValue);
            Warn1Text.Text = $"{v}%";
            App.Settings.WarnThreshold1 = v;
            App.Settings.Save();
        }

        private void Warn2_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing) return;
            int v = (int)Math.Round(e.NewValue);
            Warn2Text.Text = $"{v}%";
            App.Settings.WarnThreshold2 = v;
            App.Settings.Save();
        }

        private void Poll_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing) return;
            int v = (int)Math.Round(e.NewValue);
            PollText.Text = FormatPoll(v);
            App.Settings.PollSeconds = v;
            App.Settings.Save();
            UpdateFooter();
        }

        /// <summary>秒数超过一分钟就按分钟显示，120 秒写成「2 分钟」更好认。</summary>
        private static string FormatPoll(int sec) =>
            sec >= 60 ? $"{sec / 60.0:0.#} 分钟" : $"{sec} 秒";

        private void Osd_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            App.Settings.OsdEnabled = SwOsd.IsChecked == true;
            App.Settings.Save();
        }

        // ---- 充电上限保护 ----
        private void ChargeLimit_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            App.Settings.ChargeLimitEnabled = SwChargeLimit.IsChecked == true;
            App.Settings.Save();
            UpdateChargeLimitState();
        }

        private void ChargeLimit_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_syncing) return;
            int v = (int)Math.Round(e.NewValue);
            ChargeLimitText.Text = $"{v}%";
            App.Settings.ChargeLimit = v;
            App.Settings.Save();
            UpdateChargeLimitState();
        }

        private void UpdateChargeLimitState()
        {
            if (ChargeLimitSlider == null || ChargeLimitHint == null) return;
            bool on = SwChargeLimit.IsChecked == true;
            ChargeLimitSlider.IsEnabled = on;
            ChargeLimitText.Opacity = on ? 1.0 : 0.45;
            ChargeLimitHint.Opacity = on ? 1.0 : 0.45;

            int limit = (int)Math.Round(ChargeLimitSlider.Value);
            int level = App.Device?.BatteryLevel ?? -1;
            bool charging = App.Device?.Charging == true;

            ChargeLimitHint.Text = !on
                ? "关闭后不做充电提醒"
                : charging && level >= limit
                    ? $"当前 {level}%，已超过上限，可以取下了"
                    : $"充到 {limit}% 时提醒取下，避免长期满电";
        }

        private void Startup_Click(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            App.Settings.StartWithWindows = SwStartup.IsChecked == true;
            App.Settings.Save();
            App.ApplyStartup();
        }

        // ================= 软件图标（默认 M 标 / 自定义图片） =================
        private void PickLogo_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择软件图标",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|所有文件|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;

            if (BrandIcon.SetFrom(dlg.FileName))
            {
                UpdateLogoState();
                AppendLog("已更换软件图标：" + System.IO.Path.GetFileName(dlg.FileName));
            }
            else
            {
                AppendLog("图标载入失败：格式不支持、文件损坏或超过 8MB");
            }
        }

        private void ResetLogo_Click(object sender, RoutedEventArgs e)
        {
            if (!BrandIcon.HasCustom) return;
            BrandIcon.Reset();
            UpdateLogoState();
            AppendLog("已恢复默认 M 标");
        }

        /// <summary>同步图标相关控件的状态（预览、按钮文案、能否恢复默认）。</summary>
        private void UpdateLogoState()
        {
            bool custom = BrandIcon.HasCustom;
            if (ResetLogoBtn != null) ResetLogoBtn.IsEnabled = custom;
            if (PickLogoBtn != null) PickLogoBtn.Content = custom ? "更换…" : "自定义…";
            LogoSample?.Refresh();
            HeaderLogo?.Refresh();
        }

        private void OpenWebDriver_Click(object sender, RoutedEventArgs e) => OpenUrl(App.WebDriverUrl);
        private void OpenPairing_Click(object sender, RoutedEventArgs e) => OpenUrl(App.PairingUrl);

        private void CopyDiag_Click(object sender, RoutedEventArgs e)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Waaagh Hub 诊断信息");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"系统: {Environment.OSVersion}");
            sb.AppendLine($"状态: {Dev?.State}");
            sb.AppendLine($"电量: {Dev?.BatteryLevel}% 充电: {Dev?.Charging}");
            sb.AppendLine($"固件: {Dev?.Firmware ?? "未读取"}");
            sb.AppendLine($"接口: {Dev?.Interface}");
            sb.AppendLine("---- 接口列表 ----");
            foreach (var i in Dev?.AllInterfaces ?? new List<Hid.HidInterfaceInfo>())
                sb.AppendLine("  " + i);
            sb.AppendLine("---- 日志 ----");
            sb.AppendLine(_log.ToString());
            try { Clipboard.SetText(sb.ToString()); AppendLog("诊断信息已复制到剪贴板"); } catch { }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        // ================= 状态同步 =================
        private void SyncUi()
        {
            var d = Dev;
            if (d == null) return;

            Ring.Level = d.BatteryLevel;
            Ring.Charging = d.Charging;
            LevelText.Text = d.BatteryLevel >= 0 ? d.BatteryLevel.ToString() : "--";
            LevelUnit.Text = d.BatteryLevel >= 0 ? "%" : "";

            ChargeChip.Visibility = d.Charging ? Visibility.Visible : Visibility.Collapsed;
            ChargeChipText.Text = d.Charging ? "充电中" : "";

            // 充到上限时把提示文案改成「可以取下了」
            UpdateChargeLimitState();

            LinkChipText.Text = d.State switch
            {
                LinkState.Online => "在线",
                LinkState.Silent => "无线休眠",
                LinkState.Searching => "检测中",
                _ => "未连接"
            };
            LinkChip.Background = d.State == LinkState.Online
                ? new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0xDC, 0x97))
                : new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xC2, 0x4D));

            SubtitleText.Text = d.Interface == null
                ? "未检测到接收器"
                : $"MX300 · {d.LinkName}";

            BatteryHint.Text = d.State switch
            {
                LinkState.Online => d.Charging ? "已连接电源，正在充电" : RemainingHint(d.BatteryLevel),
                LinkState.Silent => "鼠标处于无线休眠，移动一下鼠标即可刷新",
                _ => "请插入 2.4G 接收器或 USB 数据线"
            };

            UpdatedText.Text = d.BatteryTime == DateTime.MinValue
                ? "尚未读取到电量"
                : $"更新于 {d.BatteryTime.ToLocalTime():HH:mm:ss}";

            if (d.Config != null)
            {
                _syncing = true;
                Rate125.IsChecked = d.Config.PollingRate == 125;
                Rate250.IsChecked = d.Config.PollingRate == 250;
                Rate500.IsChecked = d.Config.PollingRate == 500;
                Rate1000.IsChecked = d.Config.PollingRate == 1000;

                SwMotionSync.IsChecked = d.Config.MotionSync;
                SwRipple.IsChecked = d.Config.RippleControl;
                SwAngle.IsChecked = d.Config.AngleSnapping;
                SwScroll.IsChecked = d.Config.ScrollReverse;
                Lod1.IsChecked = d.Config.Lod != 2;
                Lod2.IsChecked = d.Config.Lod == 2;

                DebounceSlider.Value = Math.Clamp(d.Config.Debounce, 0, 30);
                DebounceText.Text = $"{d.Config.Debounce} ms";
                SleepSlider.Value = Math.Clamp(d.Config.SleepTime, 0, 30);
                SleepText.Text = d.Config.SleepTime == 0 ? "从不休眠" : $"{d.Config.SleepTime} 分钟";

                SwHighSpeed.IsChecked = d.Config.HighSpeed;
                WakeClick.IsChecked = d.Config.WakeMode != 1;
                WakeMove.IsChecked = d.Config.WakeMode == 1;

                bool lightOn = d.Config.LightEffect != 0;
                SwLight.IsChecked = lightOn;
                LightOff.IsChecked = !lightOn;
                LightStatic.IsChecked = lightOn && d.Config.LightEffect == 1;
                LightBreath.IsChecked = lightOn && d.Config.LightEffect == 2;
                LightCurrent.Text = !lightOn ? "已关闭"
                    : d.Config.LightEffect == 1 ? "常亮"
                    : d.Config.LightEffect == 2 ? "呼吸"
                    : $"自定义效果 0x{d.Config.LightEffect:X2}";
                _syncing = false;

                PerfHint.Text = "";
            }
            else
            {
                PerfHint.Text = d.State == LinkState.Silent
                    ? "读取配置需要先唤醒鼠标" : "等待设备配置…";
            }

            FwText.Text = d.Firmware ?? "--";
            LinkText.Text = d.Interface == null ? "--" : d.LinkName;
            VidText.Text = d.Interface?.VidPidText ?? "--";
            UsageText.Text = d.Interface == null ? "--" : $"{d.Interface.UsageText} · MI_{d.Interface.InterfaceNumber:00}";

            if (d.Dpi != null && !ReferenceEquals(_lastDpiSnapshot, d.Dpi))
            {
                _lastDpiSnapshot = d.Dpi;
                bool wasSyncing = _syncing;
                LoadDpiFromDevice();
                _syncing = wasSyncing;
            }

            UpdateFooter();
        }

        private void UpdateFooter()
        {
            if (StatusLine == null) return;
            var d = Dev;
            string bg = FormatPoll(Math.Max(3, App.Settings.PollSeconds));
            StatusLine.Text = d?.State == LinkState.Online
                ? $"后台 {bg} · 面板内 5 秒"
                : $"等待设备 · 后台 {bg} · 面板内 5 秒";
        }

        /// <summary>与托盘环形图标同一套四档分界（80 / 60 / 30）。</summary>
        private static string RemainingHint(int level)
        {
            if (level < 0) return "等待设备应答…";
            if (level >= 80) return "电量充足";
            if (level >= 60) return "电量良好";
            if (level >= 30) return "电量偏低，注意补充";
            if (level >= 10) return "电量告急，建议尽快充电";
            return "电量极低，请立即充电";
        }

        private void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendLog(line)); return; }
            _log.AppendLine(line);
            if (_log.Length > 12000) _log.Remove(0, _log.Length - 9000);
            LogText.Text = _log.ToString();
            LogScroll?.ScrollToEnd();
        }
    }
}
