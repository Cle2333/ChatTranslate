# 一键截取 ChatTranslate 主界面与设置页，输出到 docs/screenshots/。
# 需要在你自己的交互桌面运行（Codex 沙箱/提权会话拿不到可见窗口）。
# 用法：powershell -ExecutionPolicy Bypass -File tools\capture-screenshots.ps1
# 截图会使用当前真实配置；建议先在程序里新建一个空对话，避免把历史内容截进去。
param(
  [string]$Exe,
  [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Exe) { $Exe = Join-Path $repo 'publish\ChatTranslate\ChatTranslate.exe' }
if (-not $OutDir) { $OutDir = Join-Path $repo 'docs\screenshots' }
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "找不到程序：$Exe" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class ChatTranslateShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

function Save-Window([IntPtr]$hwnd, [string]$path) {
  $r = New-Object ChatTranslateShot+RECT
  if (-not [ChatTranslateShot]::GetWindowRect($hwnd, [ref]$r)) { throw 'GetWindowRect 失败' }
  $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  $ok = [ChatTranslateShot]::PrintWindow($hwnd, $hdc, 2)
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host ("已保存 {0}（{1} x {2}）" -f $path, $w, $h)
}

Get-Process ChatTranslate -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Write-Host "正在启动：$Exe"
$p = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Milliseconds 500
  $p.Refresh()
  if ($p.HasExited) { throw ("程序已退出，代码 0x{0:X8}" -f $p.ExitCode) }
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { throw "30 秒内没有拿到主窗口（Session=$($p.SessionId)）" }

[void][ChatTranslateShot]::SetForegroundWindow($hwnd)
[void][ChatTranslateShot]::ShowWindow($hwnd, 3)
Start-Sleep -Seconds 2
Save-Window $hwnd (Join-Path $OutDir 'main.png')
[void][ChatTranslateShot]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '设置')
$btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $btn) { throw '主界面没有找到「设置」按钮' }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3

$script:found = New-Object System.Collections.Generic.List[IntPtr]
$script:mainHwnd = $hwnd; $script:targetPid = $p.Id
$cb = [ChatTranslateShot+EnumProc]{
  param($h,$l)
  $wpid = 0
  [void][ChatTranslateShot]::GetWindowThreadProcessId($h, [ref]$wpid)
  if ($wpid -eq $script:targetPid -and $h -ne $script:mainHwnd -and [ChatTranslateShot]::IsWindowVisible($h)) {
    $r = New-Object ChatTranslateShot+RECT
    if ([ChatTranslateShot]::GetWindowRect($h, [ref]$r)) {
      if (($r.Right - $r.Left) -gt 200 -and ($r.Bottom - $r.Top) -gt 200) { $script:found.Add($h) }
    }
  }
  return $true
}
[void][ChatTranslateShot]::EnumWindows($cb, [IntPtr]::Zero)
if ($script:found.Count -eq 0) { throw '没有找到设置窗口' }
$settingsHwnd = $script:found[0]
[void][ChatTranslateShot]::SetForegroundWindow($settingsHwnd)
Start-Sleep -Milliseconds 800

$sroot = [System.Windows.Automation.AutomationElement]::FromHandle($settingsHwnd)
$restartCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '重启 Ollama')
$restartBtn = $sroot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $restartCond)
if ($restartBtn) {
  try {
    $restartBtn.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView()
    Start-Sleep -Milliseconds 1000
    Write-Host '设置页已滚动到「服务」区域'
  } catch { Write-Host "滚动失败，截取当前视图：$($_.Exception.Message)" }
} else {
  Write-Host '没找到「重启 Ollama」按钮，截取当前视图'
}
Save-Window $settingsHwnd (Join-Path $OutDir 'settings.png')

Stop-Process -Id $p.Id -Force
Write-Host "完成：$(Join-Path $OutDir 'main.png')、$(Join-Path $OutDir 'settings.png')"