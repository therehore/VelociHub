using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace Mx300Hub.Services
{
    /// <summary>托盘图标 + 右键菜单。图标按电量实时重绘。</summary>
    public sealed class TrayService : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly WF.NotifyIcon _icon;
        private readonly WF.ContextMenuStrip _menu;
        private readonly WF.ToolStripMenuItem _osdItem;
        private readonly WF.ToolStripMenuItem _startupItem;
        private readonly Dispatcher _dispatcher;
        private int _lastLevel = -2;
        private bool _lastCharging;

        public event Action? OpenRequested;
        public event Action? RefreshRequested;
        public event Action? Hover;
        public event Action? ExitRequested;
        public event Action? WebDriverRequested;
        public event Action<bool>? OsdToggled;
        public event Action<bool>? StartupToggled;

        public TrayService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;

            _menu = new WF.ContextMenuStrip
            {
                ShowImageMargin = false,
                Font = new Font("Microsoft YaHei UI", 9f)
            };

            var open = new WF.ToolStripMenuItem("打开 Waaagh Hub") { Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold) };
            open.Click += (_, _) => Invoke(OpenRequested);

            var refresh = new WF.ToolStripMenuItem("立即读取电量");
            refresh.Click += (_, _) => Invoke(RefreshRequested);

            _osdItem = new WF.ToolStripMenuItem("低电量 OSD 预警") { CheckOnClick = true, Checked = true };
            _osdItem.Click += (_, _) => Invoke(() => OsdToggled?.Invoke(_osdItem.Checked));

            _startupItem = new WF.ToolStripMenuItem("开机自动启动") { CheckOnClick = true };
            _startupItem.Click += (_, _) => Invoke(() => StartupToggled?.Invoke(_startupItem.Checked));

            var web = new WF.ToolStripMenuItem("打开网页驱动");
            web.Click += (_, _) => Invoke(WebDriverRequested);

            var exit = new WF.ToolStripMenuItem("退出");
            exit.Click += (_, _) => Invoke(ExitRequested);

            _menu.Items.Add(open);
            _menu.Items.Add(refresh);
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add(_osdItem);
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new WF.ToolStripSeparator());
            _menu.Items.Add(web);
            _menu.Items.Add(exit);

            _icon = new WF.NotifyIcon
            {
                ContextMenuStrip = _menu,
                Text = "Waaagh Hub",
                Visible = true,
                Icon = RenderIcon(-1, false)
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == WF.MouseButtons.Left) Invoke(OpenRequested);
            };
            _icon.DoubleClick += (_, _) => Invoke(OpenRequested);
            // 鼠标移到托盘图标上：通知 App 临时提速轮询，保证悬浮看到的是新鲜电量。
            // NotifyIcon 没有 MouseEnter/MouseLeave，MouseMove 在图标区域内持续触发，
            // 由 App 侧用超时窗口判断“离开”。
            _icon.MouseMove += (_, _) => Invoke(Hover);
        }

        public void SyncMenuState(bool osd, bool startup)
        {
            _dispatcher.BeginInvoke(() =>
            {
                _osdItem.Checked = osd;
                _startupItem.Checked = startup;
            });
        }

        public void Update(int level, bool charging, string stateText)
        {
            _dispatcher.BeginInvoke(() =>
            {
                if (level != _lastLevel || charging != _lastCharging)
                {
                    _lastLevel = level;
                    _lastCharging = charging;
                    var newIcon = RenderIcon(level, charging);
                    var old = _icon.Icon;
                    _icon.Icon = newIcon;
                    old?.Dispose();
                }

                string text = level >= 0
                    ? $"Waaagh · {level}% {LevelTier(level)}{(charging ? " 充电中" : "")} · {stateText}"
                    : $"Waaagh · {stateText}";
                if (text.Length > 62) text = text.Substring(0, 62);
                _icon.Text = text;
            });
        }

        private void Invoke(Action? action)
        {
            if (action == null) return;
            _dispatcher.BeginInvoke(action);
        }

        // ---------------- 图标：环形电量表 ----------------

        /// <summary>电量档位配色：≥80 蓝 / 60–79 绿 / 30–59 黄 / &lt;30 红。</summary>
        private static Color LevelColor(int level)
        {
            if (level < 0) return Color.FromArgb(0x8A, 0x90, 0xA0);
            if (level >= 80) return Color.FromArgb(0x3B, 0x9E, 0xFF); // 蓝
            if (level >= 60) return Color.FromArgb(0x3D, 0xDC, 0x97); // 绿
            if (level >= 30) return Color.FromArgb(0xFF, 0xC2, 0x4D); // 黄
            return Color.FromArgb(0xFF, 0x5F, 0x66);                 // 红
        }

        /// <summary>档位名称，用于托盘提示文字。</summary>
        private static string LevelTier(int level)
        {
            if (level < 0) return "未知";
            if (level >= 80) return "充足";
            if (level >= 60) return "良好";
            if (level >= 30) return "偏低";
            return "告急";
        }

        private static Icon RenderIcon(int level, bool charging)
        {
            const int s = 32;
            using var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // 环形轨迹：外径 28，线宽 4，居中于 32×32
                var ring = new RectangleF(4f, 4f, 24f, 24f);
                const float thickness = 4f;

                // 底盘轨道（中性灰，深浅任务栏都能看清）
                using (var track = new Pen(Color.FromArgb(0xA8, 0x9A, 0xA2, 0xB2), thickness)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round
                })
                {
                    g.DrawEllipse(track, ring);
                }

                Color color = LevelColor(level);

                if (level < 0)
                {
                    // 未知 / 离线：轨道中央一个灰点
                    using var dot = new SolidBrush(Color.FromArgb(0x88, 0x8A, 0x90, 0xA0));
                    g.FillEllipse(dot, 13.5f, 13.5f, 5f, 5f);
                }
                else
                {
                    // 0% 也保留一小段红弧，避免看起来像"没画"
                    float sweep = (float)Math.Max(7.0, Math.Min(100, level) * 3.6);

                    // 外发光，让颜色在深色任务栏上更醒目
                    using (var glow = new Pen(Color.FromArgb(0x38, color), thickness + 2.4f)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    })
                    {
                        DrawArc(g, ring, -90f, sweep, glow);
                    }

                    using var pen = new Pen(color, thickness)
                    {
                        StartCap = LineCap.Round,
                        EndCap = LineCap.Round
                    };
                    DrawArc(g, ring, -90f, sweep, pen);
                }

                if (charging)
                {
                    using var bolt = new SolidBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
                    var pts = new[]
                    {
                        new PointF(16.9f, 10.0f), new PointF(12.1f, 16.9f),
                        new PointF(15.3f, 16.9f), new PointF(13.5f, 22.0f),
                        new PointF(19.7f, 15.1f), new PointF(16.2f, 15.1f)
                    };
                    g.FillPolygon(bolt, pts);
                }
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                // 注意：Icon.FromHandle 不接管句柄所有权，且不能 Dispose 它
                // （Dispose 会 DestroyIcon，与外层 DestroyIcon 叠加造成双重释放、堆损坏）
                var tmp = Icon.FromHandle(h);
                return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(h);
            }
        }

        /// <summary>从正上方（-90°）开始顺时针画弧；接近整圈时直接画椭圆。</summary>
        private static void DrawArc(Graphics g, RectangleF bounds, float startAngle, float sweepAngle, Pen pen)
        {
            if (sweepAngle <= 0.2f) return;
            if (sweepAngle >= 359.5f)
            {
                g.DrawEllipse(pen, bounds);
                return;
            }
            using var path = new GraphicsPath();
            path.AddArc(bounds, startAngle, sweepAngle);
            g.DrawPath(pen, path);
        }

        public void Dispose()
        {
            try { _icon.Visible = false; _icon.Dispose(); } catch { }
            try { _menu.Dispose(); } catch { }
        }
    }
}
