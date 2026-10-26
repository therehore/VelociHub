using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Mx300Hub.UI
{
    public partial class OsdWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // 设计尺寸（XAML 里 Border 的固定大小），窗口按比例缩放
        private const double CardWidth = 388;
        private const double CardHeight = 96;
        /// <summary>DPI 提示用更小的尺寸（原尺寸的 80%）。</summary>
        private const double DpiScale = 0.8;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private readonly DispatcherTimer _timer = new();
        private bool _closing;

        public OsdWindow()
        {
            InitializeComponent();
            SourceInitialized += OnSourceInitialized;
            MouseLeftButtonUp += (_, _) => { App.ShowFlyout(); HideNow(); };
            _timer.Tick += (_, _) => { _timer.Stop(); FadeOut(); };
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            IntPtr style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                new IntPtr(style.ToInt64() | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }

        public void ShowFor(int level, bool charging, string title, int seconds, string? body = null, string? tag = null)
        {
            _closing = false;

            // 电池模式
            DpiBadge.Visibility = Visibility.Collapsed;
            Ring.Visibility = Visibility.Visible;
            LevelText.Visibility = Visibility.Visible;

            Ring.Level = level;
            Ring.Charging = charging;
            LevelText.Text = level >= 0 ? $"{level}%" : "--%";
            TitleText.Text = title;
            BodyText.Text = body ?? DefaultBody(level, charging);
            StyleTag(tag ?? (charging ? "充电" : "预警"),
                     charging ? ChargeBase : WarnBase,
                     charging ? ChargeOnDark : WarnOnDark,
                     charging ? ChargeOnLight : WarnOnLight);

            ApplyTheme();
            ApplyCardScale(1.0);
            Present(seconds);
        }

        private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
            new(Color.FromArgb(a, r, g, b));

        // 标签配色：基色 / 深色模式文字 / 浅色模式文字
        private static readonly Color WarnBase = Color.FromRgb(0xFF, 0x5F, 0x66);
        private static readonly Color WarnOnDark = Color.FromRgb(0xFF, 0x9A, 0xA0);
        private static readonly Color WarnOnLight = Color.FromRgb(0xC6, 0x28, 0x32);
        private static readonly Color ChargeBase = Color.FromRgb(0x4F, 0xC3, 0xF7);
        private static readonly Color ChargeOnDark = Color.FromRgb(0x8F, 0xD6, 0xFA);
        private static readonly Color ChargeOnLight = Color.FromRgb(0x0C, 0x6E, 0x99);
        private static readonly Color DpiBase = Color.FromRgb(0x7A, 0x5C, 0xFF);
        private static readonly Color DpiOnDark = Color.FromRgb(0xC3, 0xB6, 0xFF);
        private static readonly Color DpiOnLight = Color.FromRgb(0x4A, 0x2E, 0xC4);

        private void StyleTag(string text, Color baseColor, Color onDark, Color onLight)
        {
            TagText.Text = text;
            bool light = App.Settings.LightMode;
            TagChip.Background = new SolidColorBrush(Color.FromArgb(light ? (byte)0x1F : (byte)0x26,
                baseColor.R, baseColor.G, baseColor.B));
            TagText.Foreground = new SolidColorBrush(light ? onLight : onDark);
        }

        /// <summary>OSD 卡片跟随深浅色（BodyText 用的是全局画笔，必须一起换，否则浅色下会变成深字压深底）。</summary>
        private void ApplyTheme()
        {
            if (App.Settings.LightMode)
            {
                CardBorder.Background = new LinearGradientBrush(
                    Color.FromArgb(0xF7, 0xF7, 0xF8, 0xFC), Color.FromArgb(0xF2, 0xEC, 0xEE, 0xF5),
                    new Point(0, 0), new Point(1, 1));
                CardBorder.BorderBrush = Brush(0x1A, 0x00, 0x00, 0x00);
                TitleText.Foreground = Brush(0xFF, 0x15, 0x18, 0x1F);
                LevelText.Foreground = Brush(0xFF, 0x15, 0x18, 0x1F);
                DpiValueText.Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF);
                BodyText.Foreground = Brush(0xFF, 0x4B, 0x52, 0x62);
            }
            else
            {
                CardBorder.Background = new LinearGradientBrush(
                    Color.FromArgb(0xF2, 0x14, 0x16, 0x20), Color.FromArgb(0xF2, 0x0B, 0x0D, 0x13),
                    new Point(0, 0), new Point(1, 1));
                CardBorder.BorderBrush = Brush(0x26, 0xFF, 0xFF, 0xFF);
                TitleText.Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF);
                LevelText.Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF);
                DpiValueText.Foreground = Brush(0xFF, 0xFF, 0xFF, 0xFF);
                BodyText.Foreground = Brush(0xFF, 0xA9, 0xAE, 0xBF);
            }

            // 品牌标取色跟主题走（默认 M 标在浅色底上要用深一点的绿）
            BrandMark.Refresh();
            SplashLogo.Refresh();
        }

        /// <summary>DPI 切换提示（复用同一个 OSD 窗口）。</summary>
        public void ShowDpi(int stage, int dpi, int seconds, bool besideFlyout = false)
        {
            _closing = false;

            Ring.Visibility = Visibility.Collapsed;
            LevelText.Visibility = Visibility.Collapsed;
            DpiBadge.Visibility = Visibility.Visible;

            DpiValueText.Text = dpi.ToString();
            TitleText.Text = "DPI 已切换";
            BodyText.Text = $"第 {stage + 1} 档 · {dpi} DPI";
            StyleTag("DPI", DpiBase, DpiOnDark, DpiOnLight);

            ApplyTheme();
            ApplyCardScale(DpiScale);
            Present(seconds, besideFlyout ? 432 + 34 : 0);
        }

        /// <summary>按比例缩放整张卡片（Viewbox 统一缩放，文字与圆角一起等比变小）。</summary>
        private void ApplyCardScale(double scale)
        {
            Width = CardWidth * scale;
            Height = CardHeight * scale;
        }

        private void Present(int seconds, double extraRightOffset = 0)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 20 - extraRightOffset;
            Top = area.Bottom - Height - 20;

            Host.Opacity = 0;
            Shift.Y = 16;
            if (!IsVisible) Show();
            PlayIntro();

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
            Shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });

            _timer.Stop();
            _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 30));
            _timer.Start();
        }

        private static string DefaultBody(int level, bool charging)
        {
            if (charging) return "磁吸底座正在充电";
            if (level <= 5) return "请立即充电，避免断连";
            if (level <= 15) return "建议尽快充电";
            return "记得及时充电";
        }

        private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

        /// <summary>
        /// 开场动画：品牌标旋转放大弹入 → 平移+缩小飞到右上角 → 交接给常驻标；正文同步淡入。
        /// 每段动画作用在不同属性上，避免同一属性被两次 BeginAnimation 覆盖。
        /// </summary>
        private void PlayIntro()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var easeInOut = new CubicEase { EasingMode = EasingMode.EaseInOut };
            var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.55 };

            // 清掉上一轮残留的动画，恢复起始值
            SplashLogo.BeginAnimation(OpacityProperty, null);
            SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            SplashRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            SplashShift.BeginAnimation(TranslateTransform.XProperty, null);
            SplashShift.BeginAnimation(TranslateTransform.YProperty, null);
            SplashLayer.BeginAnimation(OpacityProperty, null);
            ContentRoot.BeginAnimation(OpacityProperty, null);
            ContentShift.BeginAnimation(TranslateTransform.YProperty, null);
            BrandMark.BeginAnimation(OpacityProperty, null);

            SplashLogo.Opacity = 0;
            SplashScale.ScaleX = SplashScale.ScaleY = 0.25;
            SplashRotate.Angle = -180;
            SplashShift.X = 0;
            SplashShift.Y = 0;
            SplashLayer.Opacity = 1;
            ContentRoot.Opacity = 0;
            ContentShift.Y = 8;
            BrandMark.Opacity = 0;

            // 一阶段：标旋转 + 弹入（居中）
            SplashLogo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(150)));
            SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.25, 1, Ms(420)) { EasingFunction = pop });
            SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.25, 1, Ms(420)) { EasingFunction = pop });
            SplashRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(-180, 0, Ms(520)) { EasingFunction = ease });

            // 二阶段：标平移并缩小，落到右上角常驻标的位置
            const int flyAt = 430;
            const int flyMs = 380;
            SplashShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 169, Ms(flyMs)) { BeginTime = Ms(flyAt), EasingFunction = easeInOut });
            SplashShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, -11, Ms(flyMs)) { BeginTime = Ms(flyAt), EasingFunction = easeInOut });
            SplashScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.53, Ms(flyMs)) { BeginTime = Ms(flyAt), EasingFunction = easeInOut });
            SplashScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.53, Ms(flyMs)) { BeginTime = Ms(flyAt), EasingFunction = easeInOut });

            // 交接：飞行标淡出、常驻标淡入（位置重合，看不出切换）
            SplashLayer.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0, Ms(160)) { BeginTime = Ms(flyAt + flyMs - 60) });
            BrandMark.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, Ms(200))
                {
                    BeginTime = Ms(flyAt + flyMs - 40),
                    EasingFunction = ease
                });

            // 正文：与标错开一步浮现
            ContentRoot.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, Ms(320)) { BeginTime = Ms(470), EasingFunction = ease });
            ContentShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, Ms(400)) { BeginTime = Ms(470), EasingFunction = ease });
        }

        private void FadeOut()
        {
            if (_closing) return;
            _closing = true;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var anim = new DoubleAnimation(Host.Opacity, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            anim.Completed += (_, _) => HideNow();
            Host.BeginAnimation(OpacityProperty, anim);
            Shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }

        public void HideNow()
        {
            _timer.Stop();
            Host.BeginAnimation(OpacityProperty, null);
            Host.Opacity = 0;
            Hide();
        }
    }
}
