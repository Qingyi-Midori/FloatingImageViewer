using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingImageViewer.Models;

namespace FloatingImageViewer.Services;

/// <summary>settings.json 的读写（原子写入，损坏时回退默认值）。</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 窗口/图片位置未记录时使用 double.NaN，允许以 NaN 字面量序列化。
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string SettingsPath
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("YELEE_IMGVIEWER_DATA_DIR");
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "FloatingImageViewer");
            }

            return Path.Combine(dir, "settings.json");
        }
    }

    public static ViewerSettings Load()
    {
        var settings = new ViewerSettings();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    settings = JsonSerializer.Deserialize<ViewerSettings>(json, JsonOptions) ?? new ViewerSettings();
                }
            }
        }
        catch
        {
            settings = new ViewerSettings();
        }

        Normalize(settings);
        return settings;
    }

    public static void Save(ViewerSettings settings)
    {
        try
        {
            var path = SettingsPath;
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, path, true);
        }
        catch
        {
            // 保存失败不阻断使用。
        }
    }

    private static void Normalize(ViewerSettings s)
    {
        s.Width = Math.Clamp(s.Width, 160, 5000);
        s.Height = Math.Clamp(s.Height, 120, 4000);
        if (double.IsNaN(s.Left) || double.IsNaN(s.Top))
        {
            s.Left = double.NaN;
            s.Top = double.NaN;
        }

        s.OpacityPercent = Math.Clamp(s.OpacityPercent, 0, 100);
        s.SlideshowIntervalSeconds = Math.Clamp(s.SlideshowIntervalSeconds, 1, 60);
        s.TransitionDurationMs = Math.Clamp(s.TransitionDurationMs, 50, 3000);
        s.CacheLimit = Math.Clamp(s.CacheLimit, 1, 10000);
        if (s.ZoomMode is not ("Fit" or "Original" or "Stretch"))
        {
            s.ZoomMode = "Fit";
        }

        if (s.AntiAliasing is not ("Off" or "SSAA" or "MSAA" or "TXAA"))
        {
            s.AntiAliasing = "Off";
        }

        if (s.SsaaLevel is not (2 or 4 or 8))
        {
            s.SsaaLevel = 4;
        }

        if (s.MsaaLevel is not (2 or 4 or 8))
        {
            s.MsaaLevel = 4;
        }

        if (s.TxaaQuality is not ("Low" or "Medium" or "High"))
        {
            s.TxaaQuality = "Medium";
        }

        if (s.BackgroundMode is not ("Transparent" or "Black" or "White" or "Checkerboard"))
        {
            s.BackgroundMode = "Transparent";
        }

        if (s.SlideTransition is not ("None" or "Fade" or "BlackCut" or "Slide"))
        {
            s.SlideTransition = "Fade";
        }

        if (s.SlideDirection is not ("Left" or "Right" or "Up" or "Down"))
        {
            s.SlideDirection = "Left";
        }

        if (s.CacheStrategy is not ("Count" or "Size"))
        {
            s.CacheStrategy = "Count";
        }

        if (double.IsNaN(s.ImageLeft) || double.IsNaN(s.ImageTop))
        {
            s.ImageLeft = double.NaN;
            s.ImageTop = double.NaN;
        }
    }
}
