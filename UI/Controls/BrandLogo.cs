using System;
using System.Windows;
using System.Windows.Media;
using Mx300Hub.Services;

namespace Mx300Hub.UI.Controls
{
    /// <summary>
    /// 软件图标。默认绘制内置 M 标并整体翻转 180°（视觉上即 W）；
    /// 用户在设置里指定了图片时改为等比居中绘制该图片，自动响应图标变更。
    /// </summary>
    public sealed class BrandLogo : FrameworkElement
    {
        /// <summary>M 标：两条立柱 + 中间 V，24×24 视口内留 3 单位边距。翻转 180° 后成为 W。</summary>
        private static readonly Geometry Mark = Geometry.Parse(
            "M3 20.5 L3 3.5 L7.4 3.5 L12 12.1 L16.6 3.5 L21 3.5 L21 20.5 L16.9 20.5 " +
            "L16.9 10.4 L12.9 18.1 L11.1 18.1 L7.1 10.4 L7.1 20.5 Z");

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
            dc.PushTransform(new RotateTransform(180, 12, 12));   // M 倒过来就是 W
            dc.DrawGeometry(brush, null, Mark);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
    }
}
