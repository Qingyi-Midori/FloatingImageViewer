using System.Windows;

namespace FloatingImageViewer.Views;

/// <summary>
/// 通用滑块弹窗，仅用于三种设置：不透明度、轮播间隔、网格大小。
/// 只包含滑块、实时数值显示与确定/取消按钮。
/// </summary>
public partial class SliderDialog : Window
{
    private readonly string _format;

    public SliderDialog(string title, double minimum, double maximum, double value, string format)
    {
        InitializeComponent();
        Title = title;
        _format = format;
        ValueSlider.Minimum = minimum;
        ValueSlider.Maximum = maximum;
        ValueSlider.Value = value;
    }

    /// <summary>点击“确定”后返回的值；取消时返回 null。</summary>
    public double? ResultValue { get; private set; }

    /// <summary>滑块数值变化时触发（用于“不透明度”实时调整）。</summary>
    public event Action<double>? ValueChanged;

    private void ValueSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ValueText.Text = string.Format(_format, e.NewValue);
        ValueChanged?.Invoke(e.NewValue);
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResultValue = ValueSlider.Value;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
