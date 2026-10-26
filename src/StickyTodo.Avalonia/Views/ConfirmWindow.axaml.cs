using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace StickyTodo.Notes.Views;

/// <summary>确认对话框（删除看板这类不可逆操作）。</summary>
public partial class ConfirmWindow : Window
{
    private TaskCompletionSource<bool>? _completion;

    public ConfirmWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Dismiss(false);
            }
        };
    }

    public static async Task<bool> ConfirmAsync(Window? owner, string heading, string body)
    {
        var window = new ConfirmWindow();
        Chrome.Apply(window, window.Shell, 12, 12);
        window.HeadingText.Text = heading;
        window.BodyText.Text = body;

        var completion = new TaskCompletionSource<bool>();
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

    private void Ok_Click(object? sender, RoutedEventArgs e) => Dismiss(true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Dismiss(false);

    private void Dismiss(bool result)
    {
        _completion?.TrySetResult(result);
        Close();
    }
}
