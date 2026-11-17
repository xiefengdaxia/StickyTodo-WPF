using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using StickyTodo.App.Services;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 「GIF 已复制到剪贴板」提示条：右下角出现，4 秒自动消失（鼠标停在上面时暂停计时，
/// 免得刚要点「另存为」它就没了）。
/// </summary>
public partial class GifToast : Window
{
    private readonly byte[] _gif;
    private readonly string? _filePath;
    private readonly DispatcherTimer _timer;

    public GifToast(byte[] gif, string? filePath)
    {
        InitializeComponent();

        _gif = gif;
        _filePath = filePath;

        MessageText.Text = Loc.T("gif.copied");
        SaveButton.Content = Loc.T("gif.save");
        CloseButton.Content = Loc.T("common.close");
        DetailText.Text = $"{gif.Length / 1024.0:0.#} KB" + SizeHint(Path.GetFileName(filePath));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            FadeOutAndClose();
        };

        Loaded += (_, _) =>
        {
            MoveToBottomRight();

            if (Content is FrameworkElement content)
            {
                Motion.SlideInFromTop(content, offset: 10);
            }

            _timer.Start();
        };

        Closed += (_, _) => _timer.Stop();
        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
            }
        };
    }

    private static string SizeHint(string? fileName) =>
        string.IsNullOrEmpty(fileName) ? string.Empty : $" · {fileName}";

    private void MoveToBottomRight()
    {
        var area = ScreenHelper.GetWorkArea(this);
        Left = area.Right - ActualWidth - 8;
        Top = area.Bottom - ActualHeight - 8;
    }

    private void FadeOutAndClose()
    {
        if (Content is FrameworkElement content)
        {
            Motion.FadeOutAndRemove(content, Close);
            return;
        }

        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("gif.save"),
                FileName = Path.GetFileName(_filePath) ?? $"{DateTime.Now:yyyyMMdd-HHmmss}.gif",
                DefaultExt = ".gif",
                Filter = "GIF|*.gif",
                AddExtension = true,
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            File.WriteAllBytes(dialog.FileName, _gif);
            Log.Information("[gif] 另存为 {Path}（{Kb} KB）", dialog.FileName, _gif.Length / 1024);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[gif] 另存为失败");
            ConfirmWindow.Notify(this, "GIF", "保存失败：" + ex.Message);
            return;
        }

        _timer.Stop();
        FadeOutAndClose();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }
}
