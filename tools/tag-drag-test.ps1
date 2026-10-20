param(
    [int]$StartX,
    [int]$StartY,
    [int]$EndX,
    [int]$EndY,
    [string]$TitleMatch = ""
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UiDrag {
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
$data = "D:\Temp\opencode\tag-drag-data"
if (Test-Path $data) { Remove-Item $data -Recurse -Force }
$env:STICKYTODO_DATA_DIR = $data

$p = Start-Process -FilePath $exe -ArgumentList "--tag-drag-probe", "--hot" -PassThru
Start-Sleep -Seconds 7
$p.Refresh()

$target = $null
foreach ($h in [UiDrag]::Windows([uint32]$p.Id)) {
    $t = [UiDrag]::TitleOf($h)
    Write-Output ("win: " + $t)
    if ($t -eq $TitleMatch) { $target = $h }
}
if ($null -eq $target) { Write-Output "board not found"; Stop-Process -Id $p.Id -Force; exit 1 }

[void][UiDrag]::SetForegroundWindow($target)
Start-Sleep -Milliseconds 800

Write-Output ("drag ({0},{1}) -> ({2},{3})" -f $StartX, $StartY, $EndX, $EndY)
[void][UiDrag]::SetCursorPos($StartX, $StartY)
Start-Sleep -Milliseconds 350
[UiDrag]::mouse_event([UiDrag]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 250

# move in small steps toward the chip
$steps = 30
for ($i = 1; $i -le $steps; $i++) {
    $x = [int]($StartX + (($EndX - $StartX) * $i / $steps))
    $y = [int]($StartY + (($EndY - $StartY) * $i / $steps))
    [void][UiDrag]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 45
}

Start-Sleep -Milliseconds 500
[UiDrag]::mouse_event([UiDrag]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 2

Write-Output "waiting for app self-check..."
$p.WaitForExit(15000) | Out-Null
Write-Output ("app exit code: " + $p.ExitCode)
Write-Output "stopped"
$log = Get-ChildItem "$data\logs" -Filter *.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) { Get-Content $log.FullName -Encoding UTF8 | Select-String -Pattern 'tagdrag|tag|drag' | ForEach-Object { $_.Line } }
