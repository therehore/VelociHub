using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mx300Hub.Services
{
    /// <summary>
    /// 品牌标：默认使用内置 M 标；用户可指定任意本地图片替换。
    /// 图片统一缩放（≤256px）后转存为 PNG，既保证解码稳定，也不会因为选了 4K 图而吃内存。
    /// </summary>
    public static class BrandIcon
    {
        /// <summary>自定义图标变更（载入 / 更换 / 恢复默认）后触发，供界面重绘。</summary>
        public static event Action? Changed;

        private const int MaxSize = 256;
        private const long MaxFileBytes = 8 * 1024 * 1024;

        private static ImageSource? _custom;

        /// <summary>当前自定义图标；为 null 时使用内置 M 标。</summary>
        public static ImageSource? Custom => _custom;
        public static bool HasCustom => _custom != null;

        /// <summary>启动时从磁盘载入，损坏文件直接忽略并回退到 M 标。</summary>
        public static void Load()
        {
            _custom = null;
            try
            {
                string path = AppPaths.BrandImage;
                if (!File.Exists(path)) return;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                _custom = bmp;
            }
            catch { _custom = null; }
        }

        /// <summary>把用户选的图片转成标准 PNG 存下来。返回 false 表示格式不支持或文件异常。</summary>
        public static bool SetFrom(string sourcePath)
        {
            try
            {
                var info = new FileInfo(sourcePath);
                if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) return false;

                var frame = Decode(sourcePath);
                if (frame == null) return false;

                Save(frame);
                Load();
                Changed?.Invoke();
                App.Trace("软件图标已更新: " + sourcePath);
                return true;
            }
            catch (Exception ex)
            {
                App.Trace("软件图标载入失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>恢复内置 M 标。</summary>
        public static void Reset()
        {
            try { File.Delete(AppPaths.BrandImage); } catch { }
            _custom = null;
            Changed?.Invoke();
        }

        private static BitmapSource? Decode(string path)
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
        }

        private static void Save(BitmapSource src)
        {
            AppPaths.Ensure();

            int w = src.PixelWidth, h = src.PixelHeight;
            if (w <= 0 || h <= 0) throw new InvalidOperationException("图像尺寸无效");

            BitmapSource output = src;
            int longest = Math.Max(w, h);
            if (longest > MaxSize)
            {
                double k = MaxSize / (double)longest;
                var scale = new ScaleTransform(k, k);
                scale.Freeze();
                var resized = new TransformedBitmap(src, scale);
                resized.Freeze();
                output = resized;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using var fs = File.Create(AppPaths.BrandImage);
            encoder.Save(fs);
        }
    }
}
