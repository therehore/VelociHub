using System;
using System.Windows;
using System.Windows.Media;

namespace VelociHub.UI
{
    /// <summary>
    /// 深浅色皮肤：改写 Application 级别的那几个 DynamicResource 画笔，
    /// 同时提供窗口背景渐变、亚克力 tint 与不透明度相关的取色。
    /// </summary>
    internal static class Theme
    {
        private sealed class Palette
        {
            public uint Text, Text2, Text3;
            public uint Card, CardHover, Stroke, Track;
            public uint Divider, Chip, ChipHover, SwitchTrack, ScrollThumb;
            public uint BgTop, BgBottom, Tint, WinBorder;
        }

        private static readonly Palette Dark = new()
        {
            Text = 0xFFFFFFFF,
            Text2 = 0xFFA9AEBF,
            Text3 = 0xFF6E7484,
            Card = 0x12FFFFFF,
            CardHover = 0x1CFFFFFF,
            Stroke = 0x1AFFFFFF,
            Track = 0x22FFFFFF,
            Divider = 0x12FFFFFF,
            Chip = 0x1AFFFFFF,
            ChipHover = 0x22FFFFFF,
            SwitchTrack = 0x26FFFFFF,
            ScrollThumb = 0x33FFFFFF,
            BgTop = 0x101218,
            BgBottom = 0x0C0E14,
            Tint = 0x00201A18,
            WinBorder = 0x1FFFFFFF
        };

        private static readonly Palette Light = new()
        {
            Text = 0xFF15181F,
            Text2 = 0xFF4B5262,
            Text3 = 0xFF7C8598,
            Card = 0x8AFFFFFF,
            CardHover = 0xB0FFFFFF,
            Stroke = 0x1A000000,
            Track = 0x24000000,
            Divider = 0x16000000,
            Chip = 0x3DFFFFFF,
            ChipHover = 0x70FFFFFF,
            SwitchTrack = 0x36000000,
            ScrollThumb = 0x3A000000,
            BgTop = 0xF4F6FB,
            BgBottom = 0xE6E9F1,
            Tint = 0x00F2F4FA,
            WinBorder = 0x26000000
        };

        private static Palette P(bool light) => light ? Light : Dark;

        /// <summary>把调色板写回 Application 资源，所有 DynamicResource 引用自动刷新。</summary>
        public static void Apply(bool light)
        {
            var p = P(light);
            var root = Application.Current?.Resources;
            if (root == null) return;
            // 写到 Theme.xaml 那个合并字典里：键本来就存在，属于「修改」而非「新增」，
            // DynamicResource 的失效通知更可靠（顶层新增键在部分 WPF 版本不一定触发刷新）。
            var res = root.MergedDictionaries.Count > 0 ? root.MergedDictionaries[0] : root;
            Set(res, "TextBrush", p.Text);
            Set(res, "Text2Brush", p.Text2);
            Set(res, "Text3Brush", p.Text3);
            Set(res, "CardBrush", p.Card);
            Set(res, "CardHoverBrush", p.CardHover);
            Set(res, "StrokeBrush", p.Stroke);
            Set(res, "TrackBrush", p.Track);
            Set(res, "DividerBrush", p.Divider);
            Set(res, "ChipBrush", p.Chip);
            Set(res, "ChipHoverBrush", p.ChipHover);
            Set(res, "SwitchTrackBrush", p.SwitchTrack);
            Set(res, "ScrollThumbBrush", p.ScrollThumb);
        }

        private static void Set(ResourceDictionary res, string key, uint argb) =>
            res[key] = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

        /// <summary>窗口外框描边笔（与卡片描边区分，略亮一点）。</summary>
        public static Brush WindowBorder(bool light) => Solid(light ? Light.WinBorder : Dark.WinBorder);

        /// <summary>主窗口背景渐变，alpha 由「背景不透明度」滑块决定。</summary>
        public static Brush WindowBackground(double opacity, bool light)
        {
            var p = P(light);
            byte a = (byte)Math.Clamp(Math.Round(opacity * 255), 0, 255);
            var top = Color.FromArgb(a, (byte)(p.BgTop >> 16), (byte)(p.BgTop >> 8), (byte)p.BgTop);
            var bot = Color.FromArgb(a, (byte)(p.BgBottom >> 16), (byte)(p.BgBottom >> 8), (byte)p.BgBottom);
            return new LinearGradientBrush(top, bot, new Point(0, 0), new Point(0.6, 1));
        }

        public static uint Tint(bool light) => P(light).Tint;

        /// <summary>亚克力 tint 的 alpha 跟随不透明度（保持模糊，但不至于一直很黑）。</summary>
        public static byte AcrylicAlpha(double opacity)
        {
            double a = 0.06 + 0.55 * Math.Clamp(opacity, 0.0, 1.0);
            return (byte)Math.Clamp(Math.Round(a * 255), 20, 200);
        }

        public static Color LevelColor(int level, bool light)
        {
            if (level < 0) return light ? Color.FromArgb(0xFF, 0x9A, 0xA0, 0xAE) : Color.FromArgb(0xFF, 0x8A, 0x90, 0xA0);
            if (level >= 80) return Color.FromArgb(0xFF, 0x3B, 0x9E, 0xFF);
            if (level >= 60) return Color.FromArgb(0xFF, 0x2F, 0xC9, 0x86);
            if (level >= 30) return Color.FromArgb(0xFF, 0xE8, 0xAE, 0x37);
            return Color.FromArgb(0xFF, 0xFF, 0x5F, 0x66);
        }

        private static Brush Solid(uint argb) =>
            new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }
}
