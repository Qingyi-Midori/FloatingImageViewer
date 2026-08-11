using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatingImageViewer;
using FloatingImageViewer.Models;
using FloatingImageViewer.Services;
using FloatingImageViewer.Views;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("YELEE_IMGVIEWER_DATA_DIR", tempDir);

        // 加载应用的现代菜单样式，让测试中的菜单使用与真实应用相同的模板
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/FloatingImageViewer;component/Themes/ModernMenu.xaml", UriKind.Relative),
        });

        TestDefaults();
        TestRoundTrip();
        TestCorruptFallback();
        TestClamping();
        TestNaturalSort();
        TestMosaicRenderer();
        TestMultiLayer();
        TestUiMenuAndZoom();
        TestGifAnimation();
        TestMoveResizeSync();
        TestSliderDialog();
        TestNumberDialog();
        TestNoClipping();
        TestZoomCap();
        TestComputeDownsample();
        TestLargeImageRenders();
        TestCache();

        Console.WriteLine("FloatingImageViewer.SmokeTests: 全部通过");
        return 0;
    }

    private static void TestDefaults()
    {
        var defaults = SettingsService.Load();
        Assert(defaults.Width == 640, "默认宽度");
        Assert(defaults.AntiAliasing == "Off" && defaults.SsaaLevel == 4, "默认抗锯齿关闭");
        Assert(defaults.SlideshowLoop, "默认循环");
    }

    private static void TestRoundTrip()
    {
        var settings = new ViewerSettings
        {
    ImageLeft = 12.5,
    ImageTop = 34.25,
            Width = 800,
            Height = 600,
    Topmost = false,
    ZoomMode = "Stretch",
    BackgroundMode = "Checkerboard",
    OpacityPercent = 55,
    SnapEnabled = true,
    ScreenEdgeSnap = true,
    AntiAliasing = "SSAA",
    SsaaLevel = 8,
    MsaaLevel = 2,
    TxaaQuality = "High",
    SlideshowLoop = false,
    SlideshowIntervalSeconds = 7,
    SlideTransition = "Slide",
    TransitionDurationMs = 700,
    SlideDirection = "Right",
    CacheStrategy = "Size",
    CacheLimit = 512,
};
SettingsService.Save(settings);
var loaded = SettingsService.Load();
Assert(loaded.ImageLeft == 12.5 && loaded.ImageTop == 34.25, "图片位置往返");
        Assert(loaded.Width == 800 && loaded.Height == 600, "尺寸往返");
        Assert(!loaded.Topmost, "置顶往返");
        Assert(loaded.ZoomMode == "Stretch", "缩放往返");
        Assert(loaded.BackgroundMode == "Checkerboard", "背景往返");
        Assert(loaded.OpacityPercent == 55, "透明度往返");
Assert(loaded.SnapEnabled, "吸附开关往返");
Assert(loaded.ScreenEdgeSnap, "屏幕边缘吸附往返");
Assert(loaded.AntiAliasing == "SSAA" && loaded.SsaaLevel == 8, "抗锯齿模式与 SSAA 往返");
Assert(loaded.MsaaLevel == 2 && loaded.TxaaQuality == "High", "MSAA/TXAA 参数往返");
Assert(!loaded.SlideshowLoop && loaded.SlideshowIntervalSeconds == 7, "轮播往返");
Assert(loaded.SlideTransition == "Slide" && loaded.TransitionDurationMs == 700, "切换动画往返");
Assert(loaded.SlideDirection == "Right", "切入方向往返");
Assert(loaded.CacheStrategy == "Size" && loaded.CacheLimit == 512, "缓存设置往返");
    }

    private static void TestCorruptFallback()
    {
        File.WriteAllText(SettingsService.SettingsPath, "{ not valid json !!!");
        var recovered = SettingsService.Load();
        Assert(recovered.Width == 640 && recovered.SlideshowLoop, "损坏回退");
    }

    private static void TestClamping()
    {
File.WriteAllText(SettingsService.SettingsPath, """{"OpacityPercent":500,"SlideshowIntervalSeconds":0,"TransitionDurationMs":99999,"SlideTransition":"X","SlideDirection":"Y","AntiAliasing":"FXAA","SsaaLevel":3,"TxaaQuality":"Ultra"}""");
var clamped = SettingsService.Load();
Assert(clamped.OpacityPercent == 100 && clamped.SlideshowIntervalSeconds == 1, "钳制");
Assert(clamped.AntiAliasing == "Off" && clamped.SsaaLevel == 4 && clamped.TxaaQuality == "Medium", "抗锯齿钳制");
Assert(clamped.TransitionDurationMs == 3000, "动画时长钳制");
Assert(clamped.SlideTransition == "Fade" && clamped.SlideDirection == "Left", "非法动画枚举回退");
    }

    private static void TestNaturalSort()
    {
        var sorted = new[] { "a2.jpg", "a10.jpg", "a1.jpg" }
            .OrderBy(x => x, Comparer<string>.Create(ImageFileService.NaturalCompare))
            .ToArray();
        Assert(sorted[0] == "a1.jpg" && sorted[1] == "a2.jpg" && sorted[2] == "a10.jpg", "自然排序");
    }

    /// <summary>
    /// 马赛克渲染器：纯色底图区域分别应用马赛克/模糊/涂抹/纯色，
    /// 验证处理后的区域位图尺寸与像素确实发生变化（非原图）。
    /// </summary>
    private static void TestMosaicRenderer()
    {
        // 红蓝各半的 64x64 底图
        var pixels = new byte[64 * 64 * 4];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int i = (y * 64 + x) * 4;
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = x < 32 ? (byte)255 : (byte)0;
                pixels[i + 3] = 255;
            }
        }

        var baseImage = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var region = new Int32Rect(0, 0, 64, 64);

        var mosaic = MosaicRenderer.Apply(baseImage, region, MosaicRenderer.Style.Mosaic, 8, Colors.Black);
        Assert(mosaic.PixelWidth == 64 && mosaic.PixelHeight == 64, "马赛克区域尺寸");
        var mosaicPixels = new byte[64 * 64 * 4];
        mosaic.CopyPixels(mosaicPixels, 64 * 4, 0);
        // 8px 块平均后，左侧红块中心应仍是红色（块内平均），右侧应仍是蓝色
        Assert(mosaicPixels[(32 * 64 + 16) * 4 + 2] > 200, "马赛克左半保持红色");
        Assert(mosaicPixels[(32 * 64 + 48) * 4 + 2] < 50, "马赛克右半保持蓝色");

        var blur = MosaicRenderer.Apply(baseImage, region, MosaicRenderer.Style.Blur, 8, Colors.Black);
        var blurPixels = new byte[64 * 64 * 4];
        blur.CopyPixels(blurPixels, 64 * 4, 0);
        // 模糊后红蓝边界处出现混合色（非纯红/纯蓝）
        int edge = (32 * 64 + 32) * 4;
        bool mixed = blurPixels[edge + 2] > 30 && blurPixels[edge + 2] < 225;
        Assert(mixed, "模糊边界产生混合色");

        var solid = MosaicRenderer.Apply(baseImage, region, MosaicRenderer.Style.Solid, 0, Colors.Lime);
        var solidPixels = new byte[64 * 64 * 4];
        solid.CopyPixels(solidPixels, 64 * 4, 0);
        Assert(solidPixels[0] == 0 && solidPixels[1] == 255 && solidPixels[2] == 0, "纯色填充为绿色");

        var smudge = MosaicRenderer.Apply(baseImage, region, MosaicRenderer.Style.Smudge, 8, Colors.Black);
        var smudgePixels = new byte[64 * 64 * 4];
        smudge.CopyPixels(smudgePixels, 64 * 4, 0);
        // 涂抹后左半不再全部纯红（随机偏移引入右半蓝色像素）
        bool disturbed = false;
        for (int y = 0; y < 64 && !disturbed; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                int i = (y * 64 + x) * 4;
                if (smudgePixels[i + 2] < 200)
                {
                    disturbed = true;
                    break;
                }
            }
        }

        Assert(disturbed, "涂抹破坏区域细节");
    }

    private static void TestMultiLayer()
    {
        ResetSettings();
        var png1 = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        var png2 = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        SaveSolidPng(png1, 100, 80, Colors.Red, 96);
        SaveSolidPng(png2, 60, 40, Colors.Blue, 96);
        var window = new MainWindow(png1);
        try
        {
            var addLayer = typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .First(m => m.Name == "AddLayer" && m.GetParameters().Length == 1);
            var layer2 = (ImageLayer)addLayer.Invoke(window, new object[] { png2 })!;

            var layersField = typeof(MainWindow).GetField("_layers", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var activeField = typeof(MainWindow).GetField("_activeLayer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var layers = (List<ImageLayer>)layersField.GetValue(window)!;
            Assert(layers.Count == 2, "图层数量");
            Assert(ReferenceEquals(activeField.GetValue(window), layer2), "新图层为活动图层");
            Assert(layer2.Element.Visibility == Visibility.Visible, "新图层可见");

            // 删除活动图层后选中相邻层
            var remove = typeof(MainWindow).GetMethod("RemoveActiveLayer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            remove.Invoke(window, null);
            Assert(layers.Count == 1, "删除后图层数量");
            Assert(ReferenceEquals(activeField.GetValue(window), layers[0]), "删除后选中相邻层");
        }
        finally
        {
            window.Close();
            File.Delete(png1);
            File.Delete(png2);
        }
    }

    private static void TestUiMenuAndZoom()
    {
        ResetSettings();
        var png = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        SaveTestPng(png);

        var window = new MainWindow(png);
        try
        {
            Assert(window.IsImageLoaded, "UI 图片加载");
            var workArea = SystemParameters.WorkArea;
            Assert(
                Math.Abs(window.Width - workArea.Width) < 1e-6 &&
                Math.Abs(window.Height - workArea.Height) < 1e-6,
                "窗口为工作区全屏");
            var layerField = typeof(MainWindow).GetField("_activeLayer", BindingFlags.Instance | BindingFlags.NonPublic);
            var activeLayer = (ImageLayer)layerField!.GetValue(window)!;
            Assert(
                RenderOptions.GetBitmapScalingMode(activeLayer.Element) == BitmapScalingMode.NearestNeighbor,
                "小图使用邻近插值");

            var buildMenu = typeof(MainWindow).GetMethod("BuildContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var menu = (ContextMenu)buildMenu.Invoke(window, null)!;
            Assert(menu is not null && menu.Items.Count == 18, "菜单项数量");
            var headers = menu!.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            foreach (var expected in new[] { "窗口置顶", "添加图片...", "图层", "框选马赛克", "图片对比", "缩放模式", "背景模式", "无用小功能", "不透明度", "图片缓存", "幻灯片放映", "暂停GIF动画", "更换图片", "关闭图片", "重置窗口", "退出程序" })
            {
                Assert(headers.Contains(expected), "菜单包含: " + expected);
            }

            // “图层”子菜单：单图时不显示上移/下移（层序操作仅多图时出现）
            var layerItem = menu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "图层");
            var layerHeaders = layerItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            Assert(layerHeaders.Contains("删除图层"), "图层子菜单含删除");
            Assert(!layerHeaders.Contains("上移一层"), "单图时不显示上移一层");

            // 无用小功能子菜单结构：抗锯齿（模式 + 参数预设）
            var gimmickItem = menu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "无用小功能");
            var gimmickHeaders = gimmickItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            Assert(gimmickHeaders.Contains("抗锯齿"), "无用小功能子菜单包含抗锯齿");

            var aaItem = gimmickItem.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "抗锯齿");
            var aaHeaders = aaItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            foreach (var expected in new[] { "模式", "SSAA 倍率", "MSAA 采样", "TXAA 质量" })
            {
                Assert(aaHeaders.Contains(expected), "抗锯齿子菜单包含: " + expected);
            }

            // GIF 暂停项在静态图片下应禁用
            var gifItem = menu.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "暂停GIF动画");
            Assert(!gifItem.IsEnabled, "静态图片禁用 GIF 暂停");

            // 未进入幻灯片时，“幻灯片放映”也始终是子菜单，可配置设置
            var slideshowItem = menu.Items.OfType<MenuItem>()
                .First(m => m.Header?.ToString() == "幻灯片放映");
            var idleSlideHeaders = slideshowItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            foreach (var expected in new[] { "开始放映...", "循环模式", "轮播间隔", "切换动画" })
            {
                Assert(idleSlideHeaders.Contains(expected), "幻灯片设置子菜单包含: " + expected);
            }

            // 缩放：窗口保持全屏，仅图片缩放
            var zoomAt = typeof(MainWindow).GetMethod("ZoomAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
            zoomAt.Invoke(window, new object[] { new Point(10, 10), 1.15 });
            var scaleField = typeof(MainWindow).GetField("_activeLayer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var zoomLayer = (ImageLayer)scaleField.GetValue(window)!;
            Assert(Math.Abs(zoomLayer.ZoomScale - 1.15) < 1e-9, "缩放系数");
            Assert(Math.Abs(window.Width - workArea.Width) < 1e-6, "缩放不改变窗口尺寸");

            // 滚轮事件处理器：直接触发，验证事件 → 缩放（步进按图片分辨率自适应）
            var wheelHandler = typeof(MainWindow).GetMethod(
                "Window_PreviewMouseWheel",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var wheelArgs = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent,
            };
            var computeStep = typeof(MainWindow).GetMethod(
                "ComputeZoomStep",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var adaptiveStep = (double)computeStep.Invoke(window, new object[] { true })!;
            Assert(adaptiveStep > 1.10, "小图放大步进自适应增大");
            wheelHandler.Invoke(window, new object[] { window, wheelArgs });
            Assert(
                Math.Abs(zoomLayer.ZoomScale - 1.15 * adaptiveStep) < 1e-9,
                "滚轮事件联动缩放");
            Assert(Math.Abs(window.Width - workArea.Width) < 1e-6, "滚轮缩放窗口不变");

            // 小图放大到上限（20x）后停止，窗口不超过安全尺寸
            for (int i = 0; i < 10; i++)
            {
                zoomAt.Invoke(window, new object[] { new Point(10, 10), 1.5 });
            }

            Assert(Math.Abs(zoomLayer.ZoomScale - 20.0) < 1e-9, "小图放大上限");
            Assert(Math.Abs(window.Width - workArea.Width) < 1e-6, "放大后窗口仍为全屏");

            // 双击切换 适配窗口/原始大小
            var toggle = typeof(MainWindow).GetMethod("ToggleZoomMode", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var settingsField = typeof(MainWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!;
            toggle.Invoke(window, null);
            Assert(((ViewerSettings)settingsField.GetValue(window)!).ZoomMode == "Original", "切换到原始大小");
            toggle.Invoke(window, null);
            Assert(((ViewerSettings)settingsField.GetValue(window)!).ZoomMode == "Fit", "切换回适配窗口");

            // 幻灯片模式下菜单动态变化：包含切换动画子菜单
            var slideshowActiveField = typeof(MainWindow).GetField("_slideshowActive", BindingFlags.NonPublic | BindingFlags.Instance)!;
            slideshowActiveField.SetValue(window, true);
            var slideMenu = (ContextMenu)buildMenu.Invoke(window, null)!;
            slideshowItem = slideMenu.Items.OfType<MenuItem>()
                .First(m => m.Header?.ToString() == "幻灯片放映");
            var slideHeaders = slideshowItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            foreach (var expected in new[] { "继续轮播", "上一张", "下一张", "循环模式", "轮播间隔", "切换动画", "退出幻灯片" })
            {
                Assert(slideHeaders.Contains(expected), "幻灯片子菜单包含: " + expected);
            }

            var transitionItem = slideshowItem.Items.OfType<MenuItem>()
                .First(m => m.Header?.ToString() == "切换动画");
            var transitionHeaders = transitionItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            foreach (var expected in new[] { "无动画", "淡入淡出", "黑切", "划入", "时间", "切入方向" })
            {
                Assert(transitionHeaders.Contains(expected), "切换动画子菜单包含: " + expected);
            }
            slideshowActiveField.SetValue(window, false);
        }
        finally
        {
            window.Close();
            File.Delete(png);
        }
    }

    private static void TestGifAnimation()
    {
        var gifPath = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".gif");
        try
        {
            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(RenderFrame(64, 48, Colors.Red)));
            encoder.Frames.Add(BitmapFrame.Create(RenderFrame(64, 48, Colors.Blue)));
            using (var stream = File.Create(gifPath))
            {
                encoder.Save(stream);
            }

            var image = new Image();
            var gif = new GifAnimation(image);
            var first = gif.Load(gifPath);
            Assert(first is not null, "GIF 解码");
            Assert(gif.IsAnimatedGif, "GIF 识别为动画");
            gif.Pause();
            Assert(gif.IsPaused, "GIF 暂停");
            gif.Resume();
            Assert(!gif.IsPaused, "GIF 恢复");
            Assert(GifAdvances(image, gif), "GIF 动画逐帧播放");
            gif.Stop();
            Assert(!gif.IsAnimatedGif, "GIF 停止");
        }
        finally
        {
            File.Delete(gifPath);
        }
    }

    private static bool GifAdvances(Image image, GifAnimation gif)
    {
        gif.Start();
        var initial = image.Source;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pump = new DispatcherFrame();
        var probe = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        probe.Tick += (_, _) =>
        {
            if (!ReferenceEquals(image.Source, initial) || stopwatch.ElapsedMilliseconds > 3000)
            {
                pump.Continue = false;
            }
        };
        probe.Start();
        Dispatcher.PushFrame(pump);
        probe.Stop();
        return !ReferenceEquals(image.Source, initial);
    }

    private static RenderTargetBitmap RenderFrame(int width, int height, Color color)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));
        }

        bitmap.Render(visual);
        return bitmap;
    }

    private static void TestMoveResizeSync()
    {
        ResetSettings();
        var png = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        SaveTestPng(png);
        var window = new MainWindow(png);
        try
        {
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            ScreenService.MoveResize(window, window.Left, window.Top, 700, 500);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(Math.Abs(window.Width - 700) < 2 && Math.Abs(window.Height - 500) < 2, "外部 SetWindowPos 尺寸同步");

            // 自定义菜单模板：子菜单 Popup 锚定到父项且可打开
            var buildMenu = typeof(MainWindow).GetMethod("BuildContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
            window.ContextMenu = (ContextMenu)buildMenu.Invoke(window, null)!;
            window.ContextMenu!.IsOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var gimmickItem = window.ContextMenu.Items.OfType<MenuItem>()
                .First(m => m.Header?.ToString() == "无用小功能");
            Assert(gimmickItem.Template is not null, "菜单模板已应用");
            Assert(gimmickItem.Role == MenuItemRole.SubmenuHeader, "无用小功能父项为子菜单头");
            Assert(!gimmickItem.IsCheckable, "无用小功能父项不可勾选");
            Assert(gimmickItem.HasItems && gimmickItem.Items.Count == 1, "无用小功能子项数量");
            var aaItem = (MenuItem)gimmickItem.Items[0];
            Assert(aaItem.Header?.ToString() == "抗锯齿", "无用小功能首项为抗锯齿子菜单");
            var aaModeHeaders = aaItem.Items.OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            foreach (var expected in new[] { "模式", "SSAA 倍率", "MSAA 采样", "TXAA 质量" })
            {
                Assert(aaModeHeaders.Contains(expected), "抗锯齿子项包含: " + expected);
            }

            gimmickItem.IsSubmenuOpen = true;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert(gimmickItem.IsSubmenuOpen, "子菜单状态可打开");
            gimmickItem.IsSubmenuOpen = false;
            window.ContextMenu.IsOpen = false;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        finally
        {
            window.Close();
            File.Delete(png);
        }
    }

    private static void TestSliderDialog()
    {
        var dialog = new SliderDialog("切换动画时间", 50, 3000, 700, "{0:0} ms");
        var sliderField = typeof(SliderDialog).GetField(
            "ValueSlider",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(sliderField is not null, "滑块字段存在");
        var slider = (Slider)sliderField!.GetValue(dialog)!;
        Assert(slider.Minimum == 50 && slider.Maximum == 3000, "滑块范围");
        Assert(Math.Abs(slider.Value - 700) < 1e-9, "滑块初值");
        double? live = null;
        dialog.ValueChanged += v => live = v;
        slider.Value = 55;
        Assert(live is 55.0, "滑块变化实时回调");
    }

    private static void TestNumberDialog()
    {
        var dialog = new NumberDialog("切换动画时间", 50, 3000, 700, "请输入毫秒数值");
        var boxField = typeof(NumberDialog).GetField(
            "ValueBox",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert(boxField is not null, "数字输入框字段存在");
        var box = (TextBox)boxField!.GetValue(dialog)!;
        Assert(box.Text == "700", "数字输入框初值");
    }

    private static void TestNoClipping()
    {
        ResetSettings();
        // 72 DPI 纯色大图：老实现按 DPI 自然尺寸绘制，元素装不下时右/下边缘被裁掉。
        var png = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        SaveSolidPng(png, 300, 200, Colors.Red, 72);
        try
        {
            using (var fs = File.OpenRead(png))
            {
                var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Assert(Math.Abs(decoder.Frames[0].DpiX - 72) < 0.5, "测试图片携带 72 DPI 元数据");
            }

            var window = new MainWindow(png);
            try
            {
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var rtb = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Round(window.Width)),
                    Math.Max(1, (int)Math.Round(window.Height)),
                    96,
                    96,
                    PixelFormats.Pbgra32);
                rtb.Render(window);
                var pixels = new byte[rtb.PixelWidth * rtb.PixelHeight * 4];
                rtb.CopyPixels(pixels, rtb.PixelWidth * 4, 0);

                int w = rtb.PixelWidth;
                int h = rtb.PixelHeight;
                // 全屏窗口下图片居中：取图片自身右/下边缘采样
                int imageRightX = (int)Math.Round((w + 300.0) / 2.0) - 2;
                int imageBottomY = (int)Math.Round((h + 200.0) / 2.0) - 2;
                int right = ((h / 2) * w + imageRightX) * 4;
                int bottom = (imageBottomY * w + (w / 2)) * 4;
                Assert(pixels[right + 2] > 200 && pixels[right + 3] > 200, "右边缘未被裁切");
                Assert(pixels[bottom + 2] > 200 && pixels[bottom + 3] > 200, "下边缘未被裁切");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            File.Delete(png);
        }
    }

    private static void TestZoomCap()
    {
        ResetSettings();
        // 1000x750 图片：放大到固定上限 20x，窗口被工作区约束（不随图片无限变大）
        var png = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        SaveSolidPng(png, 1000, 750, Colors.Red, 96);
        var window = new MainWindow(png);
        try
        {
            var zoomAt = typeof(MainWindow).GetMethod("ZoomAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var scaleField = typeof(MainWindow).GetField("_activeLayer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            for (int i = 0; i < 40; i++)
            {
                zoomAt.Invoke(window, new object[] { new Point(10, 10), 1.5 });
            }

            Assert(Math.Abs(((ImageLayer)scaleField.GetValue(window)!).ZoomScale - 20.0) < 1e-9, "放大上限");
            var workArea = SystemParameters.WorkArea;
            Assert(
                Math.Abs(window.Width - workArea.Width) < 1e-6 &&
                Math.Abs(window.Height - workArea.Height) < 1e-6,
                "窗口保持全屏");
        }
        finally
        {
            window.Close();
            File.Delete(png);
        }
    }

    private static void TestComputeDownsample()
    {
        var method = typeof(MainWindow).GetMethod(
            "ComputeDownsample",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert(Math.Abs((double)method.Invoke(null, new object[] { 20000, 20000 })! - 0.8) < 1e-9, "2 万像素图降采样");
        Assert(Math.Abs((double)method.Invoke(null, new object[] { 16000, 12000 })! - 1.0) < 1e-9, "安全尺寸不降采样");
        Assert(Math.Abs((double)method.Invoke(null, new object[] { 1000, 750 })! - 1.0) < 1e-9, "小图不降采样");
    }

    private static void TestLargeImageRenders()
    {
        ResetSettings();
        // 3000x2000 大图：窗口为全屏，图片适配后居中，必须完整渲染。
        var png = Path.Combine(Path.GetTempPath(), "FloatingImageViewer.Tests." + Guid.NewGuid().ToString("N") + ".png");
        SaveSolidPng(png, 3000, 2000, Colors.Orange, 96);
        var window = new MainWindow(png);
        try
        {
            window.Show();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var rtb = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Round(window.Width)),
                Math.Max(1, (int)Math.Round(window.Height)),
                96,
                96,
                PixelFormats.Pbgra32);
            rtb.Render(window);
            var pixels = new byte[rtb.PixelWidth * rtb.PixelHeight * 4];
            rtb.CopyPixels(pixels, rtb.PixelWidth * 4, 0);

            int w = rtb.PixelWidth;
            int h = rtb.PixelHeight;
            var workArea = SystemParameters.WorkArea;
            Assert(
                Math.Abs(window.Width - workArea.Width) < 1e-6 &&
                Math.Abs(window.Height - workArea.Height) < 1e-6,
                "大图窗口为全屏");
            int center = ((h / 2) * w + (w / 2)) * 4;
            var fit = Math.Min(1.0, Math.Min(workArea.Width / 3000.0, workArea.Height / 2000.0));
            var displayWidth = 3000.0 * fit;
            int imageRightX = (int)Math.Round((w + displayWidth) / 2.0) - 2;
            int right = ((h / 2) * w + imageRightX) * 4;
            Assert(pixels[center + 2] > 200 && pixels[center + 3] > 200, "大图窗口中心渲染完整");
            Assert(pixels[right + 2] > 200 && pixels[right + 3] > 200, "大图右侧渲染完整");

            // 渲染启发式：大图使用默认（线性）插值
            var imgField = typeof(MainWindow).GetField("_activeLayer", BindingFlags.Instance | BindingFlags.NonPublic);
            var imgLayer = (ImageLayer)imgField!.GetValue(window)!;
            Assert(
                RenderOptions.GetBitmapScalingMode(imgLayer.Element) == BitmapScalingMode.Unspecified,
                "大图使用默认渲染");
        }
        finally
        {
            window.Close();
            File.Delete(png);
        }
    }

    private static void TestCache()
    {
        ImageCache.Configure("Count", 2);
        ImageCache.Clear();
        var bmp = (BitmapSource)BitmapSource.Create(
            4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        ImageCache.Add("a", bmp, 4, 4);
        ImageCache.Add("b", bmp, 4, 4);
        ImageCache.Add("c", bmp, 4, 4);
        Assert(ImageCache.Count == 2, "按数量淘汰");
        Assert(ImageCache.Get("a") is null, "最久未使用被淘汰");
        Assert(ImageCache.Get("c") is not null, "最近使用保留");
        var item = ImageCache.Get("b");
        Assert(item is not null && item.Value.OriginalWidth == 4, "缓存项携带原始尺寸");

        ImageCache.Configure("Size", 1);
        ImageCache.Clear();
        ImageCache.Add("x", bmp, 4, 4);
        Assert(ImageCache.Count == 1, "按大小缓存保留");
        ImageCache.Clear();
        Assert(ImageCache.Count == 0, "清除缓存");
        ImageCache.Configure("Count", 20);
    }

    private static void SaveSolidPng(string path, int width, int height, Color color, double dpi)
    {
        var bitmap = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // RenderTargetBitmap 的绘制坐标按 96 DPI 换算：
            // 要铺满 width×height 像素，绘制矩形需按 dpi/96 放大。
            var rect = new Rect(0, 0, width * 96.0 / dpi, height * 96.0 / dpi);
            dc.DrawRectangle(new SolidColorBrush(color), null, rect);
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void SaveTestPng(string path)
    {
        var bitmap = new RenderTargetBitmap(120, 90, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 120, 90));
            dc.DrawEllipse(Brushes.Red, null, new Point(60, 45), 30, 30);
        }

        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            Console.Error.WriteLine("失败: " + name);
            Environment.Exit(1);
        }
    }

    private static void ResetSettings()
        => File.Delete(SettingsService.SettingsPath);

}
