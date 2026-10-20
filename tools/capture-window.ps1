Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Capture {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$exe = Join-Path $PSScriptRoot "..\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
$hwnd = $p.MainWindowHandle
Write-Output "MainWindowHandle: $hwnd"
if ($hwnd -eq 0) { Write-Output "window handle is empty"; Stop-Process -Id $p.Id -Force; exit 1 }

[void][Win32Capture]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 600

$rect = New-Object Win32Capture+RECT
[void][Win32Capture]::GetWindowRect($hwnd, [ref]$rect)
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top
Write-Output "window size: ${w}x${h}"

$bmp = New-Object System.Drawing.Bitmap $w, $h
$gfx = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $gfx.GetHdc()
$ok = [Win32Capture]::PrintWindow($hwnd, $hdc, 2)
$gfx.ReleaseHdc($hdc)
$gfx.Dispose()

$out = Join-Path $PSScriptRoot "..\artifacts\mainwindow.png"
New-Item -ItemType Directory -Path (Split-Path $out) -Force | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Output "PrintWindow: $ok -> $out"
Stop-Process -Id $p.Id -Force
Write-Output "process stopped"