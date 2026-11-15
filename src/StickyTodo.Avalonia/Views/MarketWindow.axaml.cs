using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Mcp.Market;
using StickyTodo.Notes.Services;
using StickyTodo.Notes.ViewModels;

namespace StickyTodo.Notes.Views;

/// <summary>
/// 看板市场：把仓库 <c>market/</c> 目录里的看板包（board.json + html + js + preview.png）
/// 列成卡片，一键安装到本机（覆盖/新建自定义看板），或卸载已安装的。
/// 与 WPF 版 MarketWindow 同构：同样的卡片信息、同样的安装/卸载语义。
/// </summary>
public partial class MarketWindow : Window
{
    private readonly ManagerViewModel _viewModel;
    private readonly WindowManager _windows;

    public MarketWindow(ManagerViewModel viewModel, WindowManager windows)
    {
        _viewModel = viewModel;
        _windows = windows;

        InitializeComponent();
        Chrome.Apply(this, Shell, 14, 12);

        TitleBar.PointerPressed += OnTitleBarPressed;

        // 卡片内容是 C# 拼的，{loc:Loc} 管不到它，语言切换时整体重排一遍
        LocalizationService.LanguageChanged += OnLanguageChanged;

        Opened += (_, _) => Reload();
        Closed += (_, _) => LocalizationService.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Reload();

    /// <summary>重新扫一遍市场目录并重建卡片。</summary>
    private void Reload()
    {
        var directory = BoardMarket.ResolveDirectory();
        var boards = BoardMarket.Load(directory);
        var installed = InstalledByName();

        Cards.Children.Clear();
        EmptyText.IsVisible = boards.Count == 0;
        foreach (var board in boards)
        {
            Cards.Children.Add(BuildCard(
                board,
                installed.TryGetValue(board.Name, out var existing) ? existing : null));
        }

        var installedCount = boards.Count(b => installed.ContainsKey(b.Name));
        StatusText.Text = directory is null
            ? Loc.T("market.missingDir")
            : Loc.F("market.status", boards.Count, installedCount, directory);
    }

    /// <summary>本机已有的自定义看板，按名字索引（市场用名字判定"已安装"）。</summary>
    private Dictionary<string, Board> InstalledByName()
    {
        var map = new Dictionary<string, Board>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _viewModel.Boards)
        {
            if (row.Board.Kind == BoardKind.Custom && !map.ContainsKey(row.Board.Name))
            {
                map[row.Board.Name] = row.Board;
            }
        }

        return map;
    }

    private Border BuildCard(MarketBoard board, Board? installed)
    {
        var root = new Border { Classes = { "marketCard" }, ClipToBounds = true };
        var stack = new StackPanel();

        stack.Children.Add(BuildPreview(board));

        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 11) };

        // 名字 + 版本号
        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleRow.Children.Add(new TextBlock { Text = board.Name, Classes = { "cardName" } });

        if (!string.IsNullOrWhiteSpace(board.Version))
        {
            var version = new TextBlock { Text = "v" + board.Version, Classes = { "cardVersion" } };
            Grid.SetColumn(version, 1);
            titleRow.Children.Add(version);
        }

        body.Children.Add(titleRow);

        body.Children.Add(new TextBlock { Text = board.Summary, Classes = { "cardSummary" } });

        if (board.Tags.Count > 0)
        {
            var tags = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            foreach (var tag in board.Tags.Take(4))
            {
                tags.Children.Add(new Border
                {
                    Classes = { "cardTag" },
                    Child = new TextBlock { Text = tag, Classes = { "cardTagText" } },
                });
            }

            body.Children.Add(tags);
        }

        // 状态行：已安装 → 已安装；否则显示作者
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 9, 0, 0) };
        footer.Children.Add(new TextBlock
        {
            Text = installed is not null
                ? Loc.T("market.installed")
                : board.Author,
            Classes = { "cardState" },
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (installed is not null)
        {
            var captured = installed;

            var open = new Button
            {
                Content = Loc.T("market.open"),
                Classes = { "ghost" },
                FontSize = 11.5,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 0),
            };
            open.Click += (_, _) => _windows.OpenBoard(captured);
            buttons.Children.Add(open);

            var remove = new Button
            {
                Content = Loc.T("market.uninstall"),
                Classes = { "ghost" },
                FontSize = 11.5,
                Padding = new Thickness(10, 3, 10, 3),
            };
            remove.Click += async (_, _) => await UninstallAsync(board, captured);
            buttons.Children.Add(remove);
        }
        else
        {
            var install = new Button
            {
                Content = Loc.T("market.install"),
                Classes = { "primary" },
                FontSize = 11.5,
                Padding = new Thickness(14, 3, 14, 3),
            };
            install.Click += async (_, _) => await InstallAsync(board);
            buttons.Children.Add(install);
        }

        Grid.SetColumn(buttons, 1);
        footer.Children.Add(buttons);
        body.Children.Add(footer);

        stack.Children.Add(body);
        root.Child = stack;
        return root;
    }

    /// <summary>预览图（16:9 左右的横向裁切）；读不出来就显示「无预览图」文案。</summary>
    private static Border BuildPreview(MarketBoard board)
    {
        var preview = new Border { Classes = { "marketPreview" } };
        var bitmap = LoadPreview(board.PreviewPath);
        if (bitmap is not null)
        {
            preview.Child = new Image { Source = bitmap, Stretch = Stretch.UniformToFill };
        }
        else
        {
            preview.Child = new TextBlock { Text = Loc.T("market.noPreview"), Classes = { "cardNoPreview" } };
        }

        return preview;
    }

    private async Task InstallAsync(MarketBoard board)
    {
        StatusText.Text = Loc.F("market.installing", board.Name);
        var installed = await _viewModel.InstallMarketBoardAsync(
            board.Name, board.Html, board.Script, board.IntervalSeconds);
        if (installed is null)
        {
            StatusText.Text = Loc.F("market.installFailed", _viewModel.ErrorText ?? Loc.T("market.unknownError"));
            return;
        }

        Reload();
        StatusText.Text = Loc.F("market.installedDone", board.Name);
        _windows.OpenBoard(installed);
    }

    private async Task UninstallAsync(MarketBoard board, Board installed)
    {
        var confirmed = await ConfirmWindow.ConfirmAsync(
            this,
            Loc.T("market.title"),
            Loc.F("market.uninstallConfirm", board.Name));
        if (!confirmed)
        {
            return;
        }

        // 先把窗口关掉再删库，否则窗口还在轮询一个已经不存在的看板
        _windows.CloseBoard(installed.Id);
        var ok = await _viewModel.DeleteBoardAsync(installed);
        Reload();
        StatusText.Text = ok
            ? Loc.F("market.uninstalled", board.Name)
            : Loc.F("market.uninstallFailed", _viewModel.ErrorText ?? Loc.T("market.unknownError"));
    }

    private static Bitmap? LoadPreview(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return new Bitmap(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            // 预览图是锦上添花：缺失/损坏就退回文案
            return null;
        }
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => Reload();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 点在按钮上不拖动
        if (e.Source is Control source)
        {
            var current = source;
            while (current is not null)
            {
                if (current is Button)
                {
                    return;
                }

                current = current.Parent as Control;
            }
        }

        try
        {
            BeginMoveDrag(e);
        }
        catch (Exception)
        {
            // 某些合成/远程桌面场景下不支持拖动，忽略
        }
    }
}
