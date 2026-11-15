using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StickyTodo.App.Services;
using StickyTodo.App.ViewModels;
using StickyTodo.Core.Entities;
using StickyTodo.Core.Localization;
using StickyTodo.Mcp.Market;

namespace StickyTodo.App.Views;

/// <summary>
/// 看板市场：把仓库 <c>market/</c> 目录里的看板包（board.json + html + js + preview.png）
/// 列成卡片，一键安装到本机（覆盖/新建自定义看板），或卸载已安装的。
/// </summary>
public partial class MarketWindow : Window
{
    private const double CardWidth = 288;
    private const double PreviewHeight = 152;

    private readonly ManagerViewModel _viewModel;
    private readonly WindowManager _windows;

    public MarketWindow(ManagerViewModel viewModel, WindowManager windows)
    {
        _viewModel = viewModel;
        _windows = windows;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        var directory = BoardMarket.ResolveDirectory();
        var boards = BoardMarket.Load(directory);
        var installed = InstalledByName();

        Cards.Items.Clear();
        EmptyText.Visibility = boards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var board in boards)
        {
            Cards.Items.Add(BuildCard(board, installed.TryGetValue(board.Name, out var existing) ? existing : null));
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
        var root = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("Brush.PaperOpaque"),
            BorderBrush = (Brush)FindResource("Brush.Line"),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
        };

        var stack = new StackPanel();

        // 预览图（16:9 左右的横向裁切）
        var previewGrid = new Grid { Height = PreviewHeight, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)) };
        if (board.PreviewPath is not null)
        {
            var image = new Image
            {
                Source = LoadPreview(board.PreviewPath),
                Stretch = Stretch.UniformToFill,
            };
            image.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth, PreviewHeight), 0, 0);
            previewGrid.Children.Add(image);
        }
        else
        {
            previewGrid.Children.Add(new TextBlock
            {
                Text = Loc.T("market.noPreview"),
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            });
        }

        stack.Children.Add(previewGrid);

        var body = new StackPanel { Margin = new Thickness(12, 9, 12, 11) };

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = board.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(name);

        if (!string.IsNullOrWhiteSpace(board.Version))
        {
            var version = new TextBlock
            {
                Text = "v" + board.Version,
                FontSize = 10,
                Opacity = 0.55,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(version, 1);
            titleRow.Children.Add(version);
        }

        body.Children.Add(titleRow);

        body.Children.Add(new TextBlock
        {
            Text = board.Summary,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 46,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0.85,
        });

        if (board.Tags.Count > 0)
        {
            var tags = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
            foreach (var tag in board.Tags.Take(4))
            {
                tags.Children.Add(new Border
                {
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(5, 0, 5, 1),
                    Margin = new Thickness(0, 0, 4, 4),
                    Child = new TextBlock { Text = tag, FontSize = 10, Opacity = 0.7 },
                });
            }

            body.Children.Add(tags);
        }

        var footer = new Grid { Margin = new Thickness(0, 9, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var state = new TextBlock
        {
            Text = installed is not null ? Loc.T("market.installed") : (board.Author.Length > 0 ? board.Author : ""),
            FontSize = 10.5,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        footer.Children.Add(state);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (installed is not null)
        {
            var open = new Button
            {
                Content = Loc.T("market.open"),
                Style = (Style)FindResource("Button.Ghost"),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 0),
            };
            var captured = installed;
            open.Click += (_, _) => _windows.OpenBoard(captured);
            buttons.Children.Add(open);

            var remove = new Button
            {
                Content = Loc.T("market.uninstall"),
                Style = (Style)FindResource("Button.Ghost"),
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
                Style = (Style)FindResource("Button.Primary"),
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

    private async Task InstallAsync(MarketBoard board)
    {
        StatusText.Text = Loc.F("market.installing", board.Name);
        var installed = await _viewModel.InstallMarketBoardAsync(board.Name, board.Html, board.Script, board.IntervalSeconds);
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
        var answer = MessageBox.Show(
            Loc.F("market.uninstallConfirm", board.Name),
            Loc.T("market.title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        _windows.CloseBoard(installed.Id);
        var ok = await _viewModel.DeleteBoardAsync(installed);
        Reload();
        StatusText.Text = ok
            ? Loc.F("market.uninstalled", board.Name)
            : Loc.F("market.uninstallFailed", _viewModel.ErrorText ?? Loc.T("market.unknownError"));
    }

    private static ImageSource? LoadPreview(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = (int)(CardWidth * 2);
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed || IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 非左键按下时重入，忽略
        }
    }

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or System.Windows.Controls.Primitives.ScrollBar)
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
