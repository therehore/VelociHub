using System;
using System.Windows;
using System.Windows.Media;

namespace VelociHub.UI.Controls
{
    /// <summary>电量环形指示器（纯矢量绘制）。</summary>
    public sealed class BatteryRing : FrameworkElement
    {
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(int), typeof(BatteryRing),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
            nameof(Charging), typeof(bool), typeof(BatteryRing),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RingWidthProperty = DependencyProperty.Register(
            nameof(RingWidth), typeof(double), typeof(BatteryRing),
            new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public int Level
        {
            get => (int)GetValue(LevelProperty);
            set => SetValue(LevelProperty, value);
        }

        public bool Charging
        {
            get => (bool)GetValue(ChargingProperty);
            set => SetValue(ChargingProperty, value);
        }

        public double RingWidth
        {
            get => (double)GetValue(RingWidthProperty);
            set => SetValue(RingWidthProperty, value);
        }

        private Color LevelColor()
        {
            // 与托盘图标保持同一套四档配色
            if (Level < 0) return Color.FromRgb(0x5A, 0x5F, 0x6E);
            if (Level >= 80) return Color.FromRgb(0x3B, 0x9E, 0xFF); // 蓝
            if (Level >= 60) return Color.FromRgb(0x3D, 0xDC, 0x97); // 绿
            if (Level >= 30) return Color.FromRgb(0xFF, 0xC2, 0x4D); // 黄
            return Color.FromRgb(0xFF, 0x5F, 0x66);                 // 红
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 4) return;

            double w = RingWidth;
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            double r = (size - w) / 2 - 2;

            // 浅色主题下白色轨道看不见，换成淡黑轨道
            bool light = App.Settings.LightMode;
            var trackColor = light
                ? Color.FromArgb(0x2E, 0x00, 0x00, 0x00)
                : Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF);
            var trackPen = new Pen(new SolidColorBrush(trackColor), w)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            trackPen.Freeze();
            dc.DrawEllipse(null, trackPen, center, r, r);

            if (Level < 0) return;

            double sweep = Math.Clamp(Level, 0, 100) * 3.6;
            if (sweep <= 0.1) sweep = 0.1;
            if (sweep > 359.4) sweep = 359.4;

            var color = LevelColor();

            // 外发光
            var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B)), w + 5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            glow.Freeze();
            dc.DrawGeometry(null, glow, Arc(center, r, -90, sweep));

            var pen = new Pen(new SolidColorBrush(color), w)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Arc(center, r, -90, sweep));
        }

        private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double a0 = startDeg * Math.PI / 180.0;
                double a1 = (startDeg + sweepDeg) * Math.PI / 180.0;
                var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
                var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            return g;
        }
    }
}
