using System.Runtime.InteropServices;
using System.Windows.Threading;
using Serilog;
using StickyTodo.Core.Services;
using WinForms = System.Windows.Forms;

namespace StickyTodo.App.Services;

/// <summary>
/// 护眼校色。豆沙绿把整块屏幕的纯白映射成 #C7EDCC；色温按开尔文温度压 R/G/B。
/// 两者默认都走显卡 gamma 表（f.lux 的做法）——只改颜色比例、不盖色层，
/// 既没有灰雾，也不会出现在截图 / 录屏里。
/// 驱动拒绝 gamma 时，色温退回 DWM 的乘法色矩阵（虚拟显示器 / 远程桌面 / HDR 上仍能变色），
/// 那条路的色偏会进抓屏，所以截图时按 CaptureTint 反算。
///
/// 每 2 秒重放一次是自愈：别的程序（校色仪）改了 gamma、切了分辨率就抢回来，
/// 显示器热插拔后新屏幕也会被盖上。退出时恢复恒等表 / identity 矩阵。
/// </summary>
public sealed class EyeGammaService : IDisposable
{
    /// <summary>白色映射目标：豆沙绿 #C7EDCC。</summary>
    private const double TargetR = 199.0 / 255;
    private const double TargetG = 237.0 / 255;
    private const double TargetB = 204.0 / 255;

    private readonly AppSettingsService _settings;
    private DispatcherTimer? _reapplyTimer;
    private bool _rampsApplied;
    private bool _magApplied;
    private bool _magUnavailable;
    private bool _magFallbackLogged;
    private bool _noEffectLogged;

    public EyeGammaService(AppSettingsService settings)
    {
        _settings = settings;
    }

    public bool IsEnabled => _settings.EyeGammaEnabled || _settings.EyeTempEnabled;

    /// <summary>
    /// 当前色温给「抓屏结果」带来的偏色比例（1,1,1 = 抓屏本身不带色）。
    /// 截图功能拿它反算：贴图显示时屏幕会再暖一次，不反算就会暖两遍。
    /// </summary>
    public static (double R, double G, double B) CaptureTint { get; private set; } = (1, 1, 1);

    /// <summary>豆沙绿护眼状态可能变了（托盘勾选态要跟着刷）。</summary>
    public event EventHandler? Changed;

