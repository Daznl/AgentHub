# Watches the crab for a while through UI Automation: the sprite's item status says "Face: wink" (and so on) while a face
# from faces.png is held, so the log proves the faces show up between outings and after rolls. A screenshot of the window is
# kept for each face seen (PrintWindow; it sometimes comes back blank, the status log is the proof). Runs a TEST AgentHub
# with its own settings, motion on. Screenshots and reads only, no input.
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'),
  [int]$Seconds = 300
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$T = 'C:\data\agenthub\test-crab-faces'; $Shots = 'C:\data\agenthub\uitest\crab-faces'
Remove-Item $Shots -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Shots, $T | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
# Motion on: the crab stays still while AnimatedBackground is off (by design), so this test keeps both on.
$proj = Join-Path $T 'Crab Test'; New-Item -ItemType Directory -Force $proj | Out-Null
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false
$settings.AnimatedBackground = $true; $settings.CrabMascot = $true
$settings.Repositories = @(@{ Id = 'crabtest'; Name = 'Crab Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"; $env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"
$p = Start-Process $Exe -PassThru
$log = Join-Path $Shots 'faces.log'
function Log($s) { $line = "$(Get-Date -Format HH:mm:ss.f) $s"; $line; Add-Content -Path $log -Value $line -Encoding UTF8 }
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $p.Id))); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  $h = [IntPtr]$main.Current.NativeWindowHandle
  $crab = $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'CrabSprite')))
  if (-not $crab) { throw 'no CrabSprite element' }
  Log "crab '$($crab.Current.Name)' at $($crab.Current.BoundingRectangle)"
  $faces = @{}; $last = ''; $lastX = -1; $moves = 0; $until = (Get-Date).AddSeconds($Seconds)
  while ((Get-Date) -lt $until) {
    $status = $crab.Current.ItemStatus; $x = [int]$crab.Current.BoundingRectangle.X
    if ($x -ne $lastX) { if ($lastX -ge 0) { $moves++ }; $lastX = $x }
    if ($status -ne $last) {
      Log "status '$status' at x=$x"
      if ($status -like 'Face: *') {
        $name = $status.Substring(6); $faces[$name] = 1 + $faces[$name]
        $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
        $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
        $bmp.Save((Join-Path $Shots ("face-{0}-{1}.png" -f $name, $faces[$name]))); $bmp.Dispose()
      }
      $last = $status
    }
    Start-Sleep -Milliseconds 100
  }
  Log "faces seen: $(($faces.GetEnumerator() | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', '); position changes: $moves"
  if ($faces.Count -eq 0) { Log 'FAIL: no face shown'; exit 1 }
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
