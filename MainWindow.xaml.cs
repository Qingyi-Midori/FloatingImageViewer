using System.ComponentModel;
using System.IO;
using System.Text;
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
    private readonly DispatcherTimer _clipboardTimer;
    private bool _clipboardWatch;
    private string? _lastClipboardFingerprint;
    private readonly TranslateTransform _slideTransform = new();
    private readonly List<ImageLayer> _layers = new();
    private ImageLayer? _activeLayer;

    private bool _isDraggingImage;
    private Point _dragStartPoint;
    private Point _dragStartPan;
    private Point _middleDownPoint;

    // 图片信息面板
    private bool _infoPanelEnabled;
    private readonly Canvas _infoCanvas = new() { IsHitTestVisible = false };
    private readonly Border _infoPanel = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 6, 10, 6),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _infoPanelText = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(240, 224, 224, 224)),
        FontSize = 12,
    };

    // 内联输入面板（替代数字/滑块弹窗，UI 与菜单统一）
    private bool _inputActive;
    private bool _inputSliderMode;
    private Action<double>? _inputCommit;
    private Action<double>? _inputLive;
    private string _inputFormat = "{0:0}";
    private double _inputCurrent;
    private readonly Border _inputPanel = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 12),
        Width = 260,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _inputTitle = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(240, 224, 224, 224)),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };
    private readonly Slider _inputSlider = new() { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _inputTextBox = new()
    {
        Margin = new Thickness(0, 4, 0, 0),
        FontSize = 13,
    };
    private readonly TextBlock _inputValue = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(220, 192, 235, 215)),
        FontSize = 13,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 4, 0, 0),
    };
    private readonly Button _inputOk = new() { Content = "确定", Width = 72, Margin = new Thickness(0, 10, 8, 0) };
    private readonly Button _inputCancel = new() { Content = "取消", Width = 72, Margin = new Thickness(0, 10, 0, 0) };

    // 图层透明度：独立条（图片内部偏下，常驻不随缩放）+ 全局面板（一个滚动条 + 展开按钮）
    private bool _hoverOpacityBarEnabled;
    private ImageLayer? _hoverSliderLayer;
    private bool _globalOpacityActive;
    private bool _globalOpacityExpanded;
    private ImageLayer? _globalOpacityTarget;
    /// <summary>独立条容器总宽度：滑条 140 + 左右 padding 8×2。</summary>
    private const double HoverBarWidth = 156;
    /// <summary>独立条容器总高度：滑条 20 + 上下 padding 4×2。</summary>
    private const double HoverBarHeight = 28;
    /// <summary>独立条：半透明灰黑圆角背景（菜单风格），尺寸固定，不随图片缩放。</summary>
    private readonly Border _hoverOpacityBar = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(215, 30, 30, 30)),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 4, 8, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
    };
    private readonly Slider _hoverOpacitySlider = new() { Width = 140, Minimum = 0, Maximum = 100 };
    private readonly Border _globalOpacityPanel = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 12),
        Width = 380,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _globalOpacityTitle = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(240, 224, 224, 224)),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };
    private readonly TextBlock _globalOpacityTargetName = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(230, 224, 224, 224)),
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, 0, 10, 0),
    };
    private readonly Slider _globalOpacitySlider = new() { Minimum = 0, Maximum = 100 };
    private readonly TextBlock _globalOpacityPercent = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(220, 192, 235, 215)),
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 42,
        TextAlignment = TextAlignment.Right,
        Margin = new Thickness(10, 0, 0, 0),
    };
    private readonly Button _globalOpacityExpand = new() { Content = "展开全部图层", Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _globalOpacityRows = new();

    // 固定图片面板（仿全局透明度面板）：全局锁定开关 + 展开后每个图层单独固定
    private bool _globalFixedActive;
    private bool _globalFixedExpanded;
    private readonly Border _globalFixedPanel = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12, 14, 12),
        Width = 380,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock _globalFixedTitle = new()
    {
        Foreground = new SolidColorBrush(Color.FromArgb(240, 224, 224, 224)),
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
    };
    private readonly CheckBox _globalFixedAll = new()
    {
        Content = "锁定所有图层",
        Margin = new Thickness(0, 0, 0, 6),
    };
    private readonly Button _globalFixedExpand = new() { Content = "展开全部图层", Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _globalFixedRows = new();

    // 图片对比模式
    private bool _compareActive;
    private bool _compareSplitMode;
    private bool _compareTopBottom;
    private bool _compareFitSize;
    private double _compareSplit = 0.5;
    private bool _comparePanning;
    private bool _compareDraggingSplit;
    private Point _compareLastPoint;
    private string? _comparePathA;
    private GifAnimation? _compareGifA;
    private GifAnimation? _compareGifB;
    private const double CompareGap = 8;
    private readonly ScaleTransform _compareScale = new();
    private readonly TranslateTransform _comparePan = new();
    private readonly Image _compareA = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Image _compareB = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Border _compareSplitLine = new()
    {
        Width = 2,
        Background = new SolidColorBrush(Color.FromArgb(220, 192, 235, 215)),
        IsHitTestVisible = false,
    };
    private readonly Canvas _compareCanvas = new() { IsHitTestVisible = false };

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

    public MainWindow(string? imagePath)
    {
        InitializeComponent();

        _settings = SettingsService.Load();
        ApplyPersistedState();
        ImageCache.Configure(_settings.CacheStrategy, _settings.CacheLimit);
        _clipboardWatch = _settings.ClipboardWatch;
        _clipboardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clipboardTimer.Tick += ClipboardTimer_Tick;
        _clipboardTimer.IsEnabled = _clipboardWatch;
        if (_clipboardWatch)
        {
            ResetClipboardBaseline(); // 启动时已开启监听：程序开启前复制的内容不粘贴
        }

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
        // 图片对比层：位于图层之上、黑切之下。
        RootGrid.Children.Insert(1, _compareCanvas);
        // 图片信息面板层（最上层）。
        _infoPanel.Child = _infoPanelText;
        _infoCanvas.Children.Add(_infoPanel);
        RootGrid.Children.Add(_infoCanvas);
        _infoPanelEnabled = _settings.InfoPanel;
        // 内联输入面板（最上层，替代数字/滑块弹窗）。
        var inputButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        inputButtons.Children.Add(_inputOk);
        inputButtons.Children.Add(_inputCancel);
        var inputLayout = new StackPanel();
        inputLayout.Children.Add(_inputTitle);
        inputLayout.Children.Add(_inputSlider);
        inputLayout.Children.Add(_inputTextBox);
        inputLayout.Children.Add(_inputValue);
        inputLayout.Children.Add(inputButtons);
        _inputPanel.Child = inputLayout;
        // 面板直接挂到 RootGrid（Grid 对齐居中），置于最上层。
        RootGrid.Children.Add(_inputPanel);

        // 独立透明度条（图片内部偏下，绝对定位）与全局透明度面板（居中）。
        _hoverOpacityBarEnabled = _settings.HoverOpacityBar;
        _hoverOpacityBar.Child = _hoverOpacitySlider;
        _hoverOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_hoverSliderLayer is { } layer && _layers.Contains(layer))
            {
                ApplyLayerOpacity(layer, _hoverOpacitySlider.Value);
            }
        };
        RootGrid.Children.Add(_hoverOpacityBar);

        _globalOpacityTitle.Text = "不透明度（全局）";
        var globalLayout = new StackPanel();
        globalLayout.Children.Add(_globalOpacityTitle);
        // 默认只显示一个滚动条（控制当前图层），点“展开全部图层”才列出所有图层。
        var targetRow = new Grid();
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_globalOpacityTargetName, 0);
        Grid.SetColumn(_globalOpacitySlider, 1);
        Grid.SetColumn(_globalOpacityPercent, 2);
        targetRow.Children.Add(_globalOpacityTargetName);
        targetRow.Children.Add(_globalOpacitySlider);
        targetRow.Children.Add(_globalOpacityPercent);
        _globalOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_globalOpacityTarget is { } target)
            {
                ApplyLayerOpacity(target, _globalOpacitySlider.Value);
                _globalOpacityPercent.Text = $"{_globalOpacitySlider.Value:0}%";
            }
        };
        globalLayout.Children.Add(targetRow);
        _globalOpacityExpand.Click += (_, _) =>
        {
            if (_globalOpacityExpanded)
            {
                CollapseGlobalOpacityList();
            }
            else
            {
                ExpandGlobalOpacityList();
            }
        };
        globalLayout.Children.Add(_globalOpacityExpand);
        globalLayout.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _globalOpacityRows,
        });
        var globalDone = new Button
        {
            Content = "完成",
            Width = 72,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        globalDone.Click += (_, _) => CloseGlobalOpacityPanel();
        globalLayout.Children.Add(globalDone);
        _globalOpacityPanel.Child = globalLayout;
        RootGrid.Children.Add(_globalOpacityPanel);

        // 固定图片面板：全局锁定开关 + 展开后每个图层单独固定。
        _globalFixedTitle.Text = "固定图片（全局）";
        var fixedLayout = new StackPanel();
        fixedLayout.Children.Add(_globalFixedTitle);
        _globalFixedAll.Checked += (_, _) => ApplyGlobalFixed(true);
        _globalFixedAll.Unchecked += (_, _) => ApplyGlobalFixed(false);
        fixedLayout.Children.Add(_globalFixedAll);
        _globalFixedExpand.Click += (_, _) =>
        {
            if (_globalFixedExpanded)
            {
                CollapseGlobalFixedList();
            }
            else
            {
                ExpandGlobalFixedList();
            }
        };
        fixedLayout.Children.Add(_globalFixedExpand);
        fixedLayout.Children.Add(new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _globalFixedRows,
        });
        var fixedDone = new Button
        {
            Content = "完成",
            Width = 72,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        fixedDone.Click += (_, _) => CloseGlobalFixedPanel();
        fixedLayout.Children.Add(fixedDone);
        _globalFixedPanel.Child = fixedLayout;
        RootGrid.Children.Add(_globalFixedPanel);

        _inputSlider.ValueChanged += (_, _) =>
        {
            if (!_inputActive)
            {
                return;
            }

            var value = _inputSlider.Value;
            _inputValue.Text = string.Format(_inputFormat, value);
            _inputLive?.Invoke(value);
        };
        _inputOk.Click += (_, _) =>
        {
            if (!_inputActive)
            {
                return;
            }

            double value = _inputSliderMode ? _inputSlider.Value : ParseInputText();
            CloseInlineInput();
            _inputCommit?.Invoke(value);
        };
        _inputCancel.Click += (_, _) =>
        {
            if (!_inputActive)
            {
                return;
            }

            CloseInlineInput();
            _inputLive?.Invoke(_inputCurrent); // 取消恢复原值
        };
        // 会话恢复：有历史图层时不在构造中恢复（窗口未显示时 DPI/工作区不准确、
        // 软件渲染管线未就绪，会导致 GIF 卡帧、位置偏移），统一延迟到 Window_Loaded
        // 首次渲染前恢复；否则按传入路径加载。
        if (_settings.Layers.Count > 0)
        {
            IsImageLoaded = true; // 图层由 Window_Loaded 恢复
        }
        else if (imagePath is not null)
        {
            IsImageLoaded = LoadImage(imagePath);
            if (IsImageLoaded)
            {
                ApplySavedImagePosition();
            }
        }
        else
        {
            IsImageLoaded = false;
        }
    }

    #region 启动 / 持久化

    /// <summary>
    /// 窗口首次渲染前（Loaded 在布局完成后、首帧绘制前触发）执行启动收尾：
    /// 1) 恢复上次会话的图层——此时 DPI/工作区已准确、软件渲染管线已就绪，
    ///    GIF 逐帧定时器在显示后启动、图层位置直接按正确尺寸计算，避免启动卡帧与位置偏移；
    /// 2) 对齐全屏尺寸（窗口未显示时 DPI 可能不准，显示后重新对齐）。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = ScreenService.GetWorkArea(this);
        if (Math.Abs(Left - workArea.Left) > 0.5 || Math.Abs(Top - workArea.Top) > 0.5 ||
            Math.Abs(Width - workArea.Width) > 0.5 || Math.Abs(Height - workArea.Height) > 0.5)
        {
            ScreenService.MoveResize(this, workArea.Left, workArea.Top, workArea.Width, workArea.Height);
        }

        if (_layers.Count == 0 && _settings.Layers.Count > 0)
        {
            RestoreLayers();
        }

        UpdateTransform();
        // GIF 兜底启动（窗口已显示时 CreateLayer 已直接启动，这里保证会话恢复的也启动）。
        foreach (var layer in _layers)
        {
            if (layer.Gif is { IsAnimatedGif: true } gif && !gif.IsPaused)
            {
                gif.Start();
            }
        }

        // 启动强制刷新（先隐藏再显示 + 重新插入宿主，保持图层顺序）：
        // 软件渲染下启动时多个图层同时出现可能只渲染部分图层（GIF 卡帧不动），
        // 与"删除上一个图层后其他图层恢复正常"同机制——视觉树变化强制完整重绘。
        // 首帧前同步摆正一次，首帧渲染完成后（ApplicationIdle）兜底再刷一次。
        ForceRefreshLayers();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ForceRefreshLayers);
    }

    /// <summary>
    /// 强制刷新所有图层：先全部隐藏，从宿主重新插入（保持顺序），再按显隐状态恢复显示。
    /// </summary>
    private void ForceRefreshLayers()
    {
        foreach (var layer in _layers)
        {
            layer.Element.Visibility = Visibility.Collapsed;
        }

        foreach (var layer in _layers.ToList())
        {
            LayerHost.Children.Remove(layer.Canvas);
            LayerHost.Children.Add(layer.Canvas);
        }

        foreach (var layer in _layers)
        {
            layer.Element.Visibility = layer.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateTransform();
    }

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

    /// <summary>
    /// 会话恢复：把上次退出前保存的图层全部恢复（路径、缩放、位置、显隐），
    /// 解码走 ImageCache 缓存命中快速加载。
    /// </summary>
    private void RestoreLayers()
    {
        foreach (var saved in _settings.Layers)
        {
            try
            {
                var layer = AddLayer(saved.Path);
                layer.ZoomScale = saved.ZoomScale;
                layer.UserPan = new Point(saved.PanX, saved.PanY);
                layer.Visible = saved.Visible;
                layer.Element.Visibility = saved.Visible ? Visibility.Visible : Visibility.Collapsed;
                layer.OpacityPercent = Math.Clamp(saved.OpacityPercent, 0, 100);
                layer.Canvas.Opacity = layer.OpacityPercent / 100.0;
                layer.Fixed = saved.Fixed;
            }
            catch
            {
                // 单个图层恢复失败（文件被移动/删除）不影响其余图层。
            }
        }

        if (_layers.Count > 0)
        {
            UpdateTransform();
        }
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

        // 会话恢复：保存窗口内所有图层（路径 + 缩放 + 位置 + 显隐 + 透明度 + 固定），重启自动恢复。
        _settings.Layers = _layers.Select(l => new SavedLayer
        {
            Path = l.Path,
            ZoomScale = l.ZoomScale,
            PanX = l.UserPan.X,
            PanY = l.UserPan.Y,
            Visible = l.Visible,
            OpacityPercent = l.OpacityPercent,
            Fixed = l.Fixed,
        }).ToList();
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
    /// 窗口未显示时（会话恢复）GIF 不立即启动，由 Window_Loaded 在首次渲染前统一启动。
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
            if (IsVisible)
            {
                gif.Start(); // 运行中新增的 GIF：窗口已显示，直接播放
            }

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

    /// <summary>切换活动图层（各 GIF 播放器独立运行，无需重绑）；独立透明度条跟随活动图层。</summary>
    private void SetActiveLayer(ImageLayer layer)
    {
        if (_activeLayer == layer)
        {
            return;
        }

        _activeLayer = layer;
        ApplyAntiAliasing();
        UpdateTransform();
        UpdateHoverOpacitySlider();
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
        // 信息面板可见时跟随图片变换。
        if (_infoPanel.Visibility == Visibility.Visible && _infoPanelEnabled)
        {
            UpdateInfoPanel(layer);
        }

        // 独立透明度条可见时跟随图片位置（拖拽/缩放/回中心动画中保持贴图）。
        if (_hoverOpacityBar.Visibility == Visibility.Visible && _hoverSliderLayer is { } hoverLayer)
        {
            PositionHoverOpacitySlider(hoverLayer);
        }
        // 棋盘格纹样保持屏幕恒定大小：图片缩放时反向补偿画刷平铺尺寸，
        // 否则高分辨率图缩小后格子被压成细密纹理、放大后变成大色块。
        if (Backdrop.Background is DrawingBrush checker && checker.TileMode == TileMode.Tile)
        {
            checker.Viewport = new Rect(0, 0, CheckerCellSize / contentScale, CheckerCellSize / contentScale);
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (AnyPanelActive || _mosaicActive)
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
        if (_compareActive)
        {
            // 对比模式：同步缩放两张图。
            var factor = e.Delta > 0 ? 1.10 : 1.0 / 1.10;
            _compareScale.ScaleX = Math.Clamp(_compareScale.ScaleX * factor, 0.1, 20);
            _compareScale.ScaleY = _compareScale.ScaleX;
            UpdateCompareTransform();
            e.Handled = true;
            return;
        }

        // 缩放步进按图片分辨率自适应（小图放大更快、大图缩小更快），
        // 普通滚轮每格即一个步进。
        var step = ComputeZoomStep(e.Delta > 0);
        var zoomFactor = e.Delta > 0 ? step : 1.0 / step;
        ZoomAt(e.GetPosition(this), zoomFactor);
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
        if (AnyPanelActive)
        {
            // 面板内点击放行给面板控件（滑块/按钮），面板外拦截避免误操作图层。
            e.Handled = !IsPointInAnyPanel(e.GetPosition(this));
            return;
        }

        // 中键落在独立透明度条上时不执行删除操作。
        if (e.ChangedButton == MouseButton.Middle && IsPointOnHoverSlider(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        if (_compareActive)
        {
            // 对比模式：中键 = 同步平移。
            _comparePanning = true;
            _compareLastPoint = e.GetPosition(this);
            e.Handled = true;
            return;
        }

        // 中键：记录按下位置，松开时执行单击操作（中键不负责拖拽，平移由左键承担）。
        _middleDownPoint = e.GetPosition(this);
        e.Handled = true;
    }

    private void Window_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (AnyPanelActive)
        {
            return; // 输入面板打开时禁用主窗口操作（中键删除等）
        }

        if (_compareActive)
        {
            _comparePanning = false;
            _compareDraggingSplit = false;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            // 中键松开落在独立透明度条上时不触发删除。
            if (IsPointOnHoverSlider(e.GetPosition(this)))
            {
                return;
            }

            // 中键单击：Shift = 一键清除所有马赛克/图层（仅保留第一张），否则删除鼠标下图层。
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                ShiftMiddleClickRemoveAll(_middleDownPoint);
            }
            else
            {
                MiddleClickRemove(_middleDownPoint);
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

    /// <summary>鼠标移出窗口时隐藏图片信息面板（面板打开时不干预；独立透明度条常驻图片内不隐藏）。</summary>
    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        if (AnyPanelActive)
        {
            return;
        }

        HideInfoPanel();
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        if (AnyPanelActive)
        {
            return;
        }

        if (_compareActive)
        {
            if (_compareDraggingSplit)
            {
                _compareSplit = Math.Clamp(position.X / Math.Max(Width, 1), 0.02, 0.98);
                UpdateCompareTransform();
                return;
            }

            if (_comparePanning)
            {
                _comparePan.X += position.X - _compareLastPoint.X;
                _comparePan.Y += position.Y - _compareLastPoint.Y;
                _compareLastPoint = position;
                UpdateCompareTransform();
                return;
            }

            return;
        }

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

        // 悬停即选中：鼠标移到哪个图层上，滚轮缩放/双击就直接作用于该图层，
        // 无需先点击选中；移到空白处保持当前选中图层不变，独立透明度条自动隐藏。
        var hover = HitTestLayer(position);
        if (hover is not null)
        {
            SetActiveLayer(hover);
            UpdateInfoPanel(hover);
        }
        else
        {
            HideInfoPanel();
            HideHoverOpacitySlider();
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
        if (AnyPanelActive)
        {
            return;
        }

        // 按下在独立透明度条上时不开始图片拖拽（条在图片内，拖条 = 调透明度）。
        if (IsPointOnHoverSlider(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }

        if (_mosaicActive)
        {
            BeginMosaicSelection(e);
            e.Handled = true;
            return;
        }

        if (_compareActive)
        {
            var position = e.GetPosition(this);
            if (_compareSplitMode && Math.Abs(position.X - Width * _compareSplit) <= 8)
            {
                _compareDraggingSplit = true;
                _compareLastPoint = position;
            }
            else
            {
                _comparePanning = true;
                _compareLastPoint = position;
            }

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
        if (hit.Fixed)
        {
            return; // 固定（锁定位置）的图层不可拖动；缩放/透明度/双击仍可用
        }

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
        if (AnyPanelActive)
        {
            e.Handled = true;
            return;
        }

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
        menu.Items.Add(CreateCheckItem("剪贴板监听", _clipboardWatch, () =>
        {
            _clipboardWatch = !_clipboardWatch;
            _clipboardTimer.IsEnabled = _clipboardWatch;
            _settings.ClipboardWatch = _clipboardWatch;
            SaveSettings();
            if (_clipboardWatch)
            {
                ResetClipboardBaseline(); // 开启监听后新复制的内容才生效
            }
        }));
        menu.Items.Add(CreateInfoPanelItem());

        // 添加图片是主入口（顶层直接点击）；图层管理作为子菜单。
        menu.Items.Add(CreateItem("添加图片...", AddImageFile));
        menu.Items.Add(CreateLayerSubmenu());
        menu.Items.Add(CreateMosaicSubmenu());
        menu.Items.Add(CreateCompareSubmenu());

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
        // 透明度首级拆分：全局 = 面板（一个滚动条 + 展开按钮）；独立 = 图片内常驻条（勾选开关）。
        menu.Items.Add(CreateItem("不透明度（全局）", OpenGlobalOpacityPanel));
        menu.Items.Add(CreateCheckItem("不透明度（独立）", _hoverOpacityBarEnabled, () =>
        {
            _hoverOpacityBarEnabled = !_hoverOpacityBarEnabled;
            _settings.HoverOpacityBar = _hoverOpacityBarEnabled;
            SaveSettings();
            if (!_hoverOpacityBarEnabled)
            {
                HideHoverOpacitySlider();
            }
            else
            {
                UpdateHoverOpacitySlider();
            }
        }));
        // 固定图片：全局 = 面板（全局锁定开关 + 展开后单独调整）；独立 = 锁定鼠标所在图层（勾选）。
        menu.Items.Add(CreateItem("固定图片（全局）", OpenGlobalFixedPanel));
        menu.Items.Add(CreateCheckItem("固定图片（独立）", _activeLayer is { Fixed: true }, () =>
        {
            if (_activeLayer is { } layer)
            {
                layer.Fixed = !layer.Fixed;
                ScheduleSave();
            }
        }));
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
            () => ShowInlineInput(
                "缓存上限",
                _settings.CacheStrategy == "Size" ? 32 : 5,
                _settings.CacheStrategy == "Size" ? 8192 : 500,
                _settings.CacheLimit,
                _settings.CacheStrategy == "Size" ? "{0:0} MB" : "{0:0} 张",
                slider: false,
                value =>
                {
                    _settings.CacheLimit = (int)value;
                    ImageCache.Configure(_settings.CacheStrategy, _settings.CacheLimit);
                    SaveSettings();
                })));

        submenu.Items.Add(CreateItem("清除缓存", () =>
        {
            ImageCache.Clear();
            SaveSettings();
        }));
        return submenu;
    }

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
        Action? pickCustom)
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
            custom.IsChecked = false; // 自定义值不落在预设上，不显示勾选
            pickCustom?.Invoke();
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
            () => ShowInlineInput(
                "轮播间隔",
                1,
                60,
                _settings.SlideshowIntervalSeconds,
                "{0:0} 秒",
                slider: false,
                value =>
                {
                    _settings.SlideshowIntervalSeconds = (int)value;
                    if (_slideshowPlaying)
                    {
                        ResumeSlideshow();
                    }

                    SaveSettings();
                }));
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
            () => ShowInlineInput(
                "切换动画时间",
                50,
                3000,
                _settings.TransitionDurationMs,
                "{0:0} ms",
                slider: false,
                value =>
                {
                    _settings.TransitionDurationMs = (int)value;
                    SaveSettings();
                })));

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

    #region 剪贴板监听

    /// <summary>
    /// 记录当前剪贴板内容为基线（图像数据或文件列表）：开启监听/启动时已有的复制内容不再粘贴，
    /// 之后的新复制才生效。
    /// </summary>
    private void ResetClipboardBaseline()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>()
                    .Where(ImageFileService.IsSupportedImage)
                    .ToList();
                if (files.Count > 0)
                {
                    _lastClipboardFingerprint = string.Join(";", files.Select(BuildCacheKey));
                    return;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var current = Clipboard.GetImage();
                _lastClipboardFingerprint = current is null ? null : ClipboardFingerprint(current);
                return;
            }
        }
        catch
        {
            // 剪贴板被占用等瞬时异常忽略。
        }

        _lastClipboardFingerprint = null;
    }

    /// <summary>
    /// 轮询剪贴板：开启监听后，复制图片（图像数据或文件资源管理器中的图片文件）都会自动粘贴到屏幕。
    /// 去重：Clipboard.GetImage 每次返回新实例，引用比较无效，改用内容指纹（尺寸 + 采样像素）/ 文件指纹。
    /// </summary>
    private void ClipboardTimer_Tick(object? sender, EventArgs e)
    {
        if (!_clipboardWatch || _compareActive || _mosaicActive)
        {
            return;
        }

        try
        {
            // 文件资源管理器复制：剪贴板为文件列表，提取其中的图片文件。
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>()
                    .Where(ImageFileService.IsSupportedImage)
                    .ToList();
                if (files.Count > 0)
                {
                    var fingerprint = string.Join(";", files.Select(BuildCacheKey));
                    if (fingerprint != _lastClipboardFingerprint)
                    {
                        _lastClipboardFingerprint = fingerprint;
                        PasteClipboardFiles(files);
                    }
                }

                return;
            }

            if (!Clipboard.ContainsImage())
            {
                return;
            }

            var image = Clipboard.GetImage();
            if (image is null)
            {
                return;
            }

            var imageFingerprint = ClipboardFingerprint(image);
            if (imageFingerprint == _lastClipboardFingerprint)
            {
                return; // 剪贴板仍是同一张图，不重复粘贴
            }

            _lastClipboardFingerprint = imageFingerprint;
            AddClipboardLayer(image);
        }
        catch
        {
            // 剪贴板被占用等瞬时异常忽略。
        }
    }

    /// <summary>
    /// 批量粘贴剪贴板中的图片文件：每张添加为图层（缩放限制为屏幕 75%），
    /// 按“窗口重叠”样式依次向右下错位层叠。
    /// </summary>
    private void PasteClipboardFiles(List<string> paths)
    {
        const double overlapOffset = 32;
        for (int i = 0; i < paths.Count; i++)
        {
            try
            {
                var layer = AddLayer(paths[i]);
                ApplyClipboardScale(layer, layer.Source);
                layer.UserPan = new Point(i * overlapOffset, i * overlapOffset);
                UpdateTransform();
            }
            catch
            {
                // 单个文件加载失败不影响其余文件。
            }
        }
    }

    /// <summary>把剪贴板内容缩放限制为不超过屏幕宽/高的 75%（原始小图不放大）。</summary>
    private void ApplyClipboardScale(ImageLayer layer, BitmapSource image)
    {
        var workArea = ScreenService.GetWorkArea(this);
        double fit = Math.Min(workArea.Width / image.PixelWidth, workArea.Height / image.PixelHeight);
        layer.ZoomScale = Math.Min(1.0, fit * 0.75);
        layer.UserPan = new Point(0, 0);
        UpdateTransform();
    }

    /// <summary>把剪贴板图片添加为图层（缩放限制为不超过屏幕宽/高的 75%）。</summary>
    private void AddClipboardLayer(BitmapSource image)
    {
        var layer = AddLayer("剪贴板图片", image);
        ApplyClipboardScale(layer, image);
    }

    /// <summary>轻量内容指纹：尺寸 + 固定 16 个采样像素（同一图片每次解码结果一致，不同图片大概率不同）。</summary>
    private static string ClipboardFingerprint(BitmapSource image)
    {
        var sb = new StringBuilder();
        sb.Append(image.PixelWidth).Append('x').Append(image.PixelHeight);
        int w = Math.Max(image.PixelWidth, 1);
        int h = Math.Max(image.PixelHeight, 1);
        var pixel = new byte[4];
        for (int i = 0; i < 16; i++)
        {
            int x = (i * 7) % w;
            int y = (i * 11) % h;
            image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            sb.Append('-').Append(pixel[0]).Append(',').Append(pixel[1]).Append(',').Append(pixel[2]);
        }

        return sb.ToString();
    }

    #endregion

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
        HideHoverOpacitySlider(); // 绘制模式下隐藏独立透明度条
        _mosaicSelecting = false;
        _mosaicPreview = null;
        _mosaicBox = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 192, 235, 215)),
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
        UpdateHoverOpacitySlider(); // 恢复独立透明度条（跟随底图图层）
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
    /// 中键单击（位移小于阈值）：马赛克模式下擦除鼠标下的马赛克效果层；
    /// 普通模式下删除鼠标下的图片图层（单图时不删除，防止误触退出程序）。
    /// </summary>
    private void MiddleClickRemove(Point position)
    {
        if (_mosaicActive)
        {
            var mosaicHit = HitTestMosaicLayer(position);
            if (mosaicHit is not null)
            {
                RemoveMosaicLayer(mosaicHit);
            }

            return;
        }

        var layerHit = HitTestLayer(position);
        if (layerHit is not null && _layers.Count > 1)
        {
            RemoveLayer(layerHit);
        }
    }

    /// <summary>删除指定图层；删除活动图层后就近选中相邻图层；删光时退出程序。</summary>
    private void RemoveLayer(ImageLayer layer)
    {
        if (_hoverSliderLayer == layer)
        {
            _hoverSliderLayer = null;
        }

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
        UpdateHoverOpacitySlider(); // 删除后独立条跟随新的活动图层（无图层则隐藏）
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
            () => ShowInlineInput("马赛克大小", 2, 200, _mosaicBlockPx, "{0:0} px", slider: false, value => _mosaicBlockPx = value)));
        submenu.Items.Add(CreateValueSubmenu(
            "模糊像素",
            new[] { ("4 px", 4d), ("8 px", 8d), ("16 px", 16d), ("32 px", 32d) },
            _mosaicBlurPx,
            value => _mosaicBlurPx = value,
            () => ShowInlineInput("模糊像素", 1, 100, _mosaicBlurPx, "{0:0} px", slider: false, value => _mosaicBlurPx = value)));
        submenu.Items.Add(CreateValueSubmenu(
            "噪声像素",
            new[] { ("4 px", 4d), ("8 px", 8d), ("16 px", 16d), ("32 px", 32d) },
            _mosaicSmudgePx,
            value => _mosaicSmudgePx = value,
            () => ShowInlineInput("噪声像素", 1, 100, _mosaicSmudgePx, "{0:0} px", slider: false, value => _mosaicSmudgePx = value)));
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

    #region 图片对比模式

    /// <summary>选择对比图片并进入对比模式（清空图层，A=当前图，B=所选图）。</summary>
    private void ChooseCompareImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择对比图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|所有文件|*.*",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StartCompare(Path.GetFullPath(dialog.FileName));
    }

    /// <summary>
    /// 进入图片对比模式：A = 当前图层图片，B = 传入图片。
    /// 初始缩放保持 A 图进入前的缩放（不放大），支持左右并排 / 上下并列 / 滑动分割，GIF 正常播放。
    /// </summary>
    private void StartCompare(string pathB)
    {
        // 对比模式中重选：_activeLayer 已被清空，用记录的 A 图路径。
        var pathA = _activeLayer?.Path ?? _comparePathA;
        if (pathA is null || string.Equals(pathA, pathB, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            // 初始缩放保持 A 图进入前的缩放（重选时保持当前对比缩放）。
            double initScale = _compareActive
                ? _compareScale.ScaleX
                : Math.Max(0.01, _activeLayer?.ZoomScale ?? 1.0);
            _compareGifA?.Stop();
            _compareGifB?.Stop();
            var (sourceA, gifA) = LoadCompareSource(pathA, _compareA);
            var (sourceB, gifB) = LoadCompareSource(pathB, _compareB);
            foreach (var layer in _layers)
            {
                layer.Gif?.Stop();
                LayerHost.Children.Remove(layer.Canvas);
            }

            _layers.Clear();
            _activeLayer = null;
            _comparePathA = pathA;
            _compareGifA = gifA;
            _compareGifB = gifB;
            _compareA.Source = sourceA;
            _compareB.Source = sourceB;
            _compareCanvas.Children.Clear();
            _compareCanvas.Children.Add(_compareA);
            _compareCanvas.Children.Add(_compareB);
            _compareCanvas.Children.Add(_compareSplitLine);
            _compareScale.ScaleX = initScale;
            _compareScale.ScaleY = initScale;
            _comparePan.X = 0;
            _comparePan.Y = 0;
            _compareSplit = 0.5;
            _compareActive = true;
            _comparePanning = false;
            _compareDraggingSplit = false;
            HideHoverOpacitySlider(); // 对比模式无图层，隐藏独立透明度条
            UpdateCompareTransform();
            gifA?.Start();
            gifB?.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"无法加载对比图片：\n{ex.Message}",
                "图片对比",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>加载对比图源：GIF 用独立播放器（对比中正常播放），静态图走解码缓存。</summary>
    private (BitmapSource Source, GifAnimation? Gif) LoadCompareSource(string path, Image target)
    {
        if (string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            var gif = new GifAnimation(target);
            var source = gif.Load(path)
                ?? throw new InvalidOperationException("GIF 解码失败。");
            return (source, gif);
        }

        return (LoadStaticImage(path), null);
    }

    /// <summary>退出对比模式：恢复 A 图为普通图层。</summary>
    private void ExitCompare()
    {
        _compareActive = false;
        _compareCanvas.Children.Clear();
        _compareGifA?.Stop();
        _compareGifB?.Stop();
        _compareGifA = null;
        _compareGifB = null;
        if (_comparePathA is not null)
        {
            LoadImage(_comparePathA);
        }
    }

    /// <summary>
    /// 对比布局计算：左右并排 / 上下并列（两张图贴在一起、整体居中）或滑动分割
    /// （同一视图叠加 + 分割线，分割线只覆盖两图重叠的竖直范围）。
    /// </summary>
    private void UpdateCompareTransform()
    {
        if (!_compareActive || _compareA.Source is not BitmapSource a || _compareB.Source is not BitmapSource b)
        {
            return;
        }

        double w = Math.Max(1, Width);
        double h = Math.Max(1, Height);
        double scale = _compareScale.ScaleX;
        double panX = _comparePan.X;
        double panY = _comparePan.Y;
        // 适应大小：左右并排等高、上下并列等宽（取两者较大值按比例缩放），分割模式不适用。
        double dwA;
        double dhA;
        double dwB;
        double dhB;
        if (_compareFitSize && !_compareSplitMode)
        {
            if (_compareTopBottom)
            {
                double targetW = Math.Max(a.PixelWidth, b.PixelWidth) * scale;
                double sa = targetW / a.PixelWidth;
                double sb = targetW / b.PixelWidth;
                dwA = a.PixelWidth * sa;
                dhA = a.PixelHeight * sa;
                dwB = b.PixelWidth * sb;
                dhB = b.PixelHeight * sb;
            }
            else
            {
                double targetH = Math.Max(a.PixelHeight, b.PixelHeight) * scale;
                double sa = targetH / a.PixelHeight;
                double sb = targetH / b.PixelHeight;
                dwA = a.PixelWidth * sa;
                dhA = a.PixelHeight * sa;
                dwB = b.PixelWidth * sb;
                dhB = b.PixelHeight * sb;
            }
        }
        else
        {
            dwA = a.PixelWidth * scale;
            dhA = a.PixelHeight * scale;
            dwB = b.PixelWidth * scale;
            dhB = b.PixelHeight * scale;
        }

        if (_compareSplitMode)
        {
            // 滑动分割：两图同一视图（各自居中 + 同缩放平移），分割线左侧 A、右侧 B。
            double leftA = (w - dwA) / 2.0 + panX;
            double topA = (h - dhA) / 2.0 + panY;
            double leftB = (w - dwB) / 2.0 + panX;
            double topB = (h - dhB) / 2.0 + panY;
            SetImage(_compareA, leftA, topA, dwA, dhA);
            SetImage(_compareB, leftB, topB, dwB, dhB);
            double splitX = w * _compareSplit;
            double clipA = Math.Clamp(splitX - leftA, 0, dwA);
            _compareA.Clip = new RectangleGeometry(new Rect(0, 0, clipA, dhA));
            double clipBLeft = Math.Clamp(splitX - leftB, 0, dwB);
            _compareB.Clip = new RectangleGeometry(new Rect(clipBLeft, 0, Math.Max(0, dwB - clipBLeft), dhB));
            // 分割线只覆盖两图重叠的竖直范围，不超出图片。
            double top = Math.Max(topA, topB);
            double bottom = Math.Min(topA + dhA, topB + dhB);
            Canvas.SetLeft(_compareSplitLine, splitX - 1);
            Canvas.SetTop(_compareSplitLine, top);
            _compareSplitLine.Height = Math.Max(0, bottom - top);
            _compareSplitLine.Visibility = bottom > top ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            // 左右并排 / 上下并列：两张图贴在一起（8px 间距），整体居中于窗口。
            double gap = CompareGap;
            if (_compareTopBottom)
            {
                double totalHeight = dhA + gap + dhB;
                double top0 = (h - totalHeight) / 2.0 + panY;
                double left0 = (w - Math.Max(dwA, dwB)) / 2.0 + panX;
                SetImage(_compareA, left0, top0, dwA, dhA);
                SetImage(_compareB, left0, top0 + dhA + gap, dwB, dhB);
            }
            else
            {
                double totalWidth = dwA + gap + dwB;
                double left0 = (w - totalWidth) / 2.0 + panX;
                double top0 = (h - Math.Max(dhA, dhB)) / 2.0 + panY;
                SetImage(_compareA, left0, top0, dwA, dhA);
                SetImage(_compareB, left0 + dwA + gap, top0, dwB, dhB);
            }

            _compareA.Clip = null;
            _compareB.Clip = null;
            _compareSplitLine.Visibility = Visibility.Collapsed;
        }
    }

    private static void SetImage(Image image, double left, double top, double width, double height)
    {
        image.Width = width;
        image.Height = height;
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, top);
    }

    /// <summary>图片对比子菜单：选择对比图、布局、退出。</summary>
    private MenuItem CreateCompareSubmenu()
    {
        var submenu = new MenuItem { Header = "图片对比" };
        submenu.Items.Add(CreateItem(_compareActive ? "重新选择对比图片..." : "选择对比图片...", ChooseCompareImage));
        submenu.Items.Add(CreateRadioSubmenu(
            "布局",
            new[]
            {
                ("左右并排", "SideBySide"),
                ("上下并列", "TopBottom"),
                ("滑动分割", "Split"),
            },
            _compareSplitMode ? "Split" : _compareTopBottom ? "TopBottom" : "SideBySide",
            value =>
            {
                _compareSplitMode = value == "Split";
                _compareTopBottom = value == "TopBottom";
                UpdateCompareTransform();
            }));
        submenu.Items.Add(CreateCheckItem("适应大小", _compareFitSize, () =>
        {
            _compareFitSize = !_compareFitSize;
            UpdateCompareTransform();
        }));
        submenu.Items.Add(CreateItem("退出对比", ExitCompare));
        return submenu;
    }

    #endregion

    /// <summary>
    /// 更新图片信息面板：显示在图层显示区域右侧（溢出窗口时移到左侧）。
    /// 仅在开关开启且面板可见时更新（悬停时显示、随图片变换跟随）。
    /// </summary>
    private void UpdateInfoPanel(ImageLayer layer)
    {
        if (!_infoPanelEnabled)
        {
            HideInfoPanel();
            return;
        }

        var rect = GetLayerRect(layer);
        const double panelWidth = 240;
        double panelX = rect.Right + 8;
        double panelY = rect.Top;
        if (panelX + panelWidth > Width)
        {
            panelX = rect.Left - panelWidth - 8;
        }

        _infoPanelText.Text = BuildLayerInfo(layer);
        Canvas.SetLeft(_infoPanel, Math.Max(2, panelX));
        Canvas.SetTop(_infoPanel, Math.Max(2, panelY));
        _infoPanel.Visibility = Visibility.Visible;
    }

    private void HideInfoPanel()
    {
        _infoPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>面板内容：文件名、像素尺寸、文件大小、格式。</summary>
    private static string BuildLayerInfo(ImageLayer layer)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileName(layer.Path));
        sb.AppendLine($"{layer.PixelWidth} × {layer.PixelHeight} px");
        try
        {
            sb.AppendLine(FormatFileSize(new FileInfo(layer.Path).Length));
        }
        catch
        {
            // 文件信息不可用时省略大小。
        }

        var extension = Path.GetExtension(layer.Path).TrimStart('.').ToUpperInvariant();
        sb.Append(string.IsNullOrEmpty(extension) ? "图片" : extension);
        return sb.ToString();
    }

    private static string FormatFileSize(long bytes)
        => bytes >= 1024 * 1024
            ? $"{bytes / 1024.0 / 1024.0:0.0} MB"
            : $"{bytes / 1024.0:0.0} KB";

    /// <summary>图片信息面板子菜单开关（默认关闭）。</summary>
    private MenuItem CreateInfoPanelItem()
        => CreateCheckItem("图片信息", _infoPanelEnabled, () =>
        {
            _infoPanelEnabled = !_infoPanelEnabled;
            _settings.InfoPanel = _infoPanelEnabled;
            SaveSettings();
            if (!_infoPanelEnabled)
            {
                HideInfoPanel();
            }
        });

    /// <summary>
    /// 打开内联输入面板（替代数字/滑块弹窗，UI 与菜单统一）：
    /// 滑块模式实时预览（onLive），数字模式文本框输入；确定提交，取消恢复原值。
    /// </summary>
    private void ShowInlineInput(
        string title,
        double min,
        double max,
        double current,
        string format,
        bool slider,
        Action<double> onCommit,
        Action<double>? onLive = null)
    {
        _inputActive = true;
        _inputSliderMode = slider;
        _inputCommit = onCommit;
        _inputLive = onLive;
        _inputFormat = format;
        _inputCurrent = current;
        _inputTitle.Text = title;
        _inputSlider.Minimum = min;
        _inputSlider.Maximum = max;
        _inputSlider.Value = current;
        _inputSlider.Visibility = slider ? Visibility.Visible : Visibility.Collapsed;
        _inputTextBox.Visibility = slider ? Visibility.Collapsed : Visibility.Visible;
        _inputTextBox.Text = current.ToString("0.##");
        _inputValue.Text = string.Format(format, current);
        _inputPanel.Visibility = Visibility.Visible;
        if (!slider)
        {
            _inputTextBox.Focus();
            _inputTextBox.SelectAll();
        }
    }

    private void CloseInlineInput()
    {
        _inputActive = false;
        _inputPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>判断窗口坐标点是否在输入面板内（面板打开时用于区分面板内外点击）。</summary>
    private bool IsPointInInputPanel(Point windowPoint)
    {
        var topLeft = _inputPanel.TranslatePoint(new Point(0, 0), this);
        return new Rect(topLeft, new Size(_inputPanel.ActualWidth, _inputPanel.ActualHeight)).Contains(windowPoint);
    }

    /// <summary>是否有任何窗口内面板打开（内联输入面板 / 全局透明度面板 / 固定图片面板）。</summary>
    private bool AnyPanelActive => _inputActive || _globalOpacityActive || _globalFixedActive;

    /// <summary>判断点是否落在任一打开的面板内。</summary>
    private bool IsPointInAnyPanel(Point windowPoint)
        => IsPointInInputPanel(windowPoint) || IsPointInGlobalOpacityPanel(windowPoint) || IsPointInGlobalFixedPanel(windowPoint);

    /// <summary>判断窗口坐标点是否在全局透明度面板内。</summary>
    private bool IsPointInGlobalOpacityPanel(Point windowPoint)
    {
        var topLeft = _globalOpacityPanel.TranslatePoint(new Point(0, 0), this);
        return new Rect(topLeft, new Size(_globalOpacityPanel.ActualWidth, _globalOpacityPanel.ActualHeight)).Contains(windowPoint);
    }

    /// <summary>判断窗口坐标点是否在独立透明度条上（条未显示时返回 false）。</summary>
    private bool IsPointOnHoverSlider(Point windowPoint)
    {
        if (_hoverOpacityBar.Visibility != Visibility.Visible)
        {
            return false;
        }

        // 条以 Margin 绝对定位在 RootGrid 中，矩形即窗口坐标（容器尺寸固定）。
        return new Rect(
            _hoverOpacityBar.Margin.Left,
            _hoverOpacityBar.Margin.Top,
            HoverBarWidth,
            HoverBarHeight).Contains(windowPoint);
    }

    /// <summary>
    /// 设置图层透明度（0–100）并应用，变化后调度保存。
    /// 透明度拉到 0 时视为图层被隐藏（不可见且不参与命中，避免"找不回来"），
    /// 重新打开需从「图层」菜单勾选（会恢复到 100%）。
    /// </summary>
    private void ApplyLayerOpacity(ImageLayer layer, double percent)
    {
        layer.OpacityPercent = Math.Clamp(percent, 0, 100);
        layer.Canvas.Opacity = layer.OpacityPercent / 100.0;
        if (layer.OpacityPercent <= 0)
        {
            layer.Visible = false;
            layer.Element.Visibility = Visibility.Collapsed;
        }

        ScheduleSave();
    }

    /// <summary>
    /// 更新独立透明度条：显示在活动图层图片内部偏下位置（不随鼠标移出消失），
    /// 只调整该图层。条自身尺寸固定，不受图片缩放影响。
    /// 图层隐藏（透明度 0 或勾选关闭）时不显示条。
    /// </summary>
    private void UpdateHoverOpacitySlider()
    {
        if (!_hoverOpacityBarEnabled || AnyPanelActive || _compareActive || _mosaicActive
            || _activeLayer is not { } layer || !layer.Visible)
        {
            HideHoverOpacitySlider();
            return;
        }

        _hoverSliderLayer = layer;
        _hoverOpacitySlider.Value = layer.OpacityPercent; // ValueChanged 应用同值，幂等
        PositionHoverOpacitySlider(layer);
        _hoverOpacityBar.Visibility = Visibility.Visible;
    }

    /// <summary>把独立透明度条定位到图层图片内部偏下（距图片底部 16px，居中）；图片过矮时贴图片顶部。</summary>
    private void PositionHoverOpacitySlider(ImageLayer layer)
    {
        var rect = GetLayerRect(layer); // 屏幕坐标
        double x = rect.Left - Left + rect.Width / 2 - HoverBarWidth / 2;
        double yBottom = rect.Bottom - Top - HoverBarHeight - 16; // 偏下：距图片底部 16px
        double yTop = rect.Top - Top + 4; // 图片过矮时的兜底位置
        double y = Math.Max(yBottom, yTop);
        if (y < 0)
        {
            y = 0;
        }

        if (y + HoverBarHeight > Height)
        {
            y = Height - HoverBarHeight - 4;
        }

        x = Math.Clamp(x, 4, Width - HoverBarWidth - 4);
        _hoverOpacityBar.Margin = new Thickness(x, y, 0, 0);
    }

    private void HideHoverOpacitySlider()
    {
        _hoverSliderLayer = null;
        _hoverOpacityBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>打开全局透明度面板：默认只显示一个滚动条（控制当前图层），展开按钮再列出全部。</summary>
    private void OpenGlobalOpacityPanel()
    {
        if (_activeLayer is not { } layer)
        {
            return;
        }

        HideHoverOpacitySlider();
        _globalOpacityTarget = layer;
        _globalOpacityTargetName.Text = Path.GetFileName(layer.Path);
        _globalOpacitySlider.Value = layer.OpacityPercent; // ValueChanged 更新百分比并应用同值
        CollapseGlobalOpacityList();
        _globalOpacityActive = true;
        _globalOpacityPanel.Visibility = Visibility.Visible;
    }

    /// <summary>展开全部图层的透明度条（面板内可滚动查看，999 张也不会撑爆面板）。</summary>
    private void ExpandGlobalOpacityList()
    {
        if (_globalOpacityRows.Children.Count == 0)
        {
            foreach (var layer in _layers)
            {
                _globalOpacityRows.Children.Add(BuildGlobalOpacityRow(layer));
            }
        }

        _globalOpacityRows.Visibility = Visibility.Visible;
        _globalOpacityExpand.Content = "收起图层列表";
        _globalOpacityExpanded = true;
    }

    private void CollapseGlobalOpacityList()
    {
        _globalOpacityRows.Visibility = Visibility.Collapsed;
        _globalOpacityExpand.Content = "展开全部图层";
        _globalOpacityExpanded = false;
    }

    /// <summary>构建一行：图层文件名 + 透明度条 + 当前百分比（拖动实时生效）。</summary>
    private Grid BuildGlobalOpacityRow(ImageLayer layer)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = Path.GetFileName(layer.Path),
            Foreground = new SolidColorBrush(Color.FromArgb(230, 224, 224, 224)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(name, 0);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = layer.OpacityPercent,
        };
        Grid.SetColumn(slider, 1);

        var percent = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(220, 192, 235, 215)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 42,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(10, 0, 0, 0),
            Text = $"{layer.OpacityPercent:0}%",
        };
        Grid.SetColumn(percent, 2);

        slider.ValueChanged += (_, _) =>
        {
            ApplyLayerOpacity(layer, slider.Value);
            percent.Text = $"{slider.Value:0}%";
        };

        row.Children.Add(name);
        row.Children.Add(slider);
        row.Children.Add(percent);
        return row;
    }

    /// <summary>关闭全局透明度面板（拖动实时生效，无需确认）；恢复独立透明度条显示。</summary>
    private void CloseGlobalOpacityPanel()
    {
        _globalOpacityActive = false;
        _globalOpacityPanel.Visibility = Visibility.Collapsed;
        UpdateHoverOpacitySlider();
    }

    /// <summary>判断窗口坐标点是否在固定图片面板内。</summary>
    private bool IsPointInGlobalFixedPanel(Point windowPoint)
    {
        var topLeft = _globalFixedPanel.TranslatePoint(new Point(0, 0), this);
        return new Rect(topLeft, new Size(_globalFixedPanel.ActualWidth, _globalFixedPanel.ActualHeight)).Contains(windowPoint);
    }

    /// <summary>打开固定图片面板：全局锁定开关 + 展开后每个图层单独固定。</summary>
    private void OpenGlobalFixedPanel()
    {
        if (_layers.Count == 0)
        {
            return;
        }

        _globalFixedAll.IsChecked = _layers.All(l => l.Fixed);
        CollapseGlobalFixedList();
        _globalFixedActive = true;
        _globalFixedPanel.Visibility = Visibility.Visible;
    }

    /// <summary>全局锁定开关：一键固定/解锁所有图层（同时同步每行的勾选状态）。</summary>
    private void ApplyGlobalFixed(bool fixedState)
    {
        foreach (var layer in _layers)
        {
            layer.Fixed = fixedState;
        }

        foreach (var child in _globalFixedRows.Children)
        {
            if (child is Grid row &&
                row.Children.OfType<CheckBox>().FirstOrDefault() is { } check)
            {
                check.IsChecked = fixedState; // 触发行事件设置同值，幂等
            }
        }

        ScheduleSave();
    }

    /// <summary>展开全部图层的固定开关列表（面板内可滚动）。</summary>
    private void ExpandGlobalFixedList()
    {
        if (_globalFixedRows.Children.Count == 0)
        {
            foreach (var layer in _layers)
            {
                _globalFixedRows.Children.Add(BuildGlobalFixedRow(layer));
            }
        }

        _globalFixedRows.Visibility = Visibility.Visible;
        _globalFixedExpand.Content = "收起图层列表";
        _globalFixedExpanded = true;
    }

    private void CollapseGlobalFixedList()
    {
        _globalFixedRows.Visibility = Visibility.Collapsed;
        _globalFixedExpand.Content = "展开全部图层";
        _globalFixedExpanded = false;
    }

    /// <summary>构建一行：图层文件名 + 固定勾选框（勾选 = 锁定该图层位置）。</summary>
    private Grid BuildGlobalFixedRow(ImageLayer layer)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = Path.GetFileName(layer.Path),
            Foreground = new SolidColorBrush(Color.FromArgb(230, 224, 224, 224)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 10, 0),
        };
        Grid.SetColumn(name, 0);

        var check = new CheckBox
        {
            Content = "固定",
            IsChecked = layer.Fixed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(check, 1);
        check.Checked += (_, _) =>
        {
            layer.Fixed = true;
            ScheduleSave();
        };
        check.Unchecked += (_, _) =>
        {
            layer.Fixed = false;
            ScheduleSave();
        };

        row.Children.Add(name);
        row.Children.Add(check);
        return row;
    }

    /// <summary>关闭固定图片面板。</summary>
    private void CloseGlobalFixedPanel()
    {
        _globalFixedActive = false;
        _globalFixedPanel.Visibility = Visibility.Collapsed;
    }

    private double ParseInputText()
        => double.TryParse(_inputTextBox.Text, out var value) ? value : _inputCurrent;

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
            submenu.Items.Add(CreateItem(_compareActive ? "对比模式" : "暂无图片", () => { }));
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
                if (layer.Visible && layer.OpacityPercent <= 0)
                {
                    // 透明度被拉到 0 而隐藏的图层：重新打开时恢复到 100%，否则仍不可见。
                    layer.OpacityPercent = 100;
                    layer.Canvas.Opacity = 1.0;
                }

                UpdateHoverOpacitySlider();
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
        _clipboardTimer.Stop();
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
