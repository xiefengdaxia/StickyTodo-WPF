using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using StickyTodo.App.Services;
using StickyTodo.Core.Localization;

namespace StickyTodo.App.Views;

/// <summary>
/// 录屏前的设置卡片：区域 / 全屏、区域尺寸（可重新框选）、是否等 3 秒、清晰度。
/// 这里只收集参数，抓帧和编码由 <see cref="GifRecordingService"/> 接手。
/// </summary>
public partial class GifSetupWindow : Window
{
    public GifSetupWindow(
        Rectangle? region,
        bool fullScreen,
        bool countdown,
        string quality)
    {
        InitializeComponent();

        ResultRegion = region;
        ResultFullScreen = fullScreen;

        TitleText.Text = Loc.T("gif.setupTitle");
        SubtitleText.Text = Loc.T("gif.setupHint");
        ScopeLabel.Text = Loc.T("gif.scope");
        RegionRadio.Content = Loc.T("gif.region");
        FullScreenRadio.Content = Loc.T("gif.fullscreen");
        SizeLabel.Text = Loc.T("gif.regionSize");
        ReselectButton.Content = Loc.T("gif.reselect");
        CountdownBox.Content = Loc.T("gif.wait3s");
        QualityLabel.Text = Loc.T("gif.quality");
        CancelButton.Content = Loc.T("common.cancel");
        StartButton.Content = Loc.T("gif.start");

        QualityBox.Items.Add(new ComboBoxItem { Content = Loc.T("gif.qualityLow"), Tag = GifQualityPresets.Low });
        QualityBox.Items.Add(new ComboBoxItem { Content = Loc.T("gif.qualityMedium"), Tag = GifQualityPresets.Medium });
        QualityBox.Items.Add(new ComboBoxItem { Content = Loc.T("gif.qualityHigh"), Tag = GifQualityPresets.High });
        QualityBox.SelectedIndex = GifQualityPresets.Normalize(quality) switch
        {
            GifQualityPresets.Low => 0,
            GifQualityPresets.High => 2,
            _ => 1,
        };

        CountdownBox.IsChecked = countdown;

        if (fullScreen)
        {
            FullScreenRadio.IsChecked = true;
        }
        else
        {
            RegionRadio.IsChecked = true;
        }

        UpdateScopeUi();

        Loaded += (_, _) => Activate();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                CloseWith(false);
            }
            else if (e.Key == System.Windows.Input.Key.Enter)
            {
                Confirm();
            }
        };
    }

    /// <summary>选中的区域（物理像素）；全屏时为 null。</summary>
    public Rectangle? ResultRegion { get; private set; }

    /// <summary>true = 录整块屏。</summary>
    public bool ResultFullScreen { get; private set; }

    /// <summary>开始前等待 3 秒。</summary>
    public bool WaitThreeSeconds => CountdownBox.IsChecked == true;

    /// <summary>清晰度预设 key。</summary>
    public string Quality =>
        (QualityBox.SelectedItem as ComboBoxItem)?.Tag as string ?? GifQualityPresets.Medium;

    private void Scope_Changed(object sender, RoutedEventArgs e) => UpdateScopeUi();

    private void UpdateScopeUi()
    {
        if (RegionRow is null)
        {
            return;
        }

        var region = RegionRadio.IsChecked == true;
        RegionRow.IsEnabled = region;
        RegionRow.Opacity = region ? 1 : 0.45;

        if (region)
        {
            if (ResultRegion is { Width: > 0, Height: > 0 } current)
            {
                SizeValue.Text = $"{current.Width} × {current.Height}";
            }
            else
            {
                var screen = ScreenCaptureService.GetScreens().FirstOrDefault().Bounds;
                SizeValue.Text = $"{screen.Width} × {screen.Height}";
            }
        }
        else
        {
            var screens = ScreenCaptureService.GetScreens().Count;
            SizeValue.Text = screens > 1
                ? Loc.F("gif.fullScreenSizeMulti", screens)
                : $"{ScreenCaptureService.GetScreens().FirstOrDefault().Bounds.Width} × {ScreenCaptureService.GetScreens().FirstOrDefault().Bounds.Height}";
        }
    }

    private void Reselect_Click(object sender, RoutedEventArgs e)
    {
        // 不能 Hide()：ShowDialog 出来的窗口一隐藏就等于关掉，外面的 StartFromSetup 会以为用户取消了。
        // 这里改成临时退出置顶带，让同样 Topmost 的框选层盖在上面，结束后再置顶回来。
        Topmost = false;

        try
        {
            var picker = new GifRegionPickerWindow(ResultRegion);

            if (picker.ShowDialog() == true)
            {
                if (picker.FullScreenChosen)
                {
                    ResultFullScreen = true;
                    FullScreenRadio.IsChecked = true;
                }
                else if (picker.SelectedRegion is { } picked)
                {
                    ResultRegion = picked;
                    ResultFullScreen = false;
                    RegionRadio.IsChecked = true;
                }
            }
        }
        catch (Exception ex)
        {
            // 框选层出问题不能把设置卡片一起带走
            Serilog.Log.Error(ex, "[gif] 打开框选层失败");
        }
        finally
        {
            Topmost = true;
            Activate();
            UpdateScopeUi();
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWith(false);

    private void Confirm()
    {
        ResultFullScreen = FullScreenRadio.IsChecked == true;

        if (!ResultFullScreen && ResultRegion is null)
        {
            // 没框过也没有上次记录：直接用主屏整块
            ResultRegion = ScreenCaptureService.GetScreens().FirstOrDefault().Bounds;
        }

        CloseWith(true);
    }

    /// <summary>ShowDialog 之外被调用时（比如窗口已经关了）别抛异常，直接关掉。</summary>
    private void CloseWith(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }
}
