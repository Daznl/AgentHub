# UI test: the header crab is the AgentHub helper's button. On a TEST AgentHub with its own settings and sessions.
# UI Automation patterns only: no mouse, no keyboard, no focus stealing.
param([string]$Exe = 'C:\data\agenthub\build-repo\AgentHub.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern
$T = 'C:\data\agenthub\test-settings-crab'
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Find($root, $prop, $val, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $e = $root.FindFirst($TS::Descendants, (Cond $prop $val)); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "not found: $val" }
function HelperOpen { $pane = $win.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty 'AppHelper')); [bool]$pane -and -not $pane.Current.IsOffscreen }
function Settle { Start-Sleep -Milliseconds 600 }

# ---- fresh test state: the user's settings, with its own sessions folder and no panes ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $T | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))

$env:AGENTHUB_SETTINGS_DIR = $T
$p = Start-Process $Exe -PassThru
try {
  $win = $null
  for ($i = 0; $i -lt 60 -and -not $win; $i++) { Start-Sleep -Milliseconds 250; $win = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)) }
  $crab = Find $win $A::NameProperty 'AgentHub helper'

  "Crab button"
  Check ($crab.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) 'the crab is a button to UI Automation and screen readers'
  Check $crab.Current.IsKeyboardFocusable 'Tab reaches it'
  Check (-not $win.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty 'AppHelperBtn'))) 'the old round crab button is gone'
  Check (-not (HelperOpen)) 'helper closed at start'

  "Open and close with the crab"
  $crab.GetCurrentPattern($Invoke).Invoke(); Settle
  Check (HelperOpen) 'crab opens the helper'
  Check ($crab.Current.ItemStatus -eq 'Helper open') "crab says the helper is open (got '$($crab.Current.ItemStatus)')"
  $crab.GetCurrentPattern($Invoke).Invoke(); Settle
  Check (-not (HelperOpen)) 'crab again closes it'
  Check ($crab.Current.ItemStatus -eq '') 'crab no longer lit'

  "Close with the pane's X"
  $crab.GetCurrentPattern($Invoke).Invoke(); Settle
  (Find $win $A::NameProperty 'Close helper').GetCurrentPattern($Invoke).Invoke(); Settle
  Check (-not (HelperOpen)) 'X closes the helper'
  Check ($crab.Current.ItemStatus -eq '') 'and the crab goes unlit with it'
  $crab.GetCurrentPattern($Invoke).Invoke(); Settle
  Check (HelperOpen) 'one crab press reopens it (no stale state)'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
