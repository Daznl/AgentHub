# UI test: runs of advanced templates are kept on disk (detach, restart), listed in Runs, resumed on a session and deleted.
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

function Win($titleLike, [int]$wait = 20) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $script:procId))) {
      if ($w.Current.Name -like $titleLike) { return $w }
      foreach ($c in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))) { if ($c.Current.Name -like $titleLike) { return $c } } }
    Start-Sleep -Milliseconds 250 }
  throw "window not found: $titleLike" }
function WaitText($root, $like, [int]$wait = 20) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $t = Texts $root $like; if ($t.Count -gt 0) { return $t }; Start-Sleep -Milliseconds 250 }
  return @() }
function ById($root, $id) { $root.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty $id)) }
# A MessageBox reached through its WPF owner lists its controls as bare panes: find them by name, press them with BM_CLICK.
Add-Type 'using System; using System.Runtime.InteropServices; public static class W2 { [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }'
function Named($root, $like) { @($root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $like }) }
function Press($el) { [W2]::PostMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }


# ---- Saved runs: a run is kept when its orchestrator is detached and when AgentHub closes, listed in Runs, resumed on a session, deleted ----
$T = 'C:\data\agenthub\test-saved-runs'
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Pane Test'; New-Item -ItemType Directory -Force "$proj\orchestration" | Out-Null
[IO.File]::WriteAllText("$proj\orchestration\staged-build.json", @'
{
  "version": 1,
  "name": "Staged build",
  "stages": [
    {
      "id": "stage-1",
      "title": "Stage 1",
      "steps": [
        { "id": "s1-build", "kind": "instruct", "title": "Build stage 1", "text": "echo building stage 1" },
        { "id": "s1-watch", "kind": "wait-for-worker", "escalate": true },
        { "id": "s1-commit", "kind": "commit" }
      ]
    }
  ]
}
'@)
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings.Repositories = @(@{ Id = 'panetest'; Name = 'Pane Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
$fake = Join-Path $T 'fake-orchestrator.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000004"",""structured_output"":{""to_owner"":null,""to_worker"":""echo step"",""note"":""Scripted run orchestrator"",""step_outcome"":""continuing""}}`r`n")

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
function Launch {
  $script:p = Start-Process $Exe -PassThru; $script:procId = $script:p.Id
  $m = $null
  for ($i = 0; $i -lt 80 -and -not $m; $i++) { $m = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $script:p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3
  return $m }
# A pane restored after a restart already has its repository: only the shell is left to pick.
function StartSession($main) { $repo = @(Buttons $main | Where-Object { $_.Current.Name -eq 'Pane Test' }); if ($repo.Count -gt 0) { Click $repo[0]; Start-Sleep 1 }; Click (FindName $main 'Command Prompt'); Start-Sleep 6 }
function RunFile { @(Get-ChildItem "$T\runs" -Filter run.json -Recurse -ErrorAction SilentlyContinue) }
function RunJson { Get-Content (RunFile)[0].FullName -Raw | ConvertFrom-Json }

try {
  "1. A run is on disk from the moment it is followed, and Runs lists it as followed on its pane"
  $main = Launch
  Check ((FindName $main '*Runs' 5).Current.Name -like '*Runs') 'the header has a Runs button'
  StartSession $main
  Click (FindName $main '*Orchestrate' 20)
  Click (MenuItem '*Follow: Staged build*' 30)
  $tree = Win '*Advanced Templates*'
  Start-Sleep 2
  Check ((RunFile).Count -eq 1) 'run.json is written when the orchestrator is attached'
  Check ((RunJson).state -eq 'ready') 'as ready: not started'
  Click (ById $tree 'GoBtn')
  Check ((WaitText $tree '*NOW').Count -eq 1) 'Go: step 1 is under way'
  Start-Sleep 1
  Check ((RunJson).state -eq 'running' -and (RunJson).currentStepId -eq 's1-build') 'the record says running at s1-build'
  Click (FindName $main '*Runs')
  $runs = Win 'AgentHub - Runs'
  Start-Sleep 1
  Check ((WaitText $runs 'FOLLOWED ON A PANE NOW*1').Count -eq 1) 'Runs lists it under followed on a pane now'
  Check ((Texts $runs 'RUNNING*Terminal 1').Count -eq 1) 'as RUNNING on Terminal 1'
  Check ((Texts $runs 'step 1 of 3*Stage 1*Build stage 1').Count -eq 1) 'with where it stands'
  Check (@(Buttons $runs | Where-Object { $_.Current.Name -like 'Resume*' -or $_.Current.Name -like 'Delete*' }).Count -eq 0) 'a followed run is neither resumed nor deleted from here'
  Shot $runs 'runs-live.png'

  "2. Detach: the run stays, paused"
  Click (FindName $main '*Detach'); Start-Sleep 2
  Check ((WaitText $runs 'PAUSED*1').Count -eq 1) 'Runs lists it under paused'
  Check ((Texts $runs 'The orchestrator was taken off the session.').Count -eq 1) 'saying why it stopped'
  Check ((RunJson).state -eq 'paused') 'the record says paused'
  Shot $runs 'runs-paused.png'

  "3. AgentHub closed and started again: the run is still there, and needs a session to resume on"
  Stop-Process -Id $script:p.Id -Force; Start-Sleep 2
  $main = Launch
  Click (FindName $main '*Runs')
  $runs = Win 'AgentHub - Runs'
  Check ((WaitText $runs 'PAUSED*1').Count -eq 1) 'the paused run is listed after a restart'
  Click (FindName $runs 'Resume Staged build')
  $none = Win 'Resume run'
  Check ((Named $none '*No session to resume this run on*').Count -eq 1) 'with no session running, Resume says to start one'
  Press (Named $none 'OK')[0]; Start-Sleep 1

  "4. Resume on a new session: the run carries on at its step, on the same record"
  StartSession $main
  Click (FindName $runs 'Resume Staged build')
  $target = MenuItem 'On Terminal 1*' 20
  Check ($null -ne $target) 'Resume offers the running session'
  Click $target
  $tree = Win '*Advanced Templates*'
  Check ((WaitText $tree 'RUNNING').Count -eq 1) 'the tree opens with the run running'
  Check ((WaitText $tree 'Terminal 1*step 1 of 3*').Count -eq 1) 'at the step it stood at'
  Check ((WaitText $main '*Orchestrator*worker').Count -eq 1) 'the orchestrator instructs the session'
  Check ((WaitText $runs 'FOLLOWED ON A PANE NOW*1').Count -eq 1) 'Runs lists it as followed again'
  Check ((RunFile).Count -eq 1) 'still one record: the same run'
  Check ((RunJson).state -eq 'running') 'which says running again'
  Shot $runs 'runs-resumed.png'

  "5. The Orchestrate menu offers a paused run of the project too"
  Click (FindName $main '*Detach'); Start-Sleep 2
  Click (FindName $main '*Orchestrate' 20)
  $resume = MenuItem '*Resume run: Staged build*step 1 of 3*' 30
  Check ($null -ne $resume) 'Resume run: Staged build is in the menu, with where it stands'
  Click (MenuItem 'Orchestrator with no preset context'); Start-Sleep 2
  Click (FindName $main '*Detach'); Start-Sleep 1

  "6. Delete forgets the run, after asking"
  Click (FindName $runs 'Delete Staged build')
  $ask = Win 'Delete run'
  Check ((Named $ask '*Only the run*s record goes*').Count -eq 1) 'the confirmation says what goes and what stays'
  Press (Named $ask 'OK')[0]
  Check ((WaitText $runs 'No runs yet*').Count -eq 1) 'Runs is empty'
  Check ((RunFile).Count -eq 0) 'and the record is gone from disk'
  Check (Test-Path "$proj\orchestration\staged-build.json") 'the project and its template are untouched'
  Shot $runs 'runs-empty.png'
} finally {
  Stop-Process -Id $script:p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
