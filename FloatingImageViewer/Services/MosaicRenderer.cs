using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatingImageViewer.Services;

/// <summary>
/// 框选区域的马赛克效果（纯 CPU 像素处理，透明窗口软件渲染下同样有效）。
/// 对底图指定矩形区域处理并返回区域位图，由调用方叠加显示。
/// </summary>
public static class MosaicRenderer
{
    public enum Style
    {
        Mosaic,
        Blur,
        Smudge,
        Solid,
    }

    /// <summary>
    /// 对底图指定区域应用效果，返回处理后的区域位图（96 DPI，Bgra32）。
    /// <paramref name="param"/> 按样式解释：马赛克=块大小 px、高斯模糊=模糊半径 px、涂抹=偏移范围 px、纯色=忽略。
    /// </summary>
    public static BitmapSource Apply(
        BitmapSource baseImage,
        Int32Rect region,
        Style style,
        double param,
        Color color)
    {
        int x = Math.Max(0, region.X);
        int y = Math.Max(0, region.Y);
        int right = Math.Min(baseImage.PixelWidth, region.X + region.Width);
        int bottom = Math.Min(baseImage.PixelHeight, region.Y + region.Height);
        int width = Math.Max(1, right - x);
        int height = Math.Max(1, bottom - y);

        var pixels = new byte[width * height * 4];
        baseImage.CopyPixels(new Int32Rect(x, y, width, height), pixels, width * 4, 0);

        switch (style)
        {
            case Style.Mosaic:
                ApplyMosaic(pixels, width, height, Math.Max(1, (int)param));
                break;
            case Style.Blur:
                ApplyBoxBlur(pixels, width, height, Math.Max(1, (int)param));
                break;
            case Style.Smudge:
                ApplySmudge(pixels, width, height, Math.Max(1, (int)param));
                break;
            case Style.Solid:
                FillSolid(pixels, color);
                break;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>马赛克：按块大小取块内平均色填充。</summary>
    private static void ApplyMosaic(byte[] pixels, int width, int height, int block)
    {
        for (int by = 0; by < height; by += block)
        {
            for (int bx = 0; bx < width; bx += block)
            {
                int endX = Math.Min(width, bx + block);
                int endY = Math.Min(height, by + block);
                long r = 0;
                long g = 0;
                long b = 0;
                long a = 0;
                int count = 0;
                for (int y = by; y < endY; y++)
                {
                    for (int x = bx; x < endX; x++)
                    {
                        int i = (y * width + x) * 4;
                        r += pixels[i];
                        g += pixels[i + 1];
                        b += pixels[i + 2];
                        a += pixels[i + 3];
                        count++;
                    }
                }

                byte ar = (byte)(r / count);
                byte ag = (byte)(g / count);
                byte ab = (byte)(b / count);
                byte aa = (byte)(a / count);
                for (int y = by; y < endY; y++)
                {
                    for (int x = bx; x < endX; x++)
                    {
                        int i = (y * width + x) * 4;
                        pixels[i] = ar;
                        pixels[i + 1] = ag;
                        pixels[i + 2] = ab;
                        pixels[i + 3] = aa;
                    }
                }
            }
        }
    }

    /// <summary>高斯模糊近似：三次盒式模糊（box blur）叠加。</summary>
    private static void ApplyBoxBlur(byte[] pixels, int width, int height, int radius)
    {
        var temp = new byte[pixels.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BlurPass(pixels, temp, width, height, radius, horizontal: true);
            BlurPass(temp, pixels, width, height, radius, horizontal: false);
        }
    }

    private static void BlurPass(byte[] src, byte[] dst, int width, int height, int radius, bool horizontal)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int start = horizontal ? Math.Max(0, x - radius) : Math.Max(0, y - radius);
                int end = horizontal ? Math.Min(width - 1, x + radius) : Math.Min(height - 1, y + radius);
                long r = 0;
                long g = 0;
                long b = 0;
                long a = 0;
                int count = 0;
                for (int k = start; k <= end; k++)
                {
                    int i = (horizontal ? y * width + k : k * width + x) * 4;
                    r += src[i];
                    g += src[i + 1];
                    b += src[i + 2];
                    a += src[i + 3];
                    count++;
                }

                int o = (y * width + x) * 4;
                dst[o] = (byte)(r / count);
                dst[o + 1] = (byte)(g / count);
                dst[o + 2] = (byte)(b / count);
                dst[o + 3] = (byte)(a / count);
            }
        }
    }

    /// <summary>涂抹：每个像素取随机偏移范围内的源像素（破坏细节的噪点涂抹）。</summary>
    private static void ApplySmudge(byte[] pixels, int width, int height, int spread)
    {
        var src = (byte[])pixels.Clone();
        var rng = new Random(12345);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Clamp(x + rng.Next(-spread, spread + 1), 0, width - 1);
                int sy = Math.Clamp(y + rng.Next(-spread, spread + 1), 0, height - 1);
                int si = (sy * width + sx) * 4;
                int di = (y * width + x) * 4;
                pixels[di] = src[si];
                pixels[di + 1] = src[si + 1];
                pixels[di + 2] = src[si + 2];
                pixels[di + 3] = src[si + 3];
            }
        }
    }

    /// <summary>纯色：整块区域填充指定颜色。</summary>
    private static void FillSolid(byte[] pixels, Color color)
    {
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }
    }
}
