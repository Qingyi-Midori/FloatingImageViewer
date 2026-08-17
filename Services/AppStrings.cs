using System.Collections.Generic;
using System.Globalization;

namespace FloatingImageViewer.Services;

/// <summary>
/// 界面文案国际化：系统默认语言非中文时切换到英文。
/// key 使用中文原文，英文系统按下表翻译；未收录的文案回退中文（保证功能不丢）。
/// </summary>
public static class AppStrings
{
    /// <summary>系统默认语言是否为中文（简体/繁体）。</summary>
    public static readonly bool IsChinese =
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>取当前语言的文案。</summary>
    public static string T(string zh) => IsChinese ? zh : Translate(zh);

    private static readonly Dictionary<string, string> En = new()
    {
        // 主菜单
        ["窗口置顶"] = "Always on Top",
        ["剪贴板监听"] = "Clipboard Watch",
        ["图片信息"] = "Image Info",
        ["添加图片..."] = "Add Images...",
        ["图层"] = "Layers",
        ["框选马赛克"] = "Mosaic",
        ["图片对比"] = "Compare",
        ["缩放模式"] = "Zoom Mode",
        ["背景模式"] = "Background",
        ["无用小功能"] = "Gimmicks",
        ["不透明度（全局）"] = "Opacity (All)",
        ["不透明度（独立）"] = "Opacity (Per Image)",
        ["固定图片（全局）"] = "Pin (All)",
        ["固定图片（独立）"] = "Pin (Per Image)",
        ["图片缓存"] = "Image Cache",
        ["幻灯片放映"] = "Slideshow",
        ["暂停GIF动画"] = "Pause GIFs",
        ["继续GIF动画"] = "Resume GIFs",
        ["更换图片"] = "Replace Image",
        ["关闭图片"] = "Close Image",
        ["重置窗口"] = "Reset Window",
        ["退出程序"] = "Exit",
        ["关于..."] = "About...",
        // 图层子菜单
        ["暂无图片"] = "No Images",
        ["对比模式"] = "Compare Mode",
        ["上移一层"] = "Move Up",
        ["下移一层"] = "Move Down",
        ["删除图层"] = "Remove Layer",
        // 缩放 / 背景
        ["适配窗口"] = "Fit Window",
        ["原始大小"] = "Original Size",
        ["拉伸填充"] = "Stretch",
        ["完全透明"] = "Transparent",
        ["黑色"] = "Black",
        ["白色"] = "White",
        ["Alpha棋盘格"] = "Checkerboard",
        // 无用小功能
        ["抗锯齿"] = "Anti-aliasing",
        ["模式"] = "Mode",
        ["关闭"] = "Off",
        ["SSAA 倍率"] = "SSAA Level",
        ["MSAA 采样"] = "MSAA Samples",
        ["TXAA 质量"] = "TXAA Quality",
        ["低"] = "Low",
        ["中"] = "Medium",
        ["高"] = "High",
        // 缓存
        ["策略"] = "Strategy",
        ["按数量"] = "By Count",
        ["按大小"] = "By Size",
        ["上限"] = "Limit",
        ["清除缓存"] = "Clear Cache",
        ["缓存上限"] = "Cache Limit",
        // 幻灯片
        ["开始放映..."] = "Start Slideshow...",
        ["暂停轮播"] = "Pause",
        ["继续轮播"] = "Resume",
        ["上一张"] = "Previous",
        ["下一张"] = "Next",
        ["循环模式"] = "Loop",
        ["退出幻灯片"] = "Exit Slideshow",
        ["轮播间隔"] = "Interval",
        ["切换动画"] = "Transition",
        ["无动画"] = "None",
        ["淡入淡出"] = "Fade",
        ["黑切"] = "Black Cut",
        ["划入"] = "Slide",
        ["时间"] = "Duration",
        ["切入方向"] = "Direction",
        ["从左侧"] = "From Left",
        ["从右侧"] = "From Right",
        ["从上方"] = "From Top",
        ["从下方"] = "From Bottom",
        // 马赛克
        ["样式"] = "Style",
        ["马赛克"] = "Mosaic",
        ["高斯模糊"] = "Blur",
        ["噪声"] = "Smudge",
        ["纯色"] = "Solid",
        ["马赛克大小"] = "Block Size",
        ["模糊像素"] = "Blur Radius",
        ["噪声像素"] = "Smudge Radius",
        ["自定义色盘..."] = "Custom Color...",
        ["开始框选"] = "Start Drawing",
        ["退出马赛克"] = "Exit Mosaic",
        ["红色"] = "Red",
        ["绿色"] = "Green",
        ["蓝色"] = "Blue",
        ["黄色"] = "Yellow",
        ["青色"] = "Cyan",
        ["品红"] = "Magenta",
        // 图片对比
        ["选择对比图片..."] = "Choose Image...",
        ["重新选择对比图片..."] = "Choose Another...",
        ["布局"] = "Layout",
        ["左右并排"] = "Side by Side",
        ["上下并列"] = "Top & Bottom",
        ["滑动分割"] = "Split",
        ["适应大小"] = "Fit Size",
        ["退出对比"] = "Exit Compare",
        // 面板 / 按钮
        ["展开全部图层"] = "Expand All",
        ["收起图层列表"] = "Collapse List",
        ["完成"] = "Done",
        ["锁定所有图层"] = "Pin All Layers",
        ["固定"] = "Pin",
        ["确定"] = "OK",
        ["取消"] = "Cancel",
        // 关于面板
        ["关于浮图查看器"] = "About FloatingImageViewer",
        ["软件名称与版本"] = "Name & Version",
        ["程序简介"] = "Description",
        ["作者与版权"] = "Author & Copyright",
        ["开源信息"] = "Open Source",
        ["反馈与联系"] = "Feedback",
        ["版本号: "] = "Version: ",
        ["开发者: YeLee (Qingyi Midori)"] = "Developer: YeLee (Qingyi Midori)",
        ["版权: Copyright © 2026 Qingyi-Midori. All rights reserved."] = "Copyright: Copyright © 2026 Qingyi-Midori. All rights reserved.",
        ["一款轻量级的悬浮图片查看器，基于 WPF 框架开发，旨在提供简洁、高效的图片浏览体验。"] = "A lightweight floating image viewer built with WPF, designed for a simple and efficient image browsing experience.",
        ["本项目遵循 MIT 许可证 开源。"] = "Licensed under the MIT License.",
        ["源代码托管于 GitHub:"] = "Source code hosted on GitHub:",
        ["欢迎提交 Issue 或 Pull Request 来报告 Bug 或提出新功能建议。"] = "Welcome to submit Issues or Pull Requests to report bugs or suggest new features.",
        // 对话框 / 弹窗
        ["选择图片"] = "Select Image",
        ["添加图片"] = "Add Images",
        ["更换图片"] = "Replace Image",
        ["选择对比图片"] = "Choose Image",
        ["选择幻灯片文件夹"] = "Select Slideshow Folder",
        ["图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|所有文件|*.*"] = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|All files|*.*",
        ["剪贴板图片"] = "Clipboard Image",
        ["马赛克底图"] = "Mosaic Base",
        ["无法加载图片："] = "Cannot load image:",
        ["加载失败"] = "Load Failed",
        ["无法加载对比图片："] = "Cannot load compare image:",
        ["图片对比"] = "Compare",
        ["所选文件都无法加载。"] = "None of the selected files could be loaded.",
        ["无法读取文件夹："] = "Cannot read folder:",
        ["幻灯片放映"] = "Slideshow",
        ["该文件夹中没有支持的图片。"] = "No supported images found in this folder.",
        ["发生未处理的错误："] = "An unhandled error occurred:",
        ["浮窗看图器"] = "FloatingImageViewer",
        // 格式串
        ["{0:0} 秒"] = "{0:0} s",
        ["{0:0} 张"] = "{0:0} imgs",
        ["自定义..."] = "Custom...",
        ["10 张"] = "10 imgs",
        ["20 张"] = "20 imgs",
        ["50 张"] = "50 imgs",
        ["100 张"] = "100 imgs",
        ["1秒"] = "1 s",
        ["2秒"] = "2 s",
        ["3秒"] = "3 s",
        ["5秒"] = "5 s",
        ["10秒"] = "10 s",
        ["切换动画时间"] = "Transition Duration",
        ["图片"] = "Image",
    };

    private static string Translate(string zh)
        => En.TryGetValue(zh, out var en) ? en : zh;
}
