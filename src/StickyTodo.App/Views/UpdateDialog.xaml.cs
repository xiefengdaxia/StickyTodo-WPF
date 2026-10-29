using System.Diagnostics;
using System.Globalization;
using System.Windows;
using StickyTodo.Core.Localization;
using StickyTodo.Updates;

namespace StickyTodo.App.Views;

/// <summary>
/// 「检查更新」的结果弹窗。三种状态共用一套布局：
///   有新版本 → 版本对比 + 发布说明 + 打开下载页
///   已是最新 → 只显示版本对比
///   检查失败 → 失败原因 + 原始错误细节
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateCheckResult _result;

    public UpdateDialog(UpdateCheckResult result, UpdateRepository repository)
    {
        InitializeComponent();
        _result = result;

        Render(result, repository);
    }

    public static void ShowResult(Window? owner, UpdateCheckResult result, UpdateRepository repository)
    {
        var dialog = new UpdateDialog(result, repository)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        dialog.ShowDialog();
    }

    private void Render(UpdateCheckResult result, UpdateRepository repository)
    {
        var current = AppVersionInfo.Display(result.CurrentVersion);
        var release = result.Release;

        DownloadButton.Content = Loc.T("update.openDownload");

        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                HeadingText.Text = Loc.F("update.available", AppVersionInfo.Display(result.LatestVersion));
                VersionsText.Text = Loc.F("update.current", current) + "  →  " +
                                    Loc.F("update.latest", AppVersionInfo.Display(result.LatestVersion));
                DownloadButton.Visibility = Visibility.Visible;
                break;

            case UpdateCheckStatus.UpToDate:
                HeadingText.Text = Loc.T("update.upToDate");
                VersionsText.Text = Loc.F("update.current", current);
                break;

            default:
                HeadingText.Text = Loc.F("update.failed", DescribeFailure(result));
                VersionsText.Text = Loc.F("update.current", current);

                // 失败时把底层原因（HTTP 码 / TLS 报错）放在下面，方便用户判断是不是被代理拦了
                if (!string.IsNullOrWhiteSpace(result.Detail))
                {
                    NotesText.Text = result.Detail;
                    NotesBox.Visibility = Visibility.Visible;
                }

                break;
        }

        if (release is not null && result.Status != UpdateCheckStatus.Failed)
        {
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(release.Title) &&
                !string.Equals(release.Title, release.TagName, StringComparison.Ordinal))
            {
                meta.Add(release.Title);
            }

            if (release.PublishedAt is { } published)
            {
                meta.Add(Loc.F("update.published",
                    published.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture)));
            }

            if (meta.Count > 0)
            {
                ReleaseMetaText.Text = string.Join(" · ", meta);
                ReleaseMetaText.Visibility = Visibility.Visible;
            }

            if (result.Status == UpdateCheckStatus.Available)
            {
                var notes = StickyTodo.Updates.ReleaseNotes.ToPlainText(release.Notes);
                NotesText.Text = string.IsNullOrWhiteSpace(notes) ? Loc.T("update.noNotes") : notes;
                NotesBox.Visibility = Visibility.Visible;
            }
        }

        SourceText.Text = Loc.F("update.source", "github.com/" + repository.Slug);
    }

    private static string DescribeFailure(UpdateCheckResult result) => result.Failure switch
    {
        UpdateFailure.NoRelease => Loc.T("update.failNoRelease"),
        UpdateFailure.RateLimited => Loc.T("update.failRateLimited"),
        UpdateFailure.Server => Loc.T("update.failServer"),
        UpdateFailure.BadResponse => Loc.T("update.failBadResponse"),
        _ => Loc.T("update.failNetwork"),
    };

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var url = _result.Release?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "StickyTodo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标提前抬起，忽略
            }
        }
    }
}
