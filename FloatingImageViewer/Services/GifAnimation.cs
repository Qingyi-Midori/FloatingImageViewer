using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FloatingImageViewer.Services;

/// <summary>
/// GIF 逐帧播放器：解码后用 DispatcherTimer 按帧延迟切换 Image.Source，
/// 加载时完成帧合成（处理 disposal 2/3），暂停时精确停留在当前帧。
/// </summary>
public sealed class GifAnimation
{
    private Image _image;
    private readonly DispatcherTimer _timer;
    private IReadOnlyList<BitmapSource>? _frames;
    private IReadOnlyList<int>? _delays;
    private int _index;
    private bool _isAnimated;
    private bool _isPaused;

    public GifAnimation(Image image)
    {
        _image = image;
        _timer = new DispatcherTimer();
        _timer.Tick += Timer_Tick;
    }

    /// <summary>切换动画输出目标（多图层模式下活动图层切换时使用）。</summary>
    public void SetTarget(Image image) => _image = image;

    public bool IsAnimatedGif => _isAnimated;

    public bool IsPaused => _isPaused;

    /// <summary>
    /// 解码 GIF 并返回第一帧（不设置 Image.Source、不启动动画），
    /// 调用方在真正显示后调用 <see cref="Start"/>。
    /// </summary>
    public BitmapSource? Load(string path)
    {
        Stop();
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            if (decoder.Frames.Count == 1)
            {
                return decoder.Frames[0];
            }

            var (frames, delays) = CompositeFrames(decoder);
            _frames = frames;
            _delays = delays;
            _index = 0;
            _isAnimated = true;
            _isPaused = false;
            return frames[0];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>启动逐帧播放（仅当当前确实是动画 GIF 且未暂停时生效）。</summary>
    public void Start()
    {
        if (!_isAnimated || _isPaused || _frames is null || _frames.Count == 0)
        {
            return;
        }

        _index = Math.Clamp(_index, 0, _frames.Count - 1);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _delays![_index]));
        _timer.Start();
    }

    public void Pause()
    {
        if (_isAnimated && !_isPaused)
        {
            _timer.Stop();
            _isPaused = true;
        }
    }

    public void Resume()
    {
        if (_isAnimated && _isPaused)
        {
            _isPaused = false;
            Start();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _frames = null;
        _delays = null;
        _index = 0;
        _isAnimated = false;
        _isPaused = false;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (_frames is null || _frames.Count == 0)
        {
            return;
        }

        _index = (_index + 1) % _frames.Count;
        _image.Source = _frames[_index];
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _delays![_index]));
    }

    private static (List<BitmapSource> Frames, List<int> Delays) CompositeFrames(BitmapDecoder decoder)
    {
        var delays = new List<int>(decoder.Frames.Count);
        var disposals = new List<int>(decoder.Frames.Count);
        var offsets = new List<(int X, int Y)>(decoder.Frames.Count);

        int canvasWidth = 0;
        int canvasHeight = 0;
        foreach (var frame in decoder.Frames)
        {
            var offset = ReadOffset(frame);
            offsets.Add(offset);
            canvasWidth = Math.Max(canvasWidth, offset.X + frame.PixelWidth);
            canvasHeight = Math.Max(canvasHeight, offset.Y + frame.PixelHeight);
            delays.Add(ReadFrameDelay(frame));
            disposals.Add(ReadDisposal(frame));
        }

        var canvas = new byte[canvasWidth * canvasHeight * 4];
        byte[]? saved = null;
        var frames = new List<BitmapSource>(decoder.Frames.Count);

        for (int i = 0; i < decoder.Frames.Count; i++)
        {
            if (saved is not null)
            {
                canvas = saved;
                saved = null;
            }
            else if (i > 0 && disposals[i - 1] == 2)
            {
                Array.Clear(canvas, 0, canvas.Length);
            }

            if (disposals[i] == 3)
            {
                saved = (byte[])canvas.Clone();
            }

            DrawFrame(decoder.Frames[i], canvas, canvasWidth, canvasHeight, offsets[i]);

            var bitmap = BitmapSource.Create(
                canvasWidth,
                canvasHeight,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                canvas,
                canvasWidth * 4);
            bitmap.Freeze();
            frames.Add(bitmap);
        }

        return (frames, delays);
    }

    private static void DrawFrame(
        BitmapFrame frame,
        byte[] canvas,
        int canvasWidth,
        int canvasHeight,
        (int X, int Y) offset)
    {
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        for (int y = 0; y < height; y++)
        {
            int cy = offset.Y + y;
            if (cy < 0 || cy >= canvasHeight)
            {
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                int cx = offset.X + x;
                if (cx < 0 || cx >= canvasWidth)
                {
                    continue;
                }

                int si = (y * width + x) * 4;
                int di = (cy * canvasWidth + cx) * 4;
                byte sa = pixels[si + 3];
                if (sa == 0)
                {
                    continue;
                }

                if (sa == 255)
                {
                    canvas[di] = pixels[si];
                    canvas[di + 1] = pixels[si + 1];
                    canvas[di + 2] = pixels[si + 2];
                    canvas[di + 3] = 255;
                }
                else
                {
                    int inverse = 255 - sa;
                    canvas[di] = (byte)((pixels[si] * sa + canvas[di] * inverse + 127) / 255);
                    canvas[di + 1] = (byte)((pixels[si + 1] * sa + canvas[di + 1] * inverse + 127) / 255);
                    canvas[di + 2] = (byte)((pixels[si + 2] * sa + canvas[di + 2] * inverse + 127) / 255);
                    canvas[di + 3] = (byte)(sa + (canvas[di + 3] * inverse + 127) / 255);
                }
            }
        }
    }

    private static (int X, int Y) ReadOffset(BitmapFrame frame)
    {
        int x = 0;
        int y = 0;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
            {
                if (metadata.ContainsQuery("/imgdesc/Left"))
                {
                    x = Convert.ToInt32(metadata.GetQuery("/imgdesc/Left"));
                }

                if (metadata.ContainsQuery("/imgdesc/Top"))
                {
                    y = Convert.ToInt32(metadata.GetQuery("/imgdesc/Top"));
                }
            }
        }
        catch
        {
        }

        return (x, y);
    }

    private static int ReadFrameDelay(BitmapFrame frame)
    {
        int delayMs = 100;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
            {
                var value = metadata.GetQuery("/grctlext/Delay");
                double centiseconds = value switch
                {
                    ushort u => u,
                    int i => i,
                    double d => d,
                    _ => 10,
                };
                delayMs = (int)Math.Round(centiseconds * 10);
            }
        }
        catch
        {
        }

        return Math.Clamp(delayMs, 20, 10_000);
    }

    private static int ReadDisposal(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Disposal"))
            {
                var value = metadata.GetQuery("/grctlext/Disposal");
                return value switch
                {
                    byte b => b,
                    ushort u => u,
                    int i => i,
                    _ => 1,
                };
            }
        }
        catch
        {
        }

        return 1;
    }
}
