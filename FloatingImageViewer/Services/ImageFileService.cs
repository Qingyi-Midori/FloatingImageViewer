using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FloatingImageViewer.Services;

/// <summary>图片扩展名判断与自然排序（img2 排在 img10 前面）。</summary>
public static partial class ImageFileService
{
    private static readonly string[] SupportedExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico",
    };

    public static bool IsSupportedImage(string path)
        => SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static int NaturalCompare(string a, string b)
    {
        var tokensA = Tokenize(a);
        var tokensB = Tokenize(b);
        for (int i = 0; i < Math.Min(tokensA.Count, tokensB.Count); i++)
        {
            int cmp;
            if (tokensA[i].IsNumber && tokensB[i].IsNumber)
            {
                cmp = long.Parse(tokensA[i].Text, CultureInfo.InvariantCulture)
                    .CompareTo(long.Parse(tokensB[i].Text, CultureInfo.InvariantCulture));
            }
            else
            {
                cmp = string.Compare(tokensA[i].Text, tokensB[i].Text, StringComparison.OrdinalIgnoreCase);
            }

            if (cmp != 0)
            {
                return cmp;
            }
        }

        return tokensA.Count.CompareTo(tokensB.Count);
    }

    private static List<(string Text, bool IsNumber)> Tokenize(string value)
    {
        var result = new List<(string, bool)>();
        foreach (Match match in TokenRegex().Matches(value))
        {
            result.Add((match.Value, char.IsDigit(match.Value[0])));
        }

        return result;
    }

    [GeneratedRegex(@"\d+|[^\d]+")]
    private static partial Regex TokenRegex();
}
