using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingImageViewer.Models;
using FloatingImageViewer.Services;
using FloatingImageViewer.Views;
using Microsoft.Win32;

namespace FloatingImageViewer;

public partial class MainWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 20.0;
    private const double MaxElementSize = 16000;
    private const double ZoomStep = 1.10;
    private const long WheelZoomThrottleMs = 50;
    /// <summary>自适应缩放步进的倍率上限：小图放大 / 大图缩小时每格最多 ×1.10×1.4 ≈ ×1.54。</summary>
    private const double MaxAdaptiveBoost = 1.4;
    /// <summary>棋盘格背景纹样在屏幕上的恒定尺寸（px），不随图片缩放。</summary>
    private const double CheckerCellSize = 16;

    private readonly ViewerSettings _settings;
    private readonly DispatcherTimer _slideTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _qualityTimer;
    private readonly TranslateTransform _slideTransform = new();
    private readonly List<ImageLayer> _layers = new();
    private ImageLayer? _activeLayer;

    private bool _isPanning;
    private Point _lastPanPoint;
    private bool _middleDown;
    private bool _middleDragged;
    private Point _middleDownPoint;
    private bool _isDraggingImage;
    private Point _dragStartPoint;
    private Point _dragStartPan;

    private List<string>? _slideFiles;
    private int _slideIndex;
    private bool _slideshowActive;
    private bool _slideshowPlaying;
    private bool _transitionBusy;
    private long _lastWheelZoomTick;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    // 框选马赛克（编辑模式）
    private bool _mosaicActive;
    private bool _mosaicSelecting;
    private Point _mosaicStart;
    private Rect _mosaicRect;
    private MosaicRenderer.Style _mosaicStyle = MosaicRenderer.Style.Mosaic;
    private double _mosaicBlockPx = 16;
    private double _mosaicBlurPx = 8;
    private double _mosaicSmudgePx = 8;
    private Color _mosaicColor = Colors.Black;
    private BitmapSource? _mosaicBase;
    private ImageLayer? _mosaicBaseLayer;
    private Image? _mosaicPreview;
    private Border? _mosaicBox;
    private readonly List<Image> _mosaicEffects = new();

    public bool IsImageLoaded { get; }

    public MainWindow(string imagePath)
    {
        InitializeComponent();

        _settings = SettingsService.Load();
        ApplyPersistedState();
        ImageCache.Configure(_settings.CacheStrategy, _settings.CacheLimit);

        // 幻灯片划入动画作用于整个图层宿主；各图层的缩放/平移在自己的 Canvas 变换上
        //（图层元素保持原始尺寸，避免元素大于窗口时被先裁剪再缩放）。
        SlideLayer.RenderTransform = new TransformGroup { Children = { _slideTransform } };

        _slideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_settings.SlideshowIntervalSeconds) };
        _slideTimer.Tick += SlideshowTimer_Tick;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };
        // 交互（拖拽/缩放）期间用低质量快速采样保证跟手，停止滚动 300ms 后恢复采样设置。
        _qualityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _qualityTimer.Tick += (_, _) =>
        {
            _qualityTimer.Stop();
            ApplyAntiAliasing();
        };
        BuildContextMenu();
        IsImageLoaded = LoadImage(imagePath);
        if (IsImageLoaded)
        {
            ApplySavedImagePosition();
        }
    }

    #region 启动 / 持久化

    private void ApplyPersistedState()
    {
        Topmost = _settings.Topmost;
        Opacity = _settings.OpacityPercent / 100.0;
        // 窗口始终为工作区全屏，缩放只作用于图片本身。
        var workArea = ScreenService.GetWorkArea(this);
        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;

        ApplyBackdrop();
    }

    /// <summary>按保存的图片屏幕位置恢复（若有效），否则居中。</summary>
    private void ApplySavedImagePosition()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        var workArea = ScreenService.GetWorkArea(this);
        if (!double.IsNaN(_settings.ImageLeft) && !double.IsNaN(_settings.ImageTop))
        {
            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);
            if (new Rect(_settings.ImageLeft, _settings.ImageTop, 1, 1).IntersectsWith(virtualScreen))
            {
                var displayWidth = Math.Max(layer.PixelWidth * layer.ZoomScale, 1);
                var displayHeight = Math.Max(layer.PixelHeight * layer.ZoomScale, 1);
                var centeringX = (Width - displayWidth) / 2.0;
                var centeringY = (Height - displayHeight) / 2.0;
                // pan = 居中偏移 + userPan；恢复时减去居中偏移，图片才能落在保存的屏幕位置。
                layer.UserPan = new Point(
                    _settings.ImageLeft - workArea.Left - centeringX,
                    _settings.ImageTop - workArea.Top - centeringY);
                UpdateTransform();
                return;
            }
        }

        layer.UserPan = new Point(0, 0);
        UpdateTransform();
    }

    private void ApplyBackdrop()
    {
        Backdrop.Background = _settings.BackgroundMode switch
        {
            "Black" => Brushes.Black,
            "White" => Brushes.White,
            "Checkerboard" => CreateCheckerBrush(),
            _ => Brushes.Transparent,
        };
    }

    private static Brush CreateCheckerBrush()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            Brushes.White,
            null,
            new RectangleGeometry(new Rect(0, 0, CheckerCellSize, CheckerCellSize))));

        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, CheckerCellSize / 2.0, CheckerCellSize / 2.0)));
        squares.Children.Add(new RectangleGeometry(new Rect(CheckerCellSize / 2.0, CheckerCellSize / 2.0, CheckerCellSize / 2.0, CheckerCellSize / 2.0)));
        drawing.Children.Add(new GeometryDrawing(Brushes.LightGray, null, squares));

        return new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, CheckerCellSize, CheckerCellSize),
            ViewportUnits = BrushMappingMode.Absolute,
        };
    }

    private void SaveSettings()
    {
        // 记录活动图层左上角在屏幕上的位置（全屏窗口下用于恢复）。
        if (_activeLayer is { } layer)
        {
            _settings.ImageLeft = Left + layer.Pan.X;
            _settings.ImageTop = Top + layer.Pan.Y;
        }

        SettingsService.Save(_settings);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    #endregion

    #region 图片加载 / 缩放 / 平移

    private bool LoadImage(string path, bool reportErrors = true)
    {
        try
        {
            if (_activeLayer is null)
            {
                AddLayer(path);
            }
            else
            {
                ReplaceActiveLayer(path);
            }

            ApplyZoomMode();
            return true;
        }
        catch (Exception ex)
        {
            if (reportErrors)
            {
                MessageBox.Show(
                    $"无法加载图片：\n{path}\n\n{ex.Message}",
                    "加载失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }
    }

    /// <summary>新建图层并设为活动图层。</summary>
    private ImageLayer AddLayer(string path)
    {
        var layer = CreateLayer(path);
        _layers.Add(layer);
        LayerHost.Children.Add(layer.Canvas);
        SetActiveLayer(layer);
        UpdateTransform();
        return layer;
    }

    /// <summary>直接以给定位图创建图层（马赛克底图等内存位图用，不经过文件解码）。</summary>
    private ImageLayer AddLayer(string name, BitmapSource source)
    {
        var layer = new ImageLayer(name, source);
        _layers.Add(layer);
        LayerHost.Children.Add(layer.Canvas);
        SetActiveLayer(layer);
        UpdateTransform();
        return layer;
    }

    /// <summary>替换活动图层的图片（保持其在图层栈中的位置与显隐状态）。</summary>
    private void ReplaceActiveLayer(string path)
    {
        var old = _activeLayer;
        if (old is null)
        {
            AddLayer(path);
            return;
        }

        int index = _layers.IndexOf(old);
        var layer = CreateLayer(path);
        layer.Visible = old.Visible;
        _layers.Remove(old);
        LayerHost.Children.Remove(old.Canvas);
        _layers.Insert(index, layer);
        LayerHost.Children.Insert(index, layer.Canvas);
        SetActiveLayer(layer);
        UpdateTransform();
    }

    /// <summary>
    /// 解码并创建图层：GIF 图层带独立播放器（每个 GIF 各自播放、互不干扰，默认播放），
    /// 静态图走解码缓存（命中时直接复用显示位图）。
    /// </summary>
    private ImageLayer CreateLayer(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            var gif = new GifAnimation(new Image());
            var source = gif.Load(path)
                ?? throw new InvalidOperationException("GIF 解码失败。");
            var layer = new ImageLayer(path, source);
            gif.SetTarget(layer.Element);
            layer.Gif = gif;
            gif.Start();
            return layer;
        }

        return new ImageLayer(path, LoadStaticImage(path));
    }

    /// <summary>静态图片解码（缓存命中直接复用显示位图）。</summary>
    private BitmapSource LoadStaticImage(string path)
    {
        var cacheKey = BuildCacheKey(path);
        var cached = ImageCache.Get(cacheKey);
        if (cached is ImageCache.CacheItem item)
        {
            return item.Bitmap;
        }

        BitmapSource source = LoadStaticFrame(path);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        var downsample = ComputeDownsample(width, height);
        if (downsample < 1.0)
        {
            source = new TransformedBitmap(source, new ScaleTransform(downsample, downsample));
        }

        source = NormalizeDpi(source);
        source.Freeze();
        ImageCache.Add(cacheKey, source, width, height);
        return source;
    }

    /// <summary>切换活动图层（各 GIF 播放器独立运行，无需重绑）。</summary>
    private void SetActiveLayer(ImageLayer layer)
    {
        if (_activeLayer == layer)
        {
            return;
        }

        _activeLayer = layer;
        ApplyAntiAliasing();
        UpdateTransform();
    }

    private static string BuildCacheKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    /// <summary>超大图显示降采样倍率：最长边超过 <see cref="MaxElementSize"/> 时按比例缩小。</summary>
    private static double ComputeDownsample(int width, int height)
    {
        var maxDimension = Math.Max(width, height);
        return maxDimension <= 0 ? 1.0 : Math.Min(1.0, MaxElementSize / maxDimension);
    }

    /// <summary>
    /// 把位图统一为 96 DPI。WPF 的 Image 会按图片 DPI 元数据缩放绘制区域：
    /// 72 DPI 图片只画到元素 75% 大小（留边），300 DPI 图片会画到元素 3 倍大（被裁切）。
    /// 统一为 96 DPI 后 Stretch=Fill 才能严格铺满元素。
    /// 采用零拷贝包装（只覆盖 DPI 元数据、共享像素数据），避免全像素拷贝的内存峰值。
    /// </summary>
    private static BitmapSource NormalizeDpi(BitmapSource source)
    {
        if (Math.Abs(source.DpiX - 96.0) < 0.01 && Math.Abs(source.DpiY - 96.0) < 0.01)
        {
            return source;
        }

        return new DpiNormalizedBitmap(source);
    }

    /// <summary>
    /// 零拷贝 DPI 归一化包装：只把 DpiX/DpiY 覆盖为 96，像素数据与原始位图共享。
    /// 原实现 FormatConvertedBitmap + CopyPixels + BitmapSource.Create 会为一张大图
    /// 产生 3 份全像素临时拷贝（如 8000×6000 图额外占用数百 MB）。
    /// </summary>
    private sealed class DpiNormalizedBitmap : BitmapSource
    {
        private readonly BitmapSource _source;

        public DpiNormalizedBitmap(BitmapSource source) => _source = source;

        public override double DpiX => 96;

        public override double DpiY => 96;

        public override int PixelWidth => _source.PixelWidth;

        public override int PixelHeight => _source.PixelHeight;

        public override PixelFormat Format => _source.Format;

        public override BitmapPalette? Palette => _source.Palette;

        public override void CopyPixels(Array pixels, int stride, int offset)
            => _source.CopyPixels(pixels, stride, offset);

        public override void CopyPixels(Int32Rect sourceRect, Array pixels, int stride, int offset)
            => _source.CopyPixels(sourceRect, pixels, stride, offset);

        public override void CopyPixels(Int32Rect sourceRect, IntPtr buffer, int bufferSize, int stride)
            => _source.CopyPixels(sourceRect, buffer, bufferSize, stride);

        protected override Freezable CreateInstanceCore() => new DpiNormalizedBitmap(_source);
    }

    private static BitmapFrame LoadStaticFrame(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException("文件中没有可用的图像帧。");
        }

        return decoder.Frames[0];
    }

    private void ApplyZoomMode()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        // 窗口从启动起即保持工作区全屏（全程不缩放窗口），缩放/平移只作用于图片（图层变换），
        // 图片在窗口内居中显示，超大图通过平移查看。
        layer.Element.HorizontalAlignment = HorizontalAlignment.Left;
        layer.Element.VerticalAlignment = VerticalAlignment.Top;
        layer.Element.Stretch = Stretch.None;
        ApplyAntiAliasing();

        layer.ZoomScale = _settings.ZoomMode == "Fit" ? ComputeFitScale() : 1.0;
        layer.UserPan = new Point(0, 0);
        UpdateTransform();
    }

    /// <summary>
    /// 渲染启发式：小图（最长边 &lt; 256）用邻近插值保持像素锐利，大图用默认（线性）渲染。
    /// </summary>
    private void ApplyScalingHeuristic()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        var maxDimension = Math.Max(layer.PixelWidth, layer.PixelHeight);
        RenderOptions.SetBitmapScalingMode(
            layer.Element,
            maxDimension < 256 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Unspecified);
    }

    /// <summary>
    /// 应用抗锯齿模式：WPF 渲染管线无 MSAA/TXAA 硬件支持，模式映射为位图缩放采样质量
    /// （SSAA=高质量、MSAA=线性、TXAA=默认），视觉效果有限，参数设置完整保留。
    /// </summary>
    private void ApplyAntiAliasing()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        switch (_settings.AntiAliasing)
        {
            case "SSAA":
                RenderOptions.SetBitmapScalingMode(layer.Element, BitmapScalingMode.HighQuality);
                break;
            case "MSAA":
                RenderOptions.SetBitmapScalingMode(layer.Element, BitmapScalingMode.Linear);
                break;
            case "TXAA":
                RenderOptions.SetBitmapScalingMode(layer.Element, BitmapScalingMode.Unspecified);
                break;
            default:
                ApplyScalingHeuristic();
                break;
        }
    }

    /// <summary>
    /// 交互（拖拽/缩放）期间切换到低质量采样：透明窗口为软件渲染，大图高质量重采样成本高，
    /// 低质量可显著提升跟手度；交互结束由 <see cref="ApplyAntiAliasing"/> 恢复。
    /// </summary>
    private void SetInteractiveSampling(bool interactive)
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        if (interactive)
        {
            _qualityTimer.Stop();
            RenderOptions.SetBitmapScalingMode(layer.Element, BitmapScalingMode.LowQuality);
        }
        else
        {
            ApplyAntiAliasing();
        }
    }

    /// <summary>适配模式基准倍率：大图缩小到工作区内，小图保持 1:1。</summary>
    private double ComputeFitScale()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return 1.0;
        }

        var workArea = ScreenService.GetWorkArea(this);
        return Math.Min(1.0, Math.Min(workArea.Width / layer.PixelWidth, workArea.Height / layer.PixelHeight));
    }

    private void UpdateTransform()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        // 任何手动变换都会终止进行中的平移动画（动画会锁定 _pan 的写入）。
        CancelPanAnimation();
        var displayWidth = Math.Max(layer.PixelWidth * layer.ZoomScale, 1);
        var displayHeight = Math.Max(layer.PixelHeight * layer.ZoomScale, 1);
        var contentScale = displayWidth / Math.Max(layer.ElementWidth, 1);
        layer.ApplyTransform(Width, Height);
        // 背景与活动图层同尺寸同位置：只覆盖图片区域，窗口其余部分保持透明。
        Backdrop.Width = layer.ElementWidth;
        Backdrop.Height = layer.ElementHeight;
        // 棋盘格纹样保持屏幕恒定大小：图片缩放时反向补偿画刷平铺尺寸，
        // 否则高分辨率图缩小后格子被压成细密纹理、放大后变成大色块。
        if (Backdrop.Background is DrawingBrush checker && checker.TileMode == TileMode.Tile)
        {
            checker.Viewport = new Rect(0, 0, CheckerCellSize / contentScale, CheckerCellSize / contentScale);
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mosaicActive)
        {
            e.Handled = true;
            return;
        }

        var now = Environment.TickCount64;
        if (now - _lastWheelZoomTick < WheelZoomThrottleMs)
        {
            e.Handled = true;
            return;
        }

        _lastWheelZoomTick = now;
        // 缩放步进按图片分辨率自适应（小图放大更快、大图缩小更快），
        // 普通滚轮每格即一个步进。
        var step = ComputeZoomStep(e.Delta > 0);
        var factor = e.Delta > 0 ? step : 1.0 / step;
        ZoomAt(e.GetPosition(this), factor);
        e.Handled = true;
    }

    /// <summary>
    /// 按图片分辨率自适应缩放步进：图片长边相对窗口长边的比例决定步进——
    /// 放大时图片越小步进越大（小图快速放大到可看尺寸），缩小时图片越大步进越大
    /// （大图快速缩小回适配），图片尺寸与窗口相近时保持默认 ×1.10。
    /// </summary>
    private double ComputeZoomStep(bool zoomIn)
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return ZoomStep;
        }

        var workArea = ScreenService.GetWorkArea(this);
        var windowLong = Math.Max(workArea.Width, workArea.Height);
        var imageLong = Math.Max(Math.Max(layer.PixelWidth, layer.PixelHeight), 1);
        var ratio = imageLong / Math.Max(windowLong, 1);

        var boost = zoomIn
            ? Math.Clamp(1.0 / ratio, 1.0, MaxAdaptiveBoost)
            : Math.Clamp(ratio, 1.0, MaxAdaptiveBoost);
        return ZoomStep * boost;
    }

    /// <summary>以指针为锚点缩放：窗口保持全屏，仅图片缩放，指针下的图像点不动。</summary>
    private void ZoomAt(Point pointer, double factor)
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        var target = Math.Clamp(layer.ZoomScale * factor, MinZoom, MaxZoom);
        if (Math.Abs(target - layer.ZoomScale) < 1e-9)
        {
            return;
        }

        // 缩放期间低质量采样，停止滚动 300ms 后由定时器恢复高质量。
        SetInteractiveSampling(true);
        _qualityTimer.Stop();
        _qualityTimer.Start();

        // 指针下的图像元素坐标（用当前变换反解）
        var oldContentScale = layer.Scale.ScaleX;
        var oldPanX = layer.Pan.X;
        var oldPanY = layer.Pan.Y;
        var imageX = (pointer.X - oldPanX) / oldContentScale;
        var imageY = (pointer.Y - oldPanY) / oldContentScale;

        layer.ZoomScale = target;
        var displayWidth = Math.Max(layer.PixelWidth * target, 1);
        var displayHeight = Math.Max(layer.PixelHeight * target, 1);
        var contentScale = displayWidth / Math.Max(layer.ElementWidth, 1);
        var centeringX = (Width - displayWidth) / 2.0;
        var centeringY = (Height - displayHeight) / 2.0;

        // 固定窗口：调整 userPan 使指针下的图像点保持在指针位置。
        layer.UserPan = new Point(
            pointer.X - contentScale * imageX - centeringX,
            pointer.Y - contentScale * imageY - centeringY);
        UpdateTransform();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        // 中键按下先记录，位移超过阈值才进入平移；位移小则松开时视为“中键单击”（去除鼠标下内容）。
        _middleDown = true;
        _middleDragged = false;
        _middleDownPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            if (_middleDown && !_middleDragged)
            {
                // Shift+中键：一键删除所有马赛克/图层（仅保留第一张）；普通中键单击只删鼠标下内容。
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    ShiftMiddleClickRemoveAll(_middleDownPoint);
                }
                else
                {
                    MiddleClickRemove(_middleDownPoint);
                }
            }

            _middleDown = false;
            if (_isPanning)
            {
                _isPanning = false;
                ReleaseMouseCapture();
                SetInteractiveSampling(false);
                CenterImageIfLost();
            }

            return;
        }

        if (_mosaicActive && _mosaicSelecting && e.ChangedButton == MouseButton.Left)
        {
            _mosaicSelecting = false;
            UpdateMosaicPreview();
            if (_mosaicPreview is not null)
            {
                _mosaicEffects.Add(_mosaicPreview); // 效果层保留在底图图层上（随图片变换）
                _mosaicPreview = null;
            }

            if (_mosaicBox is not null)
            {
                _mosaicBox.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (e.ChangedButton == MouseButton.Left && _isDraggingImage)
        {
            _isDraggingImage = false;
            ReleaseMouseCapture();
            EndImageDrag();
        }
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (_mosaicActive && _mosaicSelecting)
        {
            _mosaicRect = new Rect(_mosaicStart, position);
            UpdateMosaicPreview();
            return;
        }

        if (_isDraggingImage)
        {
            // 左键拖拽：窗口保持全屏，活动图层跟随鼠标平移（绝对定位，不累积误差）。
            var layer = _activeLayer;
            if (layer is not null)
            {
                layer.UserPan = new Point(
                    _dragStartPan.X + position.X - _dragStartPoint.X,
                    _dragStartPan.Y + position.Y - _dragStartPoint.Y);
                UpdateTransform();
            }

            return;
        }

        if (_middleDown && !_middleDragged && !_mosaicActive)
        {
            // 中键按住移动超过阈值即进入平移；单击（小位移）不触发平移。
            if (Math.Abs(position.X - _middleDownPoint.X) > 3 ||
                Math.Abs(position.Y - _middleDownPoint.Y) > 3)
            {
                _middleDragged = true;
                _isPanning = true;
                _lastPanPoint = position;
                SetInteractiveSampling(true);
                CaptureMouse();
            }

            return;
        }

        if (_isPanning)
        {
            var active = _activeLayer;
            if (active is not null)
            {
                active.UserPan = new Point(
                    active.UserPan.X + position.X - _lastPanPoint.X,
                    active.UserPan.Y + position.Y - _lastPanPoint.Y);
                _lastPanPoint = position;
                UpdateTransform();
            }

            return;
        }

        // 悬停即选中：鼠标移到哪个图层上，滚轮缩放/中键平移/双击就直接作用于该图层，
        // 无需先点击选中；移到空白处保持当前选中图层不变。
        var hover = HitTestLayer(position);
        if (hover is not null)
        {
            SetActiveLayer(hover);
        }
    }

    private void ToggleZoomMode()
    {
        _settings.ZoomMode = _settings.ZoomMode == "Fit" ? "Original" : "Fit";
        ApplyZoomMode();
        SaveSettings();
    }

    #endregion

    #region 拖拽移动 / 图层

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mosaicActive)
        {
            BeginMosaicSelection(e);
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            // 双击切换缩放模式：先取消第一次单击可能开启的拖拽。
            CancelImageDrag();
            ToggleZoomMode();
            e.Handled = true;
            return;
        }

        // 单击：命中测试选择图层（点中哪层就拖哪层），点空白不拖拽。
        var hit = HitTestLayer(e.GetPosition(this));
        if (hit is null)
        {
            return;
        }

        SetActiveLayer(hit);
        BeginImageDrag(e);
    }

    /// <summary>返回鼠标位置（窗口坐标）命中的最上层可见图层，未命中返回 null。</summary>
    private ImageLayer? HitTestLayer(Point windowPoint)
    {
        var screenPoint = new Point(Left + windowPoint.X, Top + windowPoint.Y);
        for (int i = _layers.Count - 1; i >= 0; i--)
        {
            var layer = _layers[i];
            if (!layer.Visible)
            {
                continue;
            }

            if (GetLayerRect(layer).Contains(screenPoint))
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>
    /// 开始移动图层：窗口保持全屏不动，图层通过 pan 平移跟随鼠标。
    /// 不再缩放窗口尺寸（透明窗口缩放尺寸会触发整窗软件重绘闪烁）。
    /// </summary>
    private void BeginImageDrag(MouseButtonEventArgs e)
    {
        CancelPanAnimation();
        _isDraggingImage = true;
        _dragStartPoint = e.GetPosition(this);
        _dragStartPan = _activeLayer!.UserPan;
        SetInteractiveSampling(true);
        CaptureMouse();
    }

    /// <summary>取消进行中的图片拖拽（如双击切换缩放模式时）。</summary>
    private void CancelImageDrag()
    {
        if (!_isDraggingImage)
        {
            return;
        }

        _isDraggingImage = false;
        ReleaseMouseCapture();
    }

    /// <summary>结束图片拖拽：位移超过阈值时检测出屏回中心，并保存图片位置。</summary>
    private void EndImageDrag()
    {
        SetInteractiveSampling(false);
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        var moved = Math.Abs(layer.UserPan.X - _dragStartPan.X) > 0.5 ||
                    Math.Abs(layer.UserPan.Y - _dragStartPan.Y) > 0.5;
        if (moved)
        {
            // 图片完全移出屏幕（不可见即无法取回）时自动回中心。
            CenterImageIfLost();
        }

        ScheduleSave();
    }

    /// <summary>
    /// 活动图层完全超出屏幕（与工作区无任何重叠，不可见且无法取回）时自动回到屏幕中心。
    /// 返回是否执行了回中心。
    /// </summary>
    private bool CenterImageIfLost()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return false;
        }

        var rect = GetLayerRect(layer);
        var workArea = ScreenService.GetWorkArea(this);
        if (rect.Right > workArea.Left && rect.Left < workArea.Right &&
            rect.Bottom > workArea.Top && rect.Top < workArea.Bottom)
        {
            return false;
        }

        // 回到屏幕中心：userPan 归零后图片自然居中，pan 动画过渡。
        layer.UserPan = new Point(0, 0);
        var displayWidth = Math.Max(layer.PixelWidth * layer.ZoomScale, 1);
        var displayHeight = Math.Max(layer.PixelHeight * layer.ZoomScale, 1);
        AnimatePanTo(new Point((Width - displayWidth) / 2.0, (Height - displayHeight) / 2.0));
        return true;
    }

    /// <summary>图层在当前屏幕上的矩形（全屏窗口：窗口左上角 + 平移偏移）。</summary>
    private Rect GetLayerRect(ImageLayer layer)
    {
        var displayWidth = Math.Max(layer.PixelWidth * layer.ZoomScale, 1);
        var displayHeight = Math.Max(layer.PixelHeight * layer.ZoomScale, 1);
        return new Rect(Left + layer.Pan.X, Top + layer.Pan.Y, displayWidth, displayHeight);
    }

    /// <summary>平移动画：平滑把活动图层（pan）移动到目标位置（出屏回中心用）。</summary>
    private void AnimatePanTo(Point targetPan)
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation(layer.Pan.X, targetPan.X, duration) { EasingFunction = ease };
        var animY = new DoubleAnimation(layer.Pan.Y, targetPan.Y, duration) { EasingFunction = ease };
        animX.Completed += (_, _) =>
        {
            layer.Pan.BeginAnimation(TranslateTransform.XProperty, null);
            layer.Pan.X = targetPan.X;
        };
        animY.Completed += (_, _) =>
        {
            layer.Pan.BeginAnimation(TranslateTransform.YProperty, null);
            layer.Pan.Y = targetPan.Y;
        };
        layer.Pan.BeginAnimation(TranslateTransform.XProperty, animX);
        layer.Pan.BeginAnimation(TranslateTransform.YProperty, animY);
    }

    /// <summary>取消平移动画；动画进行到一半时先把当前动画值固化，避免图片跳回动画前位置。</summary>
    private void CancelPanAnimation()
    {
        var layer = _activeLayer;
        if (layer is null || !layer.Pan.HasAnimatedProperties)
        {
            return;
        }

        var x = layer.Pan.X;
        var y = layer.Pan.Y;
        layer.Pan.BeginAnimation(TranslateTransform.XProperty, null);
        layer.Pan.BeginAnimation(TranslateTransform.YProperty, null);
        layer.Pan.X = x;
        layer.Pan.Y = y;
    }

    #endregion

    #region 右键菜单

    private void Window_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_mosaicActive)
        {
            // 马赛克绘制模式下：右键 = 直接退出绘制模式（效果保留在屏幕上），不弹菜单。
            e.Handled = true;
            ExitMosaic();
            return;
        }

        ContextMenu = BuildContextMenu();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateCheckItem("窗口置顶", _settings.Topmost, () =>
        {
            _settings.Topmost = !_settings.Topmost;
            Topmost = _settings.Topmost;
            SaveSettings();
        }));

        // 添加图片是主入口（顶层直接点击）；图层管理作为子菜单。
        menu.Items.Add(CreateItem("添加图片...", AddImageFile));
        menu.Items.Add(CreateLayerSubmenu());
        menu.Items.Add(CreateMosaicSubmenu());

        menu.Items.Add(CreateRadioSubmenu(
            "缩放模式",
            new[] { ("适配窗口", "Fit"), ("原始大小", "Original"), ("拉伸填充", "Stretch") },
            _settings.ZoomMode,
            value =>
            {
                _settings.ZoomMode = value;
                ApplyZoomMode();
                SaveSettings();
            }));

        menu.Items.Add(CreateRadioSubmenu(
            "背景模式",
            new[]
            {
                ("完全透明", "Transparent"),
                ("黑色", "Black"),
                ("白色", "White"),
                ("Alpha棋盘格", "Checkerboard"),
            },
            _settings.BackgroundMode,
            value =>
            {
                _settings.BackgroundMode = value;
                ApplyBackdrop();
                SaveSettings();
            }));

        menu.Items.Add(CreateGimmickSubmenu());
        menu.Items.Add(CreateOpacitySubmenu());
        menu.Items.Add(CreateCacheSubmenu());
        menu.Items.Add(CreateSlideshowItem());
        menu.Items.Add(CreateGifPauseItem());
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("更换图片", ChangeImage));
        menu.Items.Add(CreateItem("关闭图片", RemoveActiveLayer));
        menu.Items.Add(CreateItem("重置窗口", ResetWindow));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("退出程序", () => Close()));

        return menu;
    }

    private static MenuItem CreateItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private MenuItem CreateCacheSubmenu()
    {
        var submenu = new MenuItem { Header = "图片缓存" };
        submenu.Items.Add(CreateRadioSubmenu(
            "策略",
            new[] { ("按数量", "Count"), ("按大小", "Size") },
            _settings.CacheStrategy,
            value =>
            {
                _settings.CacheStrategy = value;
                ImageCache.Configure(_settings.CacheStrategy, _settings.CacheLimit);
                SaveSettings();
            }));

        var presets = _settings.CacheStrategy == "Size"
            ? new (string, double)[] { ("128 MB", 128), ("256 MB", 256), ("512 MB", 512), ("1024 MB", 1024) }
            : new (string, double)[] { ("10 张", 10), ("20 张", 20), ("50 张", 50), ("100 张", 100) };
        submenu.Items.Add(CreateValueSubmenu(
            "上限",
            presets,
            _settings.CacheLimit,
            value =>
            {
                _settings.CacheLimit = (int)value;
                ImageCache.Configure(_settings.CacheStrategy, _settings.CacheLimit);
                SaveSettings();
            },
            () => PickCacheLimit()));

        submenu.Items.Add(CreateItem("清除缓存", () =>
        {
            ImageCache.Clear();
            SaveSettings();
        }));
        return submenu;
    }

    private double? PickCacheLimit()
        => _settings.CacheStrategy == "Size"
            ? PickValue("缓存上限", 32, 8192, _settings.CacheLimit, "{0:0} MB")
            : PickValue("缓存上限", 5, 500, _settings.CacheLimit, "{0:0} 张");

    /// <summary>
    /// “无用小功能”子菜单：抗锯齿（模式 + 参数预设），装饰性设置。
    /// WPF 渲染管线无 MSAA/TXAA 硬件支持，抗锯齿模式映射为位图缩放采样质量，实际效果有限。
    /// </summary>
    private MenuItem CreateGimmickSubmenu()
    {
        var submenu = new MenuItem { Header = "无用小功能" };

        var aa = new MenuItem { Header = "抗锯齿" };
        aa.Items.Add(CreateRadioSubmenu(
            "模式",
            new[] { ("关闭", "Off"), ("SSAA", "SSAA"), ("MSAA", "MSAA"), ("TXAA", "TXAA") },
            _settings.AntiAliasing,
            value =>
            {
                _settings.AntiAliasing = value;
                ApplyAntiAliasing();
                SaveSettings();
            }));
        aa.Items.Add(CreateRadioSubmenu(
            "SSAA 倍率",
            new[] { ("2x", "2"), ("4x", "4"), ("8x", "8") },
            _settings.SsaaLevel.ToString(),
            value =>
            {
                _settings.SsaaLevel = int.Parse(value);
                SaveSettings();
            }));
        aa.Items.Add(CreateRadioSubmenu(
            "MSAA 采样",
            new[] { ("2x", "2"), ("4x", "4"), ("8x", "8") },
            _settings.MsaaLevel.ToString(),
            value =>
            {
                _settings.MsaaLevel = int.Parse(value);
                SaveSettings();
            }));
        aa.Items.Add(CreateRadioSubmenu(
            "TXAA 质量",
            new[] { ("低", "Low"), ("中", "Medium"), ("高", "High") },
            _settings.TxaaQuality,
            value =>
            {
                _settings.TxaaQuality = value;
                SaveSettings();
            }));
        submenu.Items.Add(aa);
        return submenu;
    }

    private static MenuItem CreateCheckItem(string header, bool isChecked, Action toggle)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => toggle();
        return item;
    }

    private MenuItem CreateRadioSubmenu(
        string header,
        IReadOnlyList<(string Label, string Value)> options,
        string current,
        Action<string> onSelect)
    {
        var submenu = new MenuItem { Header = header };
        var items = new List<MenuItem>();
        foreach (var (label, value) in options)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = value == current,
            };
            item.Click += (_, _) =>
            {
                SetGroupChecked(items, item);
                onSelect(value);
            };
            items.Add(item);
            submenu.Items.Add(item);
        }

        return submenu;
    }

    private MenuItem CreateValueSubmenu(
        string header,
        IReadOnlyList<(string Label, double Value)> presets,
        double current,
        Action<double> onSelect,
        Func<double?> pickCustom)
    {
        var submenu = new MenuItem { Header = header };
        var items = new List<MenuItem>();
        foreach (var (label, value) in presets)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = Math.Abs(value - current) < 1e-6,
            };
            item.Click += (_, _) =>
            {
                SetGroupChecked(items, item);
                onSelect(value);
            };
            items.Add(item);
            submenu.Items.Add(item);
        }

        var custom = new MenuItem { Header = "自定义...", IsCheckable = true };
        custom.Click += (_, _) =>
        {
            var picked = pickCustom();
            if (picked is double value)
            {
                SetGroupChecked(items, null);
                onSelect(value);
            }
            else
            {
                custom.IsChecked = false;
            }
        };
        items.Add(custom);
        submenu.Items.Add(custom);
        return submenu;
    }

    private static void SetGroupChecked(IReadOnlyList<MenuItem> group, MenuItem? selected)
    {
        foreach (var item in group)
        {
            item.IsChecked = ReferenceEquals(item, selected);
        }
    }

    private MenuItem CreateOpacitySubmenu()
    {
        var presets = new (string, double)[]
        {
            ("100%", 100),
            ("80%", 80),
            ("60%", 60),
            ("40%", 40),
            ("20%", 20),
        };
        return CreateValueSubmenu(
            "不透明度",
            presets,
            _settings.OpacityPercent,
            value =>
            {
                _settings.OpacityPercent = value;
                Opacity = value / 100.0;
                SaveSettings();
            },
            () => PickOpacityLive());
    }

    /// <summary>不透明度自定义：拖动滑块实时生效，确定后保存，取消恢复原值。</summary>
    private double? PickOpacityLive()
    {
        var original = _settings.OpacityPercent;
        var dialog = new SliderDialog("不透明度", 0, 100, _settings.OpacityPercent, "{0:0}%") { Owner = this };
        dialog.ValueChanged += value => Opacity = value / 100.0;
        if (dialog.ShowDialog() == true && dialog.ResultValue is double value)
        {
            return value;
        }

        Opacity = original / 100.0;
        return null;
    }

    private MenuItem CreateIntervalSubmenu()
    {
        var presets = new (string, double)[]
        {
            ("1秒", 1),
            ("2秒", 2),
            ("3秒", 3),
            ("5秒", 5),
            ("10秒", 10),
        };
        return CreateValueSubmenu(
            "轮播间隔",
            presets,
            _settings.SlideshowIntervalSeconds,
            value =>
            {
                _settings.SlideshowIntervalSeconds = (int)value;
                if (_slideshowPlaying)
                {
                    ResumeSlideshow();
                }

                SaveSettings();
            },
            () => PickValue("轮播间隔", 1, 60, _settings.SlideshowIntervalSeconds, "{0:0} 秒"));
    }

    private MenuItem CreateTransitionSubmenu()
    {
        var transition = CreateRadioSubmenu(
            "切换动画",
            new[]
            {
                ("无动画", "None"),
                ("淡入淡出", "Fade"),
                ("黑切", "BlackCut"),
                ("划入", "Slide"),
            },
            _settings.SlideTransition,
            value =>
            {
                _settings.SlideTransition = value;
                SaveSettings();
            });

        var presets = new (string, double)[]
        {
            ("100 ms", 100),
            ("200 ms", 200),
            ("300 ms", 300),
            ("500 ms", 500),
            ("800 ms", 800),
            ("1200 ms", 1200),
            ("2000 ms", 2000),
        };
        transition.Items.Add(CreateValueSubmenu(
            "时间",
            presets,
            _settings.TransitionDurationMs,
            value =>
            {
                _settings.TransitionDurationMs = (int)value;
                SaveSettings();
            },
            () => PickNumber(
                "切换动画时间",
                50,
                3000,
                _settings.TransitionDurationMs,
                "请输入毫秒数值（50 – 3000）")));

        transition.Items.Add(CreateRadioSubmenu(
            "切入方向",
            new[]
            {
                ("从左侧", "Left"),
                ("从右侧", "Right"),
                ("从上方", "Up"),
                ("从下方", "Down"),
            },
            _settings.SlideDirection,
            value =>
            {
                _settings.SlideDirection = value;
                SaveSettings();
            }));

        return transition;
    }

    private double? PickValue(
        string title,
        double min,
        double max,
        double current,
        string format)
    {
        var dialog = new SliderDialog(title, min, max, current, format) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultValue is double value)
        {
            return value;
        }

        return null;
    }

    private double? PickNumber(
        string title,
        int min,
        int max,
        int current,
        string hint)
    {
        var dialog = new NumberDialog(title, min, max, current, hint) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.ResultValue is int value)
        {
            return value;
        }

        return null;
    }

    private MenuItem CreateSlideshowItem()
    {
        var submenu = new MenuItem { Header = "幻灯片放映" };

        if (_slideshowActive)
        {
            var playPause = new MenuItem
            {
                Header = _slideshowPlaying ? "暂停轮播" : "继续轮播",
                IsCheckable = true,
                IsChecked = _slideshowPlaying,
            };
            playPause.Click += (_, _) =>
            {
                if (_slideshowPlaying)
                {
                    PauseSlideshow();
                }
                else
                {
                    ResumeSlideshow();
                }

                playPause.Header = _slideshowPlaying ? "暂停轮播" : "继续轮播";
                playPause.IsChecked = _slideshowPlaying;
            };
            submenu.Items.Add(playPause);

            submenu.Items.Add(CreateItem("上一张", ShowPrevious));
            submenu.Items.Add(CreateItem("下一张", ShowNext));
        }
        else
        {
            submenu.Items.Add(CreateItem("开始放映...", StartSlideshow));
        }

        submenu.Items.Add(CreateCheckItem("循环模式", _settings.SlideshowLoop, () =>
        {
            _settings.SlideshowLoop = !_settings.SlideshowLoop;
            SaveSettings();
        }));
        submenu.Items.Add(CreateIntervalSubmenu());
        submenu.Items.Add(CreateTransitionSubmenu());
        if (_slideshowActive)
        {
            submenu.Items.Add(CreateItem("退出幻灯片", ExitSlideshow));
        }

        return submenu;
    }

    /// <summary>
    /// GIF 暂停总控：暂停/继续所有 GIF 图层（每个图层默认独立播放）。
    /// </summary>
    private MenuItem CreateGifPauseItem()
    {
        var gifs = _layers.Where(l => l.Gif is { IsAnimatedGif: true }).Select(l => l.Gif!).ToList();
        var anyPaused = gifs.Any(g => g.IsPaused);
        var item = new MenuItem
        {
            Header = anyPaused ? "继续GIF动画" : "暂停GIF动画",
            IsEnabled = gifs.Count > 0,
        };
        item.Click += (_, _) =>
        {
            foreach (var gif in gifs)
            {
                if (gif.IsPaused)
                {
                    gif.Resume();
                }
                else
                {
                    gif.Pause();
                }
            }

            item.Header = anyPaused ? "暂停GIF动画" : "继续GIF动画";
        };
        return item;
    }

    #endregion

    #region 功能操作

    #region 框选马赛克

    /// <summary>
    /// 进入框选马赛克模式：把当前画面渲染为覆盖全屏的底图（图片外区域为白色画布），
    /// 清空所有图层，之后左键拖拽框选区域实时应用所选样式；效果层覆盖在屏幕层上，全屏可绘制。
    /// </summary>
    private void StartMosaic()
    {
        if (_mosaicActive)
        {
            return;
        }

        int width = Math.Max(1, (int)Width);
        int height = Math.Max(1, (int)Height);
        // 底图仅作为内存取样源（不显示）：窗口保持透明，图片外区域实时透出屏幕内容。
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(SlideLayer);
        rtb.Freeze();

        // 清空所有图层
        foreach (var layer in _layers)
        {
            layer.Gif?.Stop();
            LayerHost.Children.Remove(layer.Canvas);
        }

        _layers.Clear();
        _activeLayer = null;

        // 底图作为唯一图层：锁定 1:1 铺满窗口，窗口坐标即底图像素坐标。
        _mosaicBase = rtb;
        var baseLayer = AddLayer("马赛克底图", rtb);
        _mosaicBaseLayer = baseLayer;
        baseLayer.ZoomScale = 1.0;
        baseLayer.UserPan = new Point(0, 0);
        UpdateTransform();

        // 清空上一轮的效果层（旧底图已随图层清空移除，这里仅清列表）。
        _mosaicEffects.Clear();
        _mosaicActive = true;
        _mosaicSelecting = false;
        _mosaicPreview = null;
        _mosaicBox = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 79, 195, 247)),
            BorderThickness = new Thickness(1.5),
            Visibility = Visibility.Collapsed,
        };
        baseLayer.Canvas.Children.Add(_mosaicBox);
    }

    /// <summary>退出框选马赛克模式（效果层保留在底图图层上，随图片缩放/平移）。</summary>
    private void ExitMosaic()
    {
        _mosaicActive = false;
        _mosaicSelecting = false;
        _mosaicPreview = null;
        _mosaicBox = null;
    }

    /// <summary>开始一次框选：创建效果层与框选虚线框（挂载到底图图层，随图片变换）。</summary>
    private void BeginMosaicSelection(MouseButtonEventArgs e)
    {
        _mosaicSelecting = true;
        _mosaicStart = e.GetPosition(this);
        _mosaicRect = new Rect(_mosaicStart, new Size(0, 0));
        _mosaicPreview = new Image
        {
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        _mosaicBaseLayer?.Canvas.Children.Add(_mosaicPreview);
        if (_mosaicBox is not null)
        {
            _mosaicBox.Visibility = Visibility.Visible;
        }
    }

    /// <summary>按当前框选矩形实时生成并叠加效果（底图锁定 1:1 时窗口坐标 = 底图坐标）。</summary>
    private void UpdateMosaicPreview()
    {
        if (_mosaicBase is null || _mosaicPreview is null || _mosaicBox is null)
        {
            return;
        }

        var rect = _mosaicRect;
        if (rect.Width < 2 || rect.Height < 2)
        {
            _mosaicPreview.Source = null;
            return;
        }

        int x = (int)Math.Round(rect.X);
        int y = (int)Math.Round(rect.Y);
        int w = (int)Math.Round(rect.Width);
        int h = (int)Math.Round(rect.Height);
        var effect = MosaicRenderer.Apply(
            _mosaicBase,
            new Int32Rect(x, y, w, h),
            _mosaicStyle,
            CurrentMosaicParam,
            _mosaicColor);
        _mosaicPreview.Width = w;
        _mosaicPreview.Height = h;
        Canvas.SetLeft(_mosaicPreview, x);
        Canvas.SetTop(_mosaicPreview, y);
        _mosaicPreview.Source = effect;

        _mosaicBox.Width = w;
        _mosaicBox.Height = h;
        Canvas.SetLeft(_mosaicBox, x);
        Canvas.SetTop(_mosaicBox, y);
    }

    /// <summary>返回鼠标位置命中的最上层马赛克效果层（底图坐标），未命中返回 null。</summary>
    private Image? HitTestMosaicLayer(Point position)
    {
        for (int i = _mosaicEffects.Count - 1; i >= 0; i--)
        {
            var effect = _mosaicEffects[i];
            var rect = new Rect(Canvas.GetLeft(effect), Canvas.GetTop(effect), effect.Width, effect.Height);
            if (rect.Contains(position))
            {
                return effect;
            }
        }

        return null;
    }

    /// <summary>移除指定的马赛克效果层（中键擦除）。</summary>
    private void RemoveMosaicLayer(Image effect)
    {
        _mosaicEffects.Remove(effect);
        _mosaicBaseLayer?.Canvas.Children.Remove(effect);
    }

    /// <summary>
    /// Shift+中键单击：一键删除所有马赛克效果层与所有图片图层（仅保留第一张）。
    /// 鼠标必须位于马赛克或图片上才触发；删除不区分类型，鼠标在什么上面都删全部。
    /// </summary>
    private void ShiftMiddleClickRemoveAll(Point position)
    {
        bool onMosaic = HitTestMosaicLayer(position) is not null;
        bool onLayer = HitTestLayer(position) is not null;
        if (!onMosaic && !onLayer)
        {
            return;
        }

        foreach (var effect in _mosaicEffects.ToList())
        {
            RemoveMosaicLayer(effect);
        }

        while (_layers.Count > 1)
        {
            RemoveLayer(_layers[1]);
        }
    }

    /// <summary>
    /// 中键单击（位移小于阈值）：擦除鼠标所在位置的马赛克效果层。
    /// 图片图层不通过中键删除（避免误触导致程序退出），删除请用菜单或 Shift+中键。
    /// </summary>
    private void MiddleClickRemove(Point position)
    {
        var mosaicHit = HitTestMosaicLayer(position);
        if (mosaicHit is not null)
        {
            RemoveMosaicLayer(mosaicHit);
        }
    }

    /// <summary>删除指定图层；删除活动图层后就近选中相邻图层；删光时退出程序。</summary>
    private void RemoveLayer(ImageLayer layer)
    {
        int index = _layers.IndexOf(layer);
        _layers.Remove(layer);
        LayerHost.Children.Remove(layer.Canvas);
        layer.Gif?.Stop();
        if (_activeLayer == layer)
        {
            _activeLayer = null;
            if (_layers.Count == 0)
            {
                Close();
                return;
            }

            SetActiveLayer(_layers[Math.Min(index, _layers.Count - 1)]);
        }

        ScheduleSave();
    }

    /// <summary>删除活动图层（右键菜单“关闭图片”）。</summary>
    private void RemoveActiveLayer()
    {
        if (_activeLayer is { } layer)
        {
            RemoveLayer(layer);
        }
    }

    /// <summary>当前样式的效果参数。</summary>
    private double CurrentMosaicParam => _mosaicStyle switch
    {
        MosaicRenderer.Style.Mosaic => _mosaicBlockPx,
        MosaicRenderer.Style.Blur => _mosaicBlurPx,
        MosaicRenderer.Style.Smudge => _mosaicSmudgePx,
        _ => 0,
    };

    /// <summary>自定义纯色：系统色盘。</summary>
    private void PickMosaicColor()
    {
        var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(_mosaicColor.A, _mosaicColor.R, _mosaicColor.G, _mosaicColor.B),
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _mosaicColor = Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
        }
    }

    /// <summary>框选马赛克子菜单：样式 + 各样式参数（预设/自定义）+ 开始框选/退出。</summary>
    private MenuItem CreateMosaicSubmenu()
    {
        var submenu = new MenuItem { Header = "框选马赛克" };
        submenu.Items.Add(CreateRadioSubmenu(
            "样式",
            new[] { ("马赛克", "Mosaic"), ("高斯模糊", "Blur"), ("噪声", "Smudge"), ("纯色", "Solid") },
            _mosaicStyle.ToString(),
            value => _mosaicStyle = Enum.Parse<MosaicRenderer.Style>(value)));
        submenu.Items.Add(CreateValueSubmenu(
            "马赛克大小",
            new[] { ("8 px", 8d), ("16 px", 16d), ("32 px", 32d), ("64 px", 64d) },
            _mosaicBlockPx,
            value => _mosaicBlockPx = value,
            () => PickNumber("马赛克大小", 2, 200, (int)_mosaicBlockPx, "请输入像素数值（2 – 200）")));
        submenu.Items.Add(CreateValueSubmenu(
            "模糊像素",
            new[] { ("4 px", 4d), ("8 px", 8d), ("16 px", 16d), ("32 px", 32d) },
            _mosaicBlurPx,
            value => _mosaicBlurPx = value,
            () => PickNumber("模糊像素", 1, 100, (int)_mosaicBlurPx, "请输入像素数值（1 – 100）")));
        submenu.Items.Add(CreateValueSubmenu(
            "噪声像素",
            new[] { ("4 px", 4d), ("8 px", 8d), ("16 px", 16d), ("32 px", 32d) },
            _mosaicSmudgePx,
            value => _mosaicSmudgePx = value,
            () => PickNumber("噪声像素", 1, 100, (int)_mosaicSmudgePx, "请输入像素数值（1 – 100）")));
        submenu.Items.Add(CreateRadioSubmenu(
            "纯色",
            new[]
            {
                ("黑色", "#FF000000"), ("白色", "#FFFFFFFF"),
                ("红色", "#FFFF0000"), ("绿色", "#FF00FF00"),
                ("蓝色", "#FF0000FF"), ("黄色", "#FFFFFF00"),
                ("青色", "#FF00FFFF"), ("品红", "#FFFF00FF"),
            },
            _mosaicColor.ToString(),
            value => _mosaicColor = (Color)ColorConverter.ConvertFromString(value)!));
        submenu.Items.Add(CreateItem("自定义色盘...", PickMosaicColor));
        submenu.Items.Add(new Separator());
        submenu.Items.Add(CreateItem(_mosaicActive ? "退出马赛克" : "开始框选", () =>
        {
            if (_mosaicActive)
            {
                ExitMosaic();
            }
            else
            {
                StartMosaic();
            }
        }));
        return submenu;
    }

    #endregion

    /// <summary>选择并批量添加多张图片作为新图层（依次置于图层栈顶，可多选）。</summary>
    private void AddImageFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|所有文件|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        int added = 0;
        foreach (var fileName in dialog.FileNames)
        {
            try
            {
                var path = Path.GetFullPath(fileName);
                AddLayer(path);
                ApplyZoomMode();
                added++;
            }
            catch
            {
                // 单个文件解码失败不影响其余文件。
            }
        }

        if (added == 0)
        {
            MessageBox.Show(
                this,
                "所选文件都无法加载。",
                "添加图片",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// “图层”子菜单：图层列表/层序操作等子选项在添加图片后才显示
    /// （上移/下移仅在至少两张图时显示，单图时隐藏无意义的层序操作）。
    /// </summary>
    private MenuItem CreateLayerSubmenu()
    {
        var submenu = new MenuItem { Header = "图层" };
        if (_layers.Count == 0)
        {
            submenu.Items.Add(CreateItem("暂无图片", () => { }));
            return submenu;
        }

        for (int i = _layers.Count - 1; i >= 0; i--) // 顶层在上
        {
            var layer = _layers[i];
            var item = new MenuItem
            {
                Header = (ReferenceEquals(layer, _activeLayer) ? "▶ " : string.Empty) + Path.GetFileName(layer.Path),
                IsCheckable = true,
                IsChecked = layer.Visible,
            };
            item.Click += (_, _) =>
            {
                SetActiveLayer(layer);
                layer.Visible = !layer.Visible;
                layer.Element.Visibility = layer.Visible ? Visibility.Visible : Visibility.Collapsed;
                ScheduleSave();
            };
            submenu.Items.Add(item);
        }

        if (_layers.Count >= 2)
        {
            submenu.Items.Add(new Separator());
            submenu.Items.Add(CreateItem("上移一层", () => MoveActiveLayer(1)));
            submenu.Items.Add(CreateItem("下移一层", () => MoveActiveLayer(-1)));
        }

        submenu.Items.Add(CreateItem("删除图层", RemoveActiveLayer));
        return submenu;
    }

    /// <summary>把活动图层在图层栈中上移（1）或下移（-1）一层。</summary>
    private void MoveActiveLayer(int delta)
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        int index = _layers.IndexOf(layer);
        int target = index + delta;
        if (target < 0 || target >= _layers.Count)
        {
            return;
        }

        _layers.RemoveAt(index);
        _layers.Insert(target, layer);
        LayerHost.Children.Remove(layer.Canvas);
        LayerHost.Children.Insert(target, layer.Canvas);
        ScheduleSave();
    }

    private void ChangeImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "更换图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|所有文件|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = Path.GetFullPath(dialog.FileName);
        if (LoadImage(path) && _slideshowActive && _slideFiles is not null)
        {
            var index = _slideFiles.FindIndex(f =>
                string.Equals(Path.GetFullPath(f), path, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                _slideIndex = index;
            }
        }
    }

    private void ResetWindow()
    {
        var layer = _activeLayer;
        if (layer is null)
        {
            return;
        }

        _settings.ZoomMode = "Fit";
        CancelTransition();
        layer.ZoomScale = ComputeFitScale();
        layer.UserPan = new Point(0, 0);
        UpdateTransform();
        SaveSettings();
    }

    #endregion

    #region 幻灯片放映 / 切换动画

    private void StartSlideshow()
    {
        var dialog = new OpenFolderDialog { Title = "选择幻灯片文件夹" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(dialog.FolderName)
                .Where(ImageFileService.IsSupportedImage)
                .OrderBy(f => f, Comparer<string>.Create(ImageFileService.NaturalCompare))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法读取文件夹：\n{ex.Message}", "幻灯片放映", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (files.Count == 0)
        {
            MessageBox.Show(this, "该文件夹中没有支持的图片。", "幻灯片放映", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _slideFiles = files;
        var index = -1;
        if (_activeLayer is { } layer)
        {
            var full = Path.GetFullPath(layer.Path);
            index = files.FindIndex(f =>
                string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase));
        }

        _slideIndex = index >= 0 ? index : 0;
        _slideshowActive = true;
        LoadImage(files[_slideIndex]);
        ResumeSlideshow();
    }

    private void ResumeSlideshow()
    {
        if (!_slideshowActive || _slideFiles is null)
        {
            return;
        }

        _slideTimer.Interval = TimeSpan.FromSeconds(_settings.SlideshowIntervalSeconds);
        _slideTimer.Start();
        _slideshowPlaying = true;
    }

    private void PauseSlideshow()
    {
        _slideTimer.Stop();
        _slideshowPlaying = false;
    }

    private void ExitSlideshow()
    {
        _slideTimer.Stop();
        _slideshowPlaying = false;
        _slideshowActive = false;
        _slideFiles = null;
        CancelTransition();
    }

    private void SlideshowTimer_Tick(object? sender, EventArgs e)
    {
        if (_transitionBusy || _slideFiles is null || _slideFiles.Count == 0)
        {
            return;
        }

        if (_slideIndex + 1 >= _slideFiles.Count)
        {
            if (!_settings.SlideshowLoop)
            {
                _slideTimer.Stop();
                _slideshowPlaying = false;
                return;
            }

            LoadSlide(0);
        }
        else
        {
            LoadSlide(_slideIndex + 1);
        }
    }

    private void ShowNext()
    {
        if (_slideFiles is null || _slideFiles.Count == 0)
        {
            return;
        }

        if (_slideIndex + 1 < _slideFiles.Count)
        {
            LoadSlide(_slideIndex + 1);
        }
        else if (_settings.SlideshowLoop)
        {
            LoadSlide(0);
        }
    }

    private void ShowPrevious()
    {
        if (_slideFiles is null || _slideFiles.Count == 0)
        {
            return;
        }

        if (_slideIndex > 0)
        {
            LoadSlide(_slideIndex - 1);
        }
        else if (_settings.SlideshowLoop)
        {
            LoadSlide(_slideFiles.Count - 1);
        }
    }

    /// <summary>加载指定幻灯片并执行切换动画（加载失败时静默保留当前图片）。</summary>
    private void LoadSlide(int index)
    {
        if (_slideFiles is null || index < 0 || index >= _slideFiles.Count)
        {
            return;
        }

        // 预解码：失败则静默跳过（GIF 解码会占用全局单例，这里直接解码验证）。
        var path = _slideFiles[index];
        try
        {
            _ = string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase)
                ? new GifAnimation(new Image()).Load(path)
                : LoadStaticImage(path);
        }
        catch
        {
            return;
        }

        _slideIndex = index;
        RunSlideTransition(() =>
        {
            ReplaceActiveLayer(path);
            ApplyZoomMode();
        });
    }

    private void RunSlideTransition(Action swap)
    {
        CancelTransition();
        switch (_settings.SlideTransition)
        {
            case "Fade":
                FadeTransition(swap);
                break;
            case "BlackCut":
                BlackCutTransition(swap);
                break;
            case "Slide":
                SlideInTransition(swap);
                break;
            default:
                swap();
                break;
        }
    }

    private void FadeTransition(Action swap)
    {
        _transitionBusy = true;
        var oldLayer = _activeLayer;
        var half = TimeSpan.FromMilliseconds(Math.Max(1, _settings.TransitionDurationMs) / 2.0);
        var fadeOut = new DoubleAnimation(1, 0, half)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fadeOut.Completed += (_, _) =>
        {
            swap();
            var newLayer = _activeLayer;
            if (newLayer is null)
            {
                _transitionBusy = false;
                return;
            }

            var fadeIn = new DoubleAnimation(0, 1, half)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            fadeIn.Completed += (_, _) =>
            {
                newLayer.Element.Opacity = 1;
                _transitionBusy = false;
            };
            newLayer.Element.BeginAnimation(OpacityProperty, fadeIn);
        };
        oldLayer?.Element.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void BlackCutTransition(Action swap)
    {
        _transitionBusy = true;
        BlackOverlay.Opacity = 1;
        swap();
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Math.Max(1, _settings.TransitionDurationMs)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) =>
        {
            BlackOverlay.Opacity = 0;
            _transitionBusy = false;
        };
        BlackOverlay.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void SlideInTransition(Action swap)
    {
        _transitionBusy = true;
        var duration = TimeSpan.FromMilliseconds(Math.Max(1, _settings.TransitionDurationMs));
        double fromX = 0;
        double fromY = 0;
        switch (_settings.SlideDirection)
        {
            case "Right":
                fromX = Width;
                break;
            case "Up":
                fromY = -Height;
                break;
            case "Down":
                fromY = Height;
                break;
            default:
                fromX = -Width;
                break;
        }

        swap();
        _slideTransform.X = fromX;
        _slideTransform.Y = fromY;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation(fromX, 0, duration) { EasingFunction = ease };
        var animY = new DoubleAnimation(fromY, 0, duration) { EasingFunction = ease };
        int pending = 2;
        animX.Completed += (_, _) =>
        {
            _slideTransform.X = 0;
            if (--pending == 0)
            {
                _transitionBusy = false;
            }
        };
        animY.Completed += (_, _) =>
        {
            _slideTransform.Y = 0;
            if (--pending == 0)
            {
                _transitionBusy = false;
            }
        };
        _slideTransform.BeginAnimation(TranslateTransform.XProperty, animX);
        _slideTransform.BeginAnimation(TranslateTransform.YProperty, animY);
    }

    private void CancelTransition()
    {
        _transitionBusy = false;
        if (_activeLayer is { } layer)
        {
            layer.Element.BeginAnimation(OpacityProperty, null);
            layer.Element.Opacity = 1;
        }

        BlackOverlay.BeginAnimation(OpacityProperty, null);
        BlackOverlay.Opacity = 0;
        _slideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _slideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        _slideTransform.X = 0;
        _slideTransform.Y = 0;
    }

    #endregion

    #region 窗口事件

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        ScheduleSave();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTransform();
        ScheduleSave();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _saveTimer.Stop();
        _slideTimer.Stop();
        CancelTransition();
        foreach (var layer in _layers)
        {
            layer.Gif?.Stop();
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        SaveSettings();
    }

    #endregion

    #region 系统托盘

    /// <summary>创建托盘图标（窗口保持显示，仅任务栏图标隐藏；托盘左键激活窗口、右键弹菜单）。</summary>
    public void InitializeTray()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "浮窗看图器",
            Visible = true,
        };
        // 左键/双击：激活窗口
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Activate();
            }
        };
        // 右键：弹出与窗口右键菜单相同的菜单（在鼠标位置）
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };
    }

    /// <summary>从嵌入资源加载应用图标（发布单文件后 ico 不在 exe 旁，必须走资源）。</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/ico/叶黎的浮图查看器.ico", UriKind.RelativeOrAbsolute))?.Stream;
            if (stream is not null)
            {
                using (stream)
                {
                    return new System.Drawing.Icon(stream);
                }
            }
        }
        catch
        {
            // 资源加载失败回退系统图标。
        }

        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>在鼠标位置弹出与窗口右键相同的菜单。</summary>
    private void ShowTrayMenu()
    {
        var menu = BuildContextMenu();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    #endregion
}
