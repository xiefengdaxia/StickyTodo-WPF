using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StickyTodo.App.Services;

/// <summary>
/// 应用图标：窗口用 WPF 的 BitmapImage，托盘用 WinForms 的 Icon，
/// 两者都从嵌入资源读取同一个 ICO，保证处处一致。
/// </summary>
public static class AppIcon
{
    private const string ResourcePath = "pack://application:,,,/Assets/stickytodo.ico";

    private static ImageSource? _image;
    private static System.Drawing.Icon? _icon;

    /// <summary>WPF 窗口图标（大尺寸帧）。</summary>
    public static ImageSource Image
    {
        get
        {
            if (_image is not null)
            {
                return _image;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(ResourcePath, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            _image = bitmap;
            return _image;
        }
    }

    /// <summary>托盘图标（WinForms）。按 32px 帧读取，高 DPI 下也清晰。</summary>
    public static System.Drawing.Icon TrayIcon
    {
        get
        {
            if (_icon is not null)
            {
                return _icon;
            }

            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri(ResourcePath, UriKind.Absolute));
                if (info is not null)
                {
                    using var stream = info.Stream;
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    ms.Position = 0;

                    // 默认取合适的尺寸，避免托盘里被拉伸模糊
                    _icon = new System.Drawing.Icon(ms, 32, 32);
                    return _icon;
                }
            }
            catch (Exception)
            {
                // 资源读取失败时退回系统图标，不能让托盘因此挂掉
            }

            _icon = System.Drawing.SystemIcons.Application;
            return _icon;
        }
    }
}