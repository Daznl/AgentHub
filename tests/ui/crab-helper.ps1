# UI test: the header crab is the AgentHub helper's button. On a TEST AgentHub with its own settings and sessions.
# UI Automation patterns only: no mouse, no keyboard, no focus stealing.
param([string]$Exe = 'C:\data\agenthub\build-repo\AgentHub.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Name W -Namespace '' -MemberDefinition @'
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
'@
$Shots = 'C:\data\agenthub\uitest'; New-Item -ItemType Directory -Force $Shots | Out-Null
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
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

  "Context window: the crab is its helper's button too"
  (Find $win $A::NameProperty '📄 Context').GetCurrentPattern($Invoke).Invoke()
  $ctx = $null
  for ($i = 0; $i -lt 40 -and -not $ctx; $i++) { Start-Sleep -Milliseconds 250
    $ctx = $A::RootElement.FindFirst($TS::Descendants, (Cond $A::NameProperty 'AgentHub - Context')) }
  if (-not $ctx) { throw 'Context window did not open' }
  $ctxCrab = Find $ctx $A::NameProperty 'Context helper'
  function CtxHelperOpen { $x = $ctx.FindFirst($TS::Descendants, (Cond $A::NameProperty 'Close helper')); [bool]$x -and -not $x.Current.IsOffscreen }
  function WaitFor([scriptblock]$cond, [int]$ms = 8000) { $t = [Diagnostics.Stopwatch]::StartNew(); while ($t.ElapsedMilliseconds -lt $ms) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 100 }; & $cond }
  "The one crab travels to the window being worked in"
  Check (WaitFor { -not $ctxCrab.Current.IsOffscreen }) 'crab arrives at the context window (within 8 s)'
  Check $crab.Current.IsOffscreen 'and the main window spot is empty'
  $main = $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern); $main.SetWindowVisualState('Normal'); $win.SetFocus(); Settle
  Check (WaitFor { -not $crab.Current.IsOffscreen }) 'focus on the main window brings it back'
  Check $ctxCrab.Current.IsOffscreen 'context spot empty while it is away'
  $ctx.SetFocus(); Settle
  Check (WaitFor { -not $ctxCrab.Current.IsOffscreen }) 'back to the context window with its focus'
  Check ($ctxCrab.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) 'the context crab is a button'
  Check (-not $ctx.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty 'CrabBtn'))) 'the old round crab button is gone from the context window'
  Check (-not (CtxHelperOpen)) 'context helper closed at start'
  Shot $ctx 'crab-context-closed.png'
  $ctxCrab.GetCurrentPattern($Invoke).Invoke(); Settle
  Check (CtxHelperOpen) 'context crab opens the context helper'
  Check ($ctxCrab.Current.ItemStatus -eq 'Helper open') 'context crab lit'
  Shot $ctx 'crab-context-open.png'; Shot $win 'crab-main.png'
  (Find $ctx $A::NameProperty 'Close helper').GetCurrentPattern($Invoke).Invoke(); Settle
  Check (-not (CtxHelperOpen)) 'X closes the context helper'
  Check ($ctxCrab.Current.ItemStatus -eq '') 'context crab unlit again'
  $ctx.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Settle
  Check (WaitFor { -not $crab.Current.IsOffscreen }) 'closing the context window sends the crab home'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
