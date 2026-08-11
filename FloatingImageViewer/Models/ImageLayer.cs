using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatingImageViewer.Services;

namespace FloatingImageViewer.Models;

/// <summary>
/// 图片图层：一张图片 + 独立的缩放/平移状态与显示元素。
/// 每个图层有自己的 Canvas（承载 Scale/Pan 变换）与 Image，可独立移动、缩放、显隐；
/// GIF 图层额外持有独立的 <see cref="GifAnimation"/> 播放器（互不干扰，默认播放）。
/// </summary>
public sealed class ImageLayer
{
    public ImageLayer(string path, BitmapSource source)
    {
        Path = path;
        Source = source;
        PixelWidth = source.PixelWidth;
        PixelHeight = source.PixelHeight;
        ElementWidth = Math.Max(1, source.PixelWidth);
        ElementHeight = Math.Max(1, source.PixelHeight);
        Element = new Image
        {
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = ElementWidth,
            Height = ElementHeight,
            Source = source,
        };
        Canvas = new Canvas { RenderTransform = new TransformGroup { Children = { Scale, Pan } } };
        Canvas.Children.Add(Element);
    }

    /// <summary>图片路径。</summary>
    public string Path { get; }

    /// <summary>解码后的显示位图（已 DPI 归一化/降采样）。</summary>
    public BitmapSource Source { get; }

    /// <summary>GIF 图层的独立播放器（静态图为 null）。</summary>
    public GifAnimation? Gif { get; set; }

    public int PixelWidth { get; }

    public int PixelHeight { get; }

    public double ElementWidth { get; }

    public double ElementHeight { get; }

    /// <summary>当前缩放倍率：显示尺寸 = 像素尺寸 × <see cref="ZoomScale"/>。</summary>
    public double ZoomScale { get; set; } = 1.0;

    /// <summary>用户平移量（叠加在居中偏移之上）。</summary>
    public Point UserPan { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>图层自身透明度（0–100%，作用于整个图层 Canvas，不影响其他图层）。</summary>
    public double OpacityPercent { get; set; } = 100;

    /// <summary>图层自身的缩放变换（作用于 <see cref="Canvas"/>）。</summary>
    public ScaleTransform Scale { get; } = new();

    /// <summary>图层自身的平移变换（作用于 <see cref="Canvas"/>）。</summary>
    public TranslateTransform Pan { get; } = new();

    /// <summary>承载该图层的 Canvas（含 Scale/Pan 变换）。</summary>
    public Canvas Canvas { get; }

    /// <summary>显示图片的元素。</summary>
    public Image Element { get; }

    /// <summary>更新 Canvas 的变换：缩放 + 居中偏移 + 用户平移。</summary>
    public void ApplyTransform(double windowWidth, double windowHeight)
    {
        var displayWidth = Math.Max(PixelWidth * ZoomScale, 1);
        var displayHeight = Math.Max(PixelHeight * ZoomScale, 1);
        var contentScale = displayWidth / ElementWidth;
        Scale.ScaleX = contentScale;
        Scale.ScaleY = contentScale;
        // 居中项 = (窗口 - 显示尺寸)/2：图片比窗口小时居中，比窗口大时向两侧溢出。
        Pan.X = (windowWidth - displayWidth) / 2.0 + UserPan.X;
        Pan.Y = (windowHeight - displayHeight) / 2.0 + UserPan.Y;
    }
}
