using System;
using System.Windows;
using System.Windows.Media;
using VelociHub.Services;

namespace VelociHub.UI.Controls
{
    /// <summary>
    /// 软件图标。默认绘制内置 V 标；用户在设置里指定了图片时改为等比居中绘制该图片，自动响应图标变更。
    /// </summary>
    public sealed class BrandLogo : FrameworkElement
    {
        /// <summary>V 标：左右两腿对称，顶部平口、底部收窄，24×24 视口内留 3 单位边距。</summary>
        private static readonly Geometry Mark = Geometry.Parse(
            "M3 4 L8.4 4 L12 15 L15.6 4 L21 4 L14.6 20.8 L9.4 20.8 Z");

        private static readonly Color DarkGreen = Color.FromRgb(0x44, 0xD6, 0x2C);
        private static readonly Color LightGreen = Color.FromRgb(0x18, 0x9E, 0x1F);

        private bool _hooked;

        public BrandLogo()
        {
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            Loaded += (_, _) => Hook(true);
            Unloaded += (_, _) => Hook(false);
        }

        /// <summary>主题切换后调用，让 M 标取到对应底色下的绿色。</summary>
        public void Refresh() => InvalidateVisual();

        private void Hook(bool on)
        {
            if (on == _hooked) return;
            if (on) BrandIcon.Changed += OnIconChanged;
            else BrandIcon.Changed -= OnIconChanged;
            _hooked = on;
        }

        private void OnIconChanged() => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            var custom = BrandIcon.Custom;
            if (custom != null)
            {
                double cw = custom.Width, ch = custom.Height;
                if (cw <= 0 || ch <= 0) return;
                double k = Math.Min(w / cw, h / ch);
                double dw = cw * k, dh = ch * k;
                dc.DrawImage(custom, new Rect((w - dw) / 2, (h - dh) / 2, dw, dh));
                return;
            }

            double s = Math.Min(w, h) / 24.0;
            bool light = App.Settings.LightMode;
            var brush = new SolidColorBrush(light ? LightGreen : DarkGreen);
            brush.Freeze();

            dc.PushTransform(new TranslateTransform((w - 24 * s) / 2, (h - 24 * s) / 2));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(brush, null, Mark);
            dc.Pop();
            dc.Pop();
        }
    }
}
