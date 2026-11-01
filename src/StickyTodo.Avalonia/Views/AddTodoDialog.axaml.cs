using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Core.Localization;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 日历「＋」的新建待办弹窗（Avalonia 版）：待办内容 + 时间点（时/分下拉，跟随皮肤）。
/// </summary>
public partial class AddTodoDialog : Window
{
    private TaskCompletionSource<(string? Title, TimeSpan? TimeOfDay)>? _completion;

    public AddTodoDialog()
    {
        InitializeComponent();
        Chrome.Apply(this, Shell, 12, 12);

        var hours = new ObservableCollection<string>();
        for (var h = 0; h < 24; h++)
        {
            hours.Add(h.ToString("00", CultureInfo.InvariantCulture));
        }

        var minutes = new ObservableCollection<string>();
        for (var m = 0; m < 60; m += 5)
        {
            minutes.Add(m.ToString("00", CultureInfo.InvariantCulture));
        }

        HourBox.ItemsSource = hours;
        MinuteBox.ItemsSource = minutes;

        RemindCheck.IsChecked = true;
        HourBox.SelectedItem = "09";
        MinuteBox.SelectedItem = "00";

        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                Dismiss(null);
            }
        };
    }

    /// <summary>弹出并等待结果：null = 取消；否则 (标题, 时间点或 null=全天)。</summary>
    public static async Task<(string? Title, TimeSpan? TimeOfDay)?> PromptAsync(Window? owner)
    {
        var dialog = new AddTodoDialog();
        var completion = new TaskCompletionSource<(string? Title, TimeSpan? TimeOfDay)>();
        dialog._completion = completion;

        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }

        return await completion.Task;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var title = Input.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            ShowError(Loc.T("prompt.required"));
            return;
        }

        if (RemindCheck.IsChecked == true && (HourBox.SelectedItem is null || MinuteBox.SelectedItem is null))
        {
            ShowError(Loc.T("cal.addTimeInvalid"));
            return;
        }

        TimeSpan? time = null;
        if (RemindCheck.IsChecked == true)
        {
            time = TimeSpan.ParseExact(
                $"{HourBox.SelectedItem}:{MinuteBox.SelectedItem}",
                @"hh\:mm",
                CultureInfo.InvariantCulture);
        }

        Dismiss((title, time));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Dismiss(null);

    private void Dismiss((string? Title, TimeSpan? TimeOfDay)? result)
    {
        _completion?.TrySetResult(result ?? (null, null));
        Close();
    }

    /// <summary>勾选状态切换：未勾选 = 全天，时间下拉禁用。</summary>
    private void RemindCheck_Changed(object? sender, RoutedEventArgs e)
    {
        var enabled = RemindCheck.IsChecked == true;
        HourBox.IsEnabled = enabled;
        MinuteBox.IsEnabled = enabled;
        AllDayText.IsVisible = !enabled;
    }

    /// <summary>选了具体时间就自动勾上提醒。</summary>
    private void Time_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (HourBox.SelectedItem is not null && MinuteBox.SelectedItem is not null && RemindCheck.IsChecked != true)
        {
            RemindCheck.IsChecked = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
        Input.Focus();
    }
}
