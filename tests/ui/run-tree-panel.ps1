# UI test: the run tree beside a pane (🌲 Tree in the orchestrator box): shown and hidden, done green, current blue, steps opened.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
'@
# PrintWindow misses the main window's WPF content beside the terminal's WebView2: take what is on the screen instead.
# A locked screen has nothing to take: say so and go on (the unit test RunTreePanelTests draws the tree off screen).
function Grab($win, $file) {
  if (Get-Process LogonUI -ErrorAction SilentlyContinue) { "  SKIP  screenshot $file (the screen is locked)"; return }
  [W]::SetForegroundWindow([IntPtr]$win.Current.NativeWindowHandle) | Out-Null; Start-Sleep -Milliseconds 600
  $r = $win.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$r.Left, [int]$r.Top, 0, 0, $bmp.Size); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
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

# ---- A run of an advanced template on a pane: Follow in the Orchestrate menu, Go, Pause, go on from a step, Detach ----
# A scripted orchestrator (AGENTHUB_ORCHESTRATOR_COMMAND) that words "echo step" for every step and never calls one done,
# and a cmd.exe session: no Claude usage. The project folder has a space in its path.
$T = 'C:\data\agenthub\test-advanced-run'
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

# ---- The run tree beside a pane: 🌲 Tree in the orchestrator's box shows and hides it; done green, current blue ----
$T = 'C:\data\agenthub\test-run-tree'
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Pane Test'; New-Item -ItemType Directory -Force "$proj\orchestration" | Out-Null
[IO.File]::WriteAllText("$proj\orchestration\staged-build.json", @'
{
  "version": 1,
  "name": "Staged build",
  "description": "Three stages, to see the run tree scroll.",
  "stages": [
    { "id": "stage-1", "title": "Stage 1: data model", "steps": [
        { "id": "s1-branch", "kind": "create-branch", "branch": "feature/data-model" },
        { "id": "s1-build", "kind": "instruct", "title": "Build stage 1", "text": "echo building stage 1 of the plan, with tests, and nothing of stage 2" },
        { "id": "s1-watch", "kind": "wait-for-worker", "escalate": true },
        { "id": "s1-handoff", "kind": "handoff" },
        { "id": "s1-commit", "kind": "commit" } ] },
    { "id": "stage-2", "title": "Stage 2: screens", "steps": [
        { "id": "s2-new", "kind": "new-session" },
        { "id": "s2-build", "kind": "instruct", "title": "Build stage 2", "text": "echo building stage 2" },
        { "id": "s2-watch", "kind": "wait-for-worker", "escalate": true },
        { "id": "s2-check", "kind": "ask-owner", "text": "Stage 2 is built. Open the pull request?" },
        { "id": "s2-pr", "kind": "open-pull-request", "target": "main" } ] },
    { "id": "stage-3", "title": "Stage 3: polish", "steps": [
        { "id": "s3-build", "kind": "instruct", "title": "Polish", "text": "echo polishing" },
        { "id": "s3-watch", "kind": "wait-for-worker" },
        { "id": "s3-commit", "kind": "commit" },
        { "id": "s3-pr", "kind": "open-pull-request" } ] }
  ]
}
'@)
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings.Repositories = @(@{ Id = 'panetest'; Name = 'Pane Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
# A scripted orchestrator that never calls a step done, and a cmd.exe session: no Claude usage. The folder has a space in its path.
$fake = Join-Path $T 'fake-orchestrator.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000004"",""structured_output"":{""to_owner"":null,""to_worker"":null,""note"":""Scripted run orchestrator"",""step_outcome"":""continuing""}}`r`n")

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3

  "1. Follow the template on a session: 🌲 Tree is offered, the tree is hidden"
  Click (FindName $main 'Pane Test'); Start-Sleep 1
  Click (FindName $main 'Command Prompt'); Start-Sleep 6
  Click (FindName $main '*Orchestrate' 20)
  Click (MenuItem '*Follow: Staged build*' 30)
  $tree = Win '*Advanced Templates*'
  Start-Sleep 2
  $toggle = FindName $main 'Show the run tree' 10
  Check ($null -ne $toggle) '🌲 Tree is in the orchestrator box'
  Check ($null -eq (ById $main 'RunTreePanel')) 'the run tree is hidden until asked for'

  "2. 🌲 Tree shows it beside the session: every stage, every step to do"
  Click $toggle; Start-Sleep 1
  $panel = ById $main 'RunTreePanel'
  Check ($null -ne $panel) 'the run tree is shown'
  Check ((Texts $panel '0 of 14 steps done').Count -eq 1) 'with how far the run has got'
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Stage: *' }).Count -eq 3) 'three stages'
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Step: * · to do' }).Count -eq 14) 'and fourteen steps, all to do'
  $pb = $panel.Current.BoundingRectangle; $sb = (FindName $main '*Repo context').Current.BoundingRectangle
  Check ($pb.Left -ge $sb.Right -and [math]::Abs($pb.Top - $sb.Top) -lt 60) 'to the right of the session, level with its header'
  Check ($null -ne (FindName $main 'Hide the run tree' 5)) 'the button now hides it'

  "3. Go: the step the run is at is current, and so is its stage"
  Click (ById $tree 'GoBtn')
  Check ((WaitText $panel '0 of 14 steps done').Count -eq 1) 'nothing done yet'
  for ($i = 0; $i -lt 40 -and -not (Buttons $panel | Where-Object { $_.Current.Name -like 'Step: Create branch*· now' })) { Start-Sleep -Milliseconds 250 }
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Step: *· now' }).Count -eq 1) 'one step is current'
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Stage: Stage 1*· now' }).Count -eq 1) 'and so is its stage'

  "4. Go on from a later step: the steps between are skipped, done steps stay, the tree follows"
  Click (ById $tree 'PauseBtn'); Start-Sleep 1
  Click (FindName $tree 'Go on from: Polish')
  $ask = Win 'Go on from this step'
  Press (Named $ask 'OK')[0]
  for ($i = 0; $i -lt 40 -and -not (Buttons $panel | Where-Object { $_.Current.Name -like 'Step: Polish · now' })) { Start-Sleep -Milliseconds 250 }
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Step: Polish · now' }).Count -eq 1) 'Polish is current'
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Step: * · skipped' }).Count -eq 10) 'the ten steps before it are skipped'
  Check ((Buttons $panel | Where-Object { $_.Current.Name -like 'Stage: Stage 3*· now' }).Count -eq 1) 'its stage is current'
  Start-Sleep 1
  Grab $main 'run-tree-panel.png'

  "5. A click on a step opens its details; a second click closes them"
  $build = Buttons $panel | Where-Object { $_.Current.Name -like 'Step: Build stage 1*' } | Select-Object -First 1
  Click $build; Start-Sleep -Milliseconds 500
  Check ((Texts $panel 'INSTRUCT WORKER · SKIPPED').Count -eq 1) 'its kind and state'
  Check ((Texts $panel 'echo building stage 1 of the plan*').Count -eq 1) 'and its text'
  $watch = Buttons $panel | Where-Object { $_.Current.Name -like 'Step: Wait for the worker*' } | Select-Object -First 1
  Click $watch; Start-Sleep -Milliseconds 500
  Check ((Texts $panel '*pauses for you').Count -eq 1) 'an escalation point says it pauses for you'
  Grab $main 'run-tree-open.png'
  Click $build; Start-Sleep -Milliseconds 500
  Check ((Texts $panel 'echo building stage 1 of the plan*').Count -eq 0) 'a second click closes it'

  "6. ✕ in the tree hides it; 🌲 Tree brings it back; Detach takes it away"
  Click (FindName $panel 'Hide the run tree'); Start-Sleep -Milliseconds 500
  Check ($null -eq (ById $main 'RunTreePanel')) '✕ hides the tree'
  Click (FindName $main 'Show the run tree'); Start-Sleep -Milliseconds 500
  Check ($null -ne (ById $main 'RunTreePanel')) '🌲 Tree shows it again'
  Click (FindName $main '*Detach'); Start-Sleep 2
  Check ($null -eq (ById $main 'RunTreePanel')) 'detached, the tree is gone'
  Check ((Buttons $main | Where-Object { $_.Current.Name -like '*run tree' }).Count -eq 0) 'and so is its button'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-10) -and $_.MainWindowTitle -eq '' -and $_.Parent.Id -eq $p.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
