Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UiProbe {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public const uint LEFTDOWN = 0x0002;
    public const uint LEFTUP = 0x0004;
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
    public static string TitleOf(IntPtr h) {
        var sb = new StringBuilder(512);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
    public static RECT RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
}
"@

$exe = Join-Path $PSScriptRoot "..\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"
$data = "D:\Temp\opencode\ui-probe-data"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
$env:STICKYTODO_DATA_DIR = $data

$p = Start-Process -FilePath $exe -ArgumentList "--demo" -PassThru
Start-Sleep -Seconds 7
$p.Refresh()

$target = $null
$names = @()
foreach ($h in [UiProbe]::Windows([uint32]$p.Id)) {
    $t = [UiProbe]::TitleOf($h)
    $names += $t
    # Match the "work" board by its known 2-char title to stay ASCII-safe
    if ($t.Length -eq 2 -and $t -eq [char]0x5DE5 + [string][char]0x4F5C) { $target = $h }
}
Write-Output ("titles: " + ($names -join " / "))

if ($null -eq $target) {
    # fallback: first window that is not the manager (manager title is StickyTodo)
    foreach ($h in [UiProbe]::Windows([uint32]$p.Id)) {
        if ([UiProbe]::TitleOf($h) -ne "StickyTodo") { $target = $h; break }
    }
}
if ($null -eq $target) { Write-Output "no board window"; Stop-Process -Id $p.Id -Force; exit 1 }

[void][UiProbe]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 600
$r = [UiProbe]::RectOf($target)
Write-Output ("rect=({0},{1}) {2}x{3}" -f $r.Left, $r.Top, ($r.Right-$r.Left), ($r.Bottom-$r.Top))

$startX = $r.Left + 95
$startY = $r.Top + 130
$endY   = $r.Top + 215

Write-Output ("drag ({0},{1}) -> ({2},{3})" -f $startX, $startY, $startX, $endY)
[void][UiProbe]::SetCursorPos($startX, $startY)
Start-Sleep -Milliseconds 250
[UiProbe]::mouse_event([UiProbe]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 180
for ($y = $startY; $y -le $endY; $y += 8) {
    [void][UiProbe]::SetCursorPos($startX, $y)
    Start-Sleep -Milliseconds 45
}
Start-Sleep -Milliseconds 250
[UiProbe]::mouse_event([UiProbe]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 2

Stop-Process -Id $p.Id -Force
Write-Output "process stopped"
$log = Get-ChildItem "$data\logs" -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) { Get-Content $log.FullName -Encoding UTF8 | Select-String -Pattern 'demo|ERR|WRN' | Select-Object -Last 8 | ForEach-Object { $_.Line } }