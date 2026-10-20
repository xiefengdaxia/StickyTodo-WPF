param(
    [string]$OutDir = "D:\Temp\opencode\scrcap",
    [double]$Opacity = 1.0,
    [string]$Tag = "100"
)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ScrCap {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public static List<IntPtr> Windows(uint targetPid) {
        var result = new List<IntPtr>();
        EnumWindows((h, l) => {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == targetPid && IsWindowVisible(h)) {
                var sb = new StringBuilder(512);
                if (GetWindowTextW(h, sb, sb.Capacity) > 0) result.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }
    public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
"@

$exe = Join-Path $PSScriptRoot "..\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"
$data = "D:\Temp\opencode\scrcap-data-$Tag"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
$env:STICKYTODO_DATA_DIR = $data

$p = Start-Process -FilePath $exe -ArgumentList @("--demo", "--window-opacity", "$Opacity") -PassThru
Start-Sleep -Seconds 8
$p.Refresh()

$qtitle = [string][char]0x6211 + [string][char]0x7684 + [string][char]0x5F85 + [string][char]0x529E
$target = $null
foreach ($h in [ScrCap]::Windows([uint32]$p.Id)) {
    if ([ScrCap]::TitleOf($h) -eq $qtitle) { $target = $h }
}
if ($null -eq $target) { Write-Output "board not found"; Get-Process StickyTodo -ErrorAction SilentlyContinue | Stop-Process -Force; exit 1 }

[void][ScrCap]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 1000
$r = [ScrCap]::RectOf($target)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Output ("opacity={0} rect=({1},{2}) {3}x{4}" -f $Opacity, $r.Left, $r.Top, $w, $ht)

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $ht)))
$g.Dispose()

$out = Join-Path $OutDir "screen-$Tag.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
# 采样纸面中心区域几个点
$samples = @()
foreach ($pt in @(@(60, 300), @(180, 300), @(180, 420))) {
    $c = $bmp.GetPixel($pt[0], $pt[1])
    $samples += "$($c.R),$($c.G),$($c.B)"
}
$bmp.Dispose()

Write-Output ("纸面采样: " + ($samples -join ' | '))
Write-Output "saved -> $out"

Get-Process StickyTodo -ErrorAction SilentlyContinue | Stop-Process -Force
