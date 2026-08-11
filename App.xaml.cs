using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FloatingImageViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"发生未处理的错误：\n\n{args.Exception.Message}",
                "浮窗看图器",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        // 可选：直接传入图片路径可跳过文件选择器（便于工具箱传参 / 测试）。
        string? imagePath = e.Args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && File.Exists(a));
        if (imagePath is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.ico|所有文件|*.*",
                Multiselect = false,
            };
            if (dialog.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            imagePath = dialog.FileName;
        }

        var window = new MainWindow(Path.GetFullPath(imagePath));
        if (!window.IsImageLoaded)
        {
            Shutdown();
            return;
        }

        MainWindow = window;
        window.Show();
        // 窗口保持显示，任务栏不显示图标，程序图标驻留系统托盘（左键激活窗口，右键菜单与窗口相同）。
        window.InitializeTray();
    }
}
