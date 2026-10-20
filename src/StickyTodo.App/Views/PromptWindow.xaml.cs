using System.Windows;
using System.Windows.Controls;

namespace StickyTodo.App.Views;

/// <summary>极简单行输入框，用于新建/重命名看板与标签。</summary>
public partial class PromptWindow : Window
{
    /// <summary>返回到手（已 trim）的文本；取消返回 null。</summary>
    public Func<string, string?>? Validate { get; init; }

    public PromptWindow(string label, string initial = "")
    {
        InitializeComponent();
        LabelText.Text = label;
        Input.Text = initial;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public static string? Prompt(Window? owner, string title, string label, string initial = "", Func<string, string?>? validate = null)
    {
        var window = new PromptWindow(label, initial)
        {
            Title = title,
            Owner = owner is { IsLoaded: true } ? owner : null,
            Validate = validate,
        };

        return window.ShowDialog() == true ? window.Result : null;
    }

    public string Result { get; private set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = Input.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            ShowError("不能为空。");
            return;
        }

        var error = Validate?.Invoke(text);
        if (!string.IsNullOrEmpty(error))
        {
            ShowError(error);
            return;
        }

        Result = text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        Input.Focus();
    }
}