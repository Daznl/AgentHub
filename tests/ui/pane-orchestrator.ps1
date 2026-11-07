# UI test: an orchestrator put on a running session with 🧭 Orchestrate, messaged from its box, and taken off with ✕ Detach.
# A scripted orchestrator (AGENTHUB_ORCHESTRATOR_COMMAND) and a cmd.exe session: no Claude usage. UI Automation only.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'))
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
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern; $Val = [System.Windows.Automation.ValuePattern]::Pattern
$T = 'C:\data\agenthub\test-pane-orchestrator'; $Shots = 'C:\data\agenthub\uitest'
New-Item -ItemType Directory -Force $Shots | Out-Null
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Buttons($root) { $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) }
function FindName($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { foreach ($e in (Buttons $root)) { if ($e.Current.Name -like $like) { return $e } }; Start-Sleep -Milliseconds 250 }
  throw "not found by name: $like" }
function Texts($root, $like) { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like $like }) }
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
function Click($el) { $el.GetCurrentPattern($Invoke).Invoke() }
# The context menu is a popup window of the process; its items are MenuItems.
function MenuItem($like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $script:procId))) {
      foreach ($m in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem)))) { if ($m.Current.Name -like $like) { return $m } } }
    Start-Sleep -Milliseconds 250 }
  throw "menu item not found: $like" }
function Edit($root, $nameLike, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($e in $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))) { if ($e.Current.Name -like $nameLike) { return $e } }
    Start-Sleep -Milliseconds 250 }
  throw "edit not found: $nameLike" }

# ---- fresh test state: one repository folder and a scripted orchestrator that answers the owner and instructs the session ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Pane Test'; New-Item -ItemType Directory -Force $proj | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings.Repositories = @(@{ Id = 'panetest'; Name = 'Pane Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
$fake = Join-Path $T 'fake-orchestrator.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000002"",""structured_output"":{""to_owner"":""Understood: I will have the session list the folder first."",""to_worker"":""dir"",""note"":""Scripted pane orchestrator""}}`r`n")

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3

  "1. An idle pane offers no Orchestrate button"
  Check ((Buttons $main | Where-Object { $_.Current.Name -like '*Orchestrate' }).Count -eq 0) 'no Orchestrate button before a session runs'

  "2. Start a Command Prompt session in the repository"
  Click (FindName $main 'Pane Test'); Start-Sleep 1
  Click (FindName $main 'Command Prompt'); Start-Sleep 6
  Shot $main 'p-running.png'
  $btn = FindName $main '*Orchestrate' 20
  Check ($null -ne $btn) 'a running session offers 🧭 Orchestrate'
  Check ((Texts $main '*ORCHESTRATOR').Count -eq 0) 'and has no box yet'

  "3. Orchestrate: pick a context from the menu, the box appears with its message line"
  Click $btn
  Click (MenuItem 'Orchestrator with no preset context'); Start-Sleep 2
  Check (@(Texts $main '*ORCHESTRATOR' | Where-Object { $_.Current.Name -ceq '🧠 ORCHESTRATOR' }).Count -eq 1) 'exactly one orchestrator box'
  Check ((Texts $main '🧭 Orchestrator').Count -eq 1) 'titled 🧭 Orchestrator'
  Check ((Texts $main 'LISTENING').Count -eq 1) 'listening, nothing asked yet'
  Check ((Texts $main 'Tell the orchestrator what the job is*').Count -eq 1) 'the box says how to use it'
  $input = Edit $main 'Message the orchestrator'
  Check ($null -ne $input) 'and has a message line'
  Check ((Buttons $main | Where-Object { $_.Current.Name -like '*Orchestrate' }).Count -eq 0) 'the Orchestrate button is gone while attached'
  Check ((Buttons $main | Where-Object { $_.Current.Name -like '*Pause*' }).Count -eq 0) 'no Pause: it is not a run'
  Shot $main 'p-attached.png'

  "4. A message goes to the orchestrator, which answers and instructs the session"
  $input.GetCurrentPattern($Val).SetValue('List the folder, then wait for me.')
  Click (FindName $main 'Send')
  $reply = @(); for ($i = 0; $i -lt 80 -and $reply.Count -eq 0; $i++) { Start-Sleep -Milliseconds 250; $reply = Texts $main 'Understood: I will have the session*' }
  Check ((Texts $main 'List the folder, then wait for me.').Count -eq 1) 'the message is listed as yours'
  Check ($reply.Count -eq 1) "the orchestrator's reply is shown"
  Check ((Texts $main '*Orchestrator → you').Count -eq 1) 'labelled Orchestrator → you'
  $sent = @(); for ($i = 0; $i -lt 40 -and $sent.Count -eq 0; $i++) { Start-Sleep -Milliseconds 250; $sent = Texts $main '*Orchestrator → worker' }   # typed in after the reply is shown
  Check ($sent.Count -eq 1) 'and its instruction to the session is listed'
  Check ((Texts $main 'Scripted pane orchestrator').Count -eq 1) 'with its note under the title'
  Check ((Texts $main 'ATTACHED').Count -eq 1) 'state ATTACHED once it has spoken'
  Check ($input.GetCurrentPattern($Val).Current.Value -eq '') 'the message line is cleared'
  Shot $main 'p-replied.png'

  "5. Detach: the box goes, the session carries on, Orchestrate is offered again"
  Click (FindName $main '*Detach'); Start-Sleep 2
  Check ((Texts $main '*ORCHESTRATOR').Count -eq 0) 'no box after detach'
  Check ($null -ne (FindName $main '*Orchestrate' 5)) 'Orchestrate offered again'
  Check ((Buttons $main | Where-Object { $_.Current.Name -eq 'Relaunch' }).Count -eq 1) 'the session is still running'
  Shot $main 'p-detached.png'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
