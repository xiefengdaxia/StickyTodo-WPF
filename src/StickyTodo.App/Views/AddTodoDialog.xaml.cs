using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 日历「＋」的新建待办弹窗：待办内容 + 时间点（时/分两个下拉框，跟随皮肤）。
/// 勾选「提醒」才启用时间；返回值 = (标题, 时间点或 null)。
/// </summary>
public partial class AddTodoDialog : Window
{
    /// <summary>确定后拿结果：标题 + 时间点（null = 全天不提醒）。</summary>
    public (string Title, TimeSpan? TimeOfDay) Result { get; private set; }

    /// <summary>默认勾选提醒并预填时间（默认 09:00）。</summary>
    public AddTodoDialog()
    {
        InitializeComponent();

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

        // 默认提醒 09:00
        RemindCheck.IsChecked = true;
        HourBox.SelectedItem = "09";
        MinuteBox.SelectedItem = "00";

        Loaded += (_, _) =>
        {
            Input.Focus();
        };
    }

    public static (string? Title, TimeSpan? TimeOfDay)? Prompt(Window? owner)
    {
        var dialog = new AddTodoDialog
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        return dialog.ShowDialog() == true ? (dialog.Result.Title, dialog.Result.TimeOfDay) : null;
    }

    public string ResultTitle { get; private set; } = string.Empty;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var title = Input.Text.Trim();
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

        Result = (title, time);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>勾选状态切换：未勾选 = 全天，时间下拉禁用。</summary>
    private void RemindCheck_Changed(object sender, RoutedEventArgs e)
    {
        var enabled = RemindCheck.IsChecked == true;
        HourBox.IsEnabled = enabled;
        MinuteBox.IsEnabled = enabled;
        AllDayText.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Time_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 选了具体时间就自动勾上提醒（比"选了时间却不提醒"更符合直觉）
        if (HourBox.SelectedItem is not null && MinuteBox.SelectedItem is not null && RemindCheck.IsChecked != true)
        {
            RemindCheck.IsChecked = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        Input.Focus();
    }
}
