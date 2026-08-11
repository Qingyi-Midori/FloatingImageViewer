using System.Windows;
using System.Windows.Input;

namespace FloatingImageViewer.Views;

/// <summary>
/// 数字输入弹窗：用于“切换动画时间”自定义（填写毫秒数值），
/// 只包含提示、数字输入框与确定/取消按钮。
/// </summary>
public partial class NumberDialog : Window
{
    private readonly int _minimum;
    private readonly int _maximum;

    public NumberDialog(string title, int minimum, int maximum, int value, string hint)
    {
        InitializeComponent();
        Title = title;
        _minimum = minimum;
        _maximum = maximum;
        HintText.Text = hint;
        ValueBox.Text = value.ToString();
        ValueBox.SelectAll();
        ValueBox.Focus();
    }

    /// <summary>点击“确定”后返回的有效数值；取消或输入无效时返回 null。</summary>
    public int? ResultValue { get; private set; }

    private void ValueBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = e.Text.Any(c => !char.IsDigit(c));

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(ValueBox.Text, out var value))
        {
            ResultValue = Math.Clamp(value, _minimum, _maximum);
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;
}
