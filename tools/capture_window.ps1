Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$sig = @'
using System;
using System.Runtime.InteropServices;
public class WinApi {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
Add-Type -TypeDefinition $sig

[void][WinApi]::SetProcessDPIAware()

$proc = Get-Process -Name "Dip3Grouper.Desktop" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { "NO_PROCESS"; exit 1 }

$h = $proc.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { "NO_WINDOW_HANDLE"; exit 1 }

[void][WinApi]::ShowWindow($h, 9)      # SW_RESTORE
[void][WinApi]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 900

$r = New-Object WinApi+RECT
[void][WinApi]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$hh = $r.Bottom - $r.Top
if ($w -le 0 -or $hh -le 0) { "BAD_RECT $w x $hh"; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $hh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
$g.Dispose()

$out = Join-Path $env:TEMP "dip3-window.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
"SAVED $out ${w}x${hh}"
