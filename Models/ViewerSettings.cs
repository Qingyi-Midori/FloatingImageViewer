namespace FloatingImageViewer.Models;

/// <summary>浮窗看图器的持久化设置，对应 settings.json。</summary>
public sealed class ViewerSettings
{
    public int Version { get; set; } = 1;

    /// <summary>窗口位置（未记录时使用 double.NaN）。</summary>
    public double Left { get; set; } = double.NaN;

    public double Top { get; set; } = double.NaN;

    public double Width { get; set; } = 640;

    public double Height { get; set; } = 480;

    public bool Topmost { get; set; } = true;

    /// <summary>剪贴板监听（复制图片自动粘贴到屏幕，默认关闭）。</summary>
    public bool ClipboardWatch { get; set; }

    /// <summary>Fit / Original / Stretch</summary>
    public string ZoomMode { get; set; } = "Fit";

    /// <summary>Transparent / Black / White / Checkerboard</summary>
    public string BackgroundMode { get; set; } = "Transparent";

    public double OpacityPercent { get; set; } = 100;

    // 吸附功能暂时移除（字段保留，便于以后恢复实现）。
    public bool SnapEnabled { get; set; }

    /// <summary>屏幕边缘/中心吸附（字段保留，功能暂未启用）。</summary>
    public bool ScreenEdgeSnap { get; set; }

    /// <summary>窗口边缘吸附（字段保留，功能暂未启用）。</summary>
    public bool WindowEdgeSnap { get; set; }

    /// <summary>抗锯齿模式：Off / SSAA / MSAA / TXAA（默认不开启）。</summary>
    public string AntiAliasing { get; set; } = "Off";

    /// <summary>SSAA 采样倍率预设：2 / 4 / 8。</summary>
    public int SsaaLevel { get; set; } = 4;

    /// <summary>MSAA 采样数预设：2 / 4 / 8。</summary>
    public int MsaaLevel { get; set; } = 4;

    /// <summary>TXAA 质量预设：Low / Medium / High。</summary>
    public string TxaaQuality { get; set; } = "Medium";

    public bool SlideshowLoop { get; set; } = true;

    public int SlideshowIntervalSeconds { get; set; } = 3;

    /// <summary>幻灯片切换动画：None / Fade / BlackCut / Slide</summary>
    public string SlideTransition { get; set; } = "Fade";

    public int TransitionDurationMs { get; set; } = 300;

    /// <summary>划入方向：Left / Right / Up / Down</summary>
    public string SlideDirection { get; set; } = "Left";

    /// <summary>图片缓存策略：Count（按数量） / Size（按大小 MB）。</summary>
    public string CacheStrategy { get; set; } = "Count";

    /// <summary>缓存上限：按数量时为张数，按大小时为 MB。</summary>
    public int CacheLimit { get; set; } = 20;

    /// <summary>图片左上角在屏幕上的位置（全屏窗口模式下用于恢复位置）。</summary>
    public double ImageLeft { get; set; } = double.NaN;

    public double ImageTop { get; set; } = double.NaN;

    /// <summary>会话恢复：上次退出前窗口内的所有图层（重启后自动恢复，解码走 ImageCache）。</summary>
    public List<SavedLayer> Layers { get; set; } = new();
}

/// <summary>单个图层的会话状态。</summary>
public sealed class SavedLayer
{
    public string Path { get; set; } = string.Empty;

    public double ZoomScale { get; set; } = 1.0;

    public double PanX { get; set; }

    public double PanY { get; set; }

    public bool Visible { get; set; } = true;
}
