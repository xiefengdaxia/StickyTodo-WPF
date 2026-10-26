using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.Views;

/// <summary>极简单行输入框，用于新建/重命名看板与标签。</summary>
public partial class PromptWindow : Window
{
    private TaskCompletionSource<string?>? _completion;

    /// <summary>返回到手（已 trim）的文本；取消返回 null。</summary>
    public Func<string, string?>? Validate { get; init; }

    public PromptWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Input.KeyDown += OnInputKeyDown;
        Opened += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
        Chrome.Apply(this, Shell, 12, 12);
    }

    public PromptWindow(string label, string initial = "")
        : this()
    {
        LabelText.Text = label;
        Input.Text = initial;
    }

    /// <summary>弹窗并等待结果；取消返回 null。</summary>
    public static async Task<string?> PromptAsync(
        Window? owner,
        string title,
        string label,
        string initial = "",
        Func<string, string?>? validate = null)
    {
        var window = new PromptWindow(label, initial)
        {
            Title = title,
            Validate = validate,
        };

        var completion = new TaskCompletionSource<string?>();
        window._completion = completion;

        if (owner is { IsVisible: true })
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }

        return await completion.Task;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Submit();

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Dismiss(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss(null);
            e.Handled = true;
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        var text = Input.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            ShowError(Loc.T("prompt.required"));
            return;
        }

        var error = Validate?.Invoke(text);
        if (!string.IsNullOrEmpty(error))
        {
            ShowError(error);
            return;
        }

        Dismiss(text);
    }

    private void Dismiss(string? result)
    {
        _completion?.TrySetResult(result);
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        Input.Focus();
    }
}
