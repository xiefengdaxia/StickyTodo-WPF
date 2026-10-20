param(
    [int]$StartX = 0,
    [int]$StartY = 0,
    [int]$EndY = 0
)

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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
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
    public static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }
}
"@

$exe = Join-Path $PSScriptRoot "..\src\StickyTodo.App\bin\Debug\net8.0-windows\StickyTodo.exe"
$data = "D:\Temp\opencode\ui-drag-data"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
$env:STICKYTODO_DATA_DIR = $data

$p = Start-Process -FilePath $exe -ArgumentList "--ui-probe-hot" -PassThru
Start-Sleep -Seconds 7
$p.Refresh()

# find the todo board window (title = 4 chars for "我的待办")
$target = $null
foreach ($h in [UiProbe]::Windows([uint32]$p.Id)) {
    $t = [UiProbe]::TitleOf($h)
    Write-Output ("win: " + $t)
    if ($t.Length -eq 4) { $target = $h }
}
if ($null -eq $target) { Write-Output "no board"; Stop-Process -Id $p.Id -Force; exit 1 }

[void][UiProbe]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 700

Write-Output ("drag from ({0},{1}) down to y={2}" -f $StartX, $StartY, $EndY)
[void][UiProbe]::SetCursorPos($StartX, $StartY)
Start-Sleep -Milliseconds 300
[UiProbe]::mouse_event([UiProbe]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 200
for ($y = $StartY; $y -le $EndY; $y += 6) {
    [void][UiProbe]::SetCursorPos($StartX, $y)
    Start-Sleep -Milliseconds 40
}
Start-Sleep -Milliseconds 300
[UiProbe]::mouse_event([UiProbe]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 2

Stop-Process -Id $p.Id -Force
Write-Output "stopped"
$log = Get-ChildItem "$data\logs" -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) { Get-Content $log.FullName -Encoding UTF8 | Select-String -Pattern 'drag|probe' | ForEach-Object { $_.Line } }
