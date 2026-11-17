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
    private readonly System.Drawing.Rectangle? _anchorRegion;
    private readonly DispatcherTimer _timer;

    /// <param name="anchorRegion">
    /// 这次录制的区域（物理像素）：提示条出现在**那块屏**的右下角，
    /// 而不是主屏；区域贴边时再夹进工作区，保证不会被屏幕边缘切掉。
    /// </param>
    public GifToast(byte[] gif, string? filePath, System.Drawing.Rectangle? anchorRegion = null)
    {
        InitializeComponent();

        _gif = gif;
        _filePath = filePath;
        _anchorRegion = anchorRegion;

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

    /// <summary>
    /// 贴到录制区域所在那块屏的右下角，再夹进它的工作区
    /// （任务栏在下面 / 缩放到 150% 时 Right-Bottom 也不一定真能放下）。
    /// </summary>
    private void MoveToBottomRight()
    {
        var work = OverlayPlacement.WorkAreaFor(
            _anchorRegion ?? ScreenCaptureService.GetVirtualScreenBounds());

        var size = OverlayPlacement.MeasurePx(this, work);

        OverlayPlacement.ClampToWorkArea(
            this,
            new System.Drawing.Rectangle(
                work.Right - size.Width,
                work.Bottom - size.Height,
                size.Width,
                size.Height));
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
