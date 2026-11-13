# UI test: an orchestrator follows an advanced template on a pane (Follow, Go, Pause, go on from a step, Detach).
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

Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Pane Test'; New-Item -ItemType Directory -Force "$proj\orchestration" | Out-Null
[IO.File]::WriteAllText("$proj\orchestration\staged-build.json", @'
{
  "version": 1,
  "name": "Staged build",
  "description": "One stage, to try a run on.",
  "stages": [
    {
      "id": "stage-1",
      "title": "Stage 1",
      "steps": [
        { "id": "s1-build", "kind": "instruct", "title": "Build stage 1", "text": "echo building stage 1" },
        { "id": "s1-watch", "kind": "wait-for-worker", "escalate": true },
        { "id": "s1-check", "kind": "ask-owner", "text": "Stage 1 is built. Commit it?" },
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
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000003"",""structured_output"":{""to_owner"":null,""to_worker"":""echo step"",""note"":""Scripted run orchestrator"",""step_outcome"":""continuing""}}`r`n")

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3

  "1. The Orchestrate menu offers the project's template to follow"
  Click (FindName $main 'Pane Test'); Start-Sleep 1
  Click (FindName $main 'Command Prompt'); Start-Sleep 6
  $btn = FindName $main '*Orchestrate' 20
  Click $btn
  $item = MenuItem '*Follow: Staged build (1 stage*4 steps)' 30
  Check ($null -ne $item) 'Follow: Staged build is in the menu, with its size'
  Start-Sleep -Milliseconds 600
  $r = $btn.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap 620, 300; $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]($r.Left - 420), [int]($r.Top - 10), 0, 0, $bmp.Size); $bmp.Save((Join-Path $Shots 'run-menu.png')); $g.Dispose(); $bmp.Dispose()
  Click $item

  "2. The orchestrator is on, the tree opens, and nothing happens until Go"
  $tree = Win '*Advanced Templates*'
  Start-Sleep 2
  Check ((Texts $main 'RUN READY').Count -eq 2) 'the box on the pane and the tree both say RUN READY'   # the tree is an owned window: its texts are the main window''s too
  Check ((Texts $main '*Staged build').Count -ge 1) 'and is titled after the template'
  Check ($null -ne (FindName $main '*Run' 5)) 'and offers its tree again'
  Check ((Texts $tree 'RUN READY').Count -eq 1) 'one of them in the tree''s run bar'
  Check ((Texts $tree 'Terminal 1*4 steps, not started').Count -eq 1) 'naming the pane and where the run stands'
  Check ($null -ne (ById $tree 'GoBtn')) 'with Go'
  Check ($null -eq (ById $tree 'PauseBtn')) 'and no Pause yet'
  Check ((Texts $tree '*followed on Terminal 1').Count -eq 1) 'the list says which pane follows it'
  Check ((Texts $main '*Orchestrator*worker').Count -eq 0) 'nothing was sent to the session'
  Shot $tree 'run-ready.png'

  "3. Go: the first step is the current one and its instruction reaches the session"
  Click (ById $tree 'GoBtn')
  $now = WaitText $tree '*NOW'
  Check ($now.Count -eq 1) 'one step is marked NOW'
  Check ((WaitText $tree 'RUNNING').Count -eq 1) 'the bar says RUNNING'
  Check ((WaitText $tree 'Terminal 1*step 1 of 4*Stage 1*Build stage 1').Count -eq 1) 'at step 1 of 4'
  Check ((WaitText $main '*Orchestrator*worker').Count -eq 1) 'the orchestrator instructed the session'
  Check ($null -ne (ById $tree 'PauseBtn')) 'Pause is offered'
  Check ($null -eq (ById $tree 'GoBtn')) 'and Go is not, while it runs'
  Shot $tree 'run-now.png'

  "4. Pause stops it at that step"
  Click (ById $tree 'PauseBtn')
  Check ((WaitText $tree 'RUN PAUSED').Count -eq 1) 'the bar says RUN PAUSED'
  Check ((WaitText $tree '*PAUSED HERE').Count -eq 1) 'the step says PAUSED HERE'
  Check ((WaitText $tree 'Paused by you.').Count -eq 1) 'with the reason'
  Check ($null -ne (ById $tree 'GoBtn')) 'Go is back'
  Check ((WaitText $main 'RUN PAUSED').Count -eq 2) 'the box on the pane says so too'
  Shot $tree 'run-paused.png'

  "5. Go on from a later step: asked first, the steps between are skipped, and its question is put to you"
  Click (FindName $tree 'Go on from: Stage 1 is built*')
  $ask = Win 'Go on from this step'
  Check ((Named $ask '*The 2 steps before it that are not done are skipped*').Count -eq 1) 'the confirmation says what is skipped'
  Shot $ask 'run-from-here.png'
  Press (Named $ask 'OK')[0]
  Check ((WaitText $tree 'RUN WAITING FOR YOU').Count -eq 1) 'the bar says RUN WAITING FOR YOU'
  Check ((WaitText $tree 'SKIPPED').Count -eq 2) 'two steps are SKIPPED'
  Check ((WaitText $tree '*WAITING FOR YOU' | Where-Object { $_.Current.Name -notlike 'RUN*' }).Count -eq 1) 'the question step waits for you'
  Check ((WaitText $main '*Orchestrator asks you').Count -eq 1) 'the question is in the box on the pane'
  Check ((Texts $main 'Stage 1 is built. Commit it?').Count -ge 2) 'in its own words, as on the tree'
  Shot $tree 'run-question.png'
  Shot $main 'run-main.png'

  "6. Detach: the tree is a plain template again"
  Click (FindName $main '*Detach'); Start-Sleep 2
  Check ((Texts $tree 'RUN*').Count -eq 0) 'no run bar after detach'
  Check ((Texts $tree '*followed on*').Count -eq 0) 'and the list no longer names a pane'
  Check ((Texts $tree 'Build stage 1').Count -eq 1) 'the template is still shown'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-10) -and $_.MainWindowTitle -eq '' -and $_.Parent.Id -eq $p.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