    /// <summary>按当前设置生效或还原。设置页 / 托盘 / 启动恢复都走这一个入口。</summary>
    public void ApplyFromSettings()
    {
        try
        {
            // 色温优先：它和豆沙绿都是 gamma，同时开会互相打架
            if (_settings.EyeTempEnabled)
            {
                ApplyTemperature(_settings.EyeTempKelvin);
            }
            else if (_settings.EyeGammaEnabled)
            {
                ApplyRamp(BuildRamp(TargetR, TargetG, TargetB));
                DisableMagColorEffect();
                CaptureTint = (1, 1, 1);
            }
            else
            {
                Restore();
            }
        }
        catch (Exception ex)
        {
            // 校色是锦上添花，别把设置页 / 托盘点崩
            Log.Error(ex, "[eye] 应用护眼设置失败");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把一张 gamma 表下发到所有显示器，并开启自愈重放。</summary>
    private void ApplyRamp(ushort[] ramp)
    {
        foreach (var dc in GetMonitorDcs())
        {
            if (SetDeviceGammaRamp(dc, ramp))
            {
                _rampsApplied = true;
            }

            ReleaseDc(IntPtr.Zero, dc);
        }

        StartReapplyTimer();
    }

    /// <summary>
    /// 色温：默认走显卡 gamma 表（f.lux 的做法）——色偏只出现在显示器上，
    /// 截图 / 录屏 / 抓屏都拿不到，所以截图不会变黄。
    /// 只有驱动明确拒绝 gamma 表时，才退回 DWM 的乘法色矩阵：那条路在虚拟显示器、
    /// 远程桌面、HDR 上照样能变色，但抓屏会带上色偏，所以抓屏时按 CaptureTint 反算回去。
    /// </summary>
    private void ApplyTemperature(int kelvin)
    {
        var (r, g, b) = TemperatureScale(kelvin);
        var ramp = BuildRamp(r, g, b);

        var gammaAccepted = false;
        foreach (var dc in GetMonitorDcs())
        {
            if (SetDeviceGammaRamp(dc, ramp))
            {
                gammaAccepted = true;
            }

            ReleaseDc(IntPtr.Zero, dc);
        }

        if (gammaAccepted && !_settings.EyeTempCompositor)
        {
            _rampsApplied = true;
            DisableMagColorEffect();
            CaptureTint = (1, 1, 1);
        }
        else
        {
            // 之前可能已经下过 gamma：先撤掉，避免和色矩阵叠成两层偏色
            ResetRamps();
            ApplyMagColorEffect(r, g, b);
            CaptureTint = _magApplied ? (r, g, b) : (1, 1, 1);

            if (!_magApplied && !_noEffectLogged)
            {
                _noEffectLogged = true;
                Log.Warning("[eye] 色温没生效：显卡不接受 gamma（gammaAccepted={Gamma}），DWM 色矩阵也不可用", gammaAccepted);
            }
        }

        StartReapplyTimer();
    }

    /// <summary>把已经下发的校色表恢复成恒等（没下过就什么都不做，别冲掉用户的显示器校色）。</summary>
    private void ResetRamps()
    {
        if (!_rampsApplied)
        {
            return;
        }

        _rampsApplied = false;

        var identity = BuildRamp(1.0, 1.0, 1.0);
        foreach (var dc in GetMonitorDcs())
        {
            _ = SetDeviceGammaRamp(dc, identity);
            ReleaseDc(IntPtr.Zero, dc);
        }
    }

    private void StartReapplyTimer()
    {
        _reapplyTimer ??= CreateReapplyTimer();
        _reapplyTimer.Start();
    }

    /// <summary>
    /// 色温比例：按开尔文温度压 R/G/B 三个通道（Tanner Helland 的近似公式），
    /// 返回 0-1 的三个缩放，不抬黑位。
    /// </summary>
    private static (double R, double G, double B) TemperatureScale(int kelvin)
    {
        var t = Math.Clamp(kelvin, 1000, 40000) / 100.0;

        double r, g, b;

        if (t <= 66)
        {
            r = 255;
            g = (99.4708025861 * Math.Log(t)) - 161.1195681661;
        }
        else
        {
            r = 329.698727446 * Math.Pow(t - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        }

        if (t >= 66)
        {
            b = 255;
        }
        else if (t <= 19)
        {
            b = 0;
        }
        else
        {
            b = (138.5177312231 * Math.Log(t - 10)) - 305.0447927307;
        }

        return (
            Math.Clamp(r, 0, 255) / 255,
            Math.Clamp(g, 0, 255) / 255,
            Math.Clamp(b, 0, 255) / 255);
    }

    /// <summary>DWM 全屏乘法色矩阵（Magnification API）：所有显示器一起生效，按通道做乘法。</summary>
    private void ApplyMagColorEffect(double r, double g, double b)
    {
        if (_magUnavailable)
        {
            return;
        }

        try
        {
            if (!_magApplied)
            {
                if (!MagInitialize())
                {
                    _magUnavailable = true;
                    Log.Warning("[eye] MagInitialize 失败，色温只能用 gamma");
                    return;
                }

                _magApplied = true;
            }

            // 每 2 秒重放一次是自愈：切分辨率 / 插拔显示器会重置 DWM 的色矩阵
            var effect = new MagColorEffect
            {
                Transform = new float[25],
            };
            effect.Transform[0] = (float)r;
            effect.Transform[6] = (float)g;
            effect.Transform[12] = (float)b;
            effect.Transform[18] = 1f;
            effect.Transform[24] = 1f;

            if (MagSetFullscreenColorEffect(ref effect))
            {
                if (!_magFallbackLogged)
                {
                    _magFallbackLogged = true;
                    Log.Information("[eye] 色温生效：DWM 乘法色矩阵（按通道缩放）");
                }
            }
        }
        catch (DllNotFoundException)
        {
            _magUnavailable = true;
        }
        catch (EntryPointNotFoundException)
        {
            _magUnavailable = true;
        }
    }

    /// <summary>撤掉 DWM 色矩阵（换成 identity 再反初始化）。</summary>
    private void DisableMagColorEffect()
    {
        if (!_magApplied)
        {
            return;
        }

        try
        {
            var identity = new MagColorEffect { Transform = new float[25] };
            identity.Transform[0] = 1f;
            identity.Transform[6] = 1f;
            identity.Transform[12] = 1f;
            identity.Transform[18] = 1f;
            identity.Transform[24] = 1f;
            _ = MagSetFullscreenColorEffect(ref identity);
            _ = MagUninitialize();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        _magApplied = false;
    }

    private void Restore()
    {
        _reapplyTimer?.Stop();

        DisableMagColorEffect();
        CaptureTint = (1, 1, 1);
        ResetRamps();
    }

    private DispatcherTimer CreateReapplyTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (_settings.EyeTempEnabled || _settings.EyeGammaEnabled)
            {
                ApplyFromSettings();
            }
        };
        return timer;
    }

    /// <summary>线性映射表：输入 i 输出 i×scale（WORD 是 8 位值 ×257 撑满 16 位）。</summary>
    private static ushort[] BuildRamp(double scaleR, double scaleG, double scaleB)
    {
        var ramp = new ushort[768];
        for (var i = 0; i < 256; i++)
        {
            ramp[i] = (ushort)Math.Clamp(Math.Round(i * scaleR * 257), 0, 65535);
            ramp[256 + i] = (ushort)Math.Clamp(Math.Round(i * scaleG * 257), 0, 65535);
            ramp[512 + i] = (ushort)Math.Clamp(Math.Round(i * scaleB * 257), 0, 65535);
        }

        return ramp;
    }

    /// <summary>每个显示器一个 DC（含主屏）；逐个下 gamma。</summary>
    private static IEnumerable<IntPtr> GetMonitorDcs()
    {
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var dc = CreateDc(screen.DeviceName, null, null, IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                yield return dc;
            }
        }

        // 枚举失败时兜底主屏
        if (!WinForms.Screen.AllScreens.Any())
        {
            var primary = GetDc(IntPtr.Zero);
            if (primary != IntPtr.Zero)
            {
                yield return primary;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            Restore();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[eye] 还原 gamma 失败");
        }
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateDCW")]
    private static extern IntPtr CreateDc(string? lpszDriver, string? lpszDevice, string? lpszOutput, IntPtr lpInitData);

    [DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr GetDc(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int ReleaseDc(IntPtr hWnd, IntPtr hDc);

    /// <summary>DWM 全屏色矩阵，行优先 5×5。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MagColorEffect
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] Transform;
    }

    [DllImport("Magnification.dll")]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll")]
    private static extern bool MagSetFullscreenColorEffect(ref MagColorEffect effect);
}
