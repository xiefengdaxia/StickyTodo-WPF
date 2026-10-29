using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyTodo.Core.Localization;
using StickyTodo.Updates;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 「检查更新」的结果弹窗（Avalonia / Win7 版）。
/// 三种状态共用一套布局：有新版本 / 已是最新 / 检查失败。
/// </summary>
public partial class UpdateDialog : Window
{
    private string? _downloadUrl;

    public UpdateDialog()
    {
        InitializeComponent();
        Chrome.Apply(this, Shell, 12, 12);
    }

    public static void ShowResult(Window? owner, UpdateCheckResult result, UpdateRepository repository)
    {
        var dialog = new UpdateDialog();
        dialog.Render(result, repository);

        if (owner is { IsVisible: true })
        {
            _ = dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    private void Render(UpdateCheckResult result, UpdateRepository repository)
    {
        var current = AppVersionInfo.Display(result.CurrentVersion);

        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                HeadingText.Text = Loc.F("update.available", AppVersionInfo.Display(result.LatestVersion));
                VersionsText.Text = Loc.F("update.current", current) + "  →  " +
                                    Loc.F("update.latest", AppVersionInfo.Display(result.LatestVersion));
                DownloadButton.IsVisible = true;
                break;

            case UpdateCheckStatus.UpToDate:
                HeadingText.Text = Loc.T("update.upToDate");
                VersionsText.Text = Loc.F("update.current", current);
                break;

            default:
                HeadingText.Text = Loc.F("update.failed", DescribeFailure(result));
                VersionsText.Text = Loc.F("update.current", current);

                if (!string.IsNullOrWhiteSpace(result.Detail))
                {
                    NotesText.Text = result.Detail;
                    NotesBox.IsVisible = true;
                }

                break;
        }

        var release = result.Release;
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
                ReleaseMetaText.IsVisible = true;
            }

            if (result.Status == UpdateCheckStatus.Available)
            {
                var notes = ReleaseNotes.ToPlainText(release.Notes);
                NotesText.Text = string.IsNullOrWhiteSpace(notes) ? Loc.T("update.noNotes") : notes;
                NotesBox.IsVisible = true;
                _downloadUrl = release.DownloadUrl;
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

    private void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_downloadUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 打不开浏览器（没有默认浏览器 / 被策略拦）时别把程序搞崩，把原因写回弹窗
            NotesText.Text = ex.Message;
            NotesBox.IsVisible = true;
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
