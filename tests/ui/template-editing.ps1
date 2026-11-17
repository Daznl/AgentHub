# UI test for editing advanced templates (stage 5): edit through a draft, problems stop a save, add / move / remove
# steps, save, keep a draft, discard it; then, with a run paused on a pane, the steps it has reached are locked and a
# saved change is applied to it. UI Automation patterns only (no mouse, no keyboard, no focus stealing); a cmd.exe session
# and a scripted orchestrator, so no Claude usage. Project folders have spaces in their paths. Screenshots go to
# C:\data\agenthub\uitest. Kept ASCII: Windows PowerShell 5.1 reads a script without a byte order mark as ANSI.
param([string]$Exe = 'C:\data\agenthub\build-test\AgentHub.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public struct RECT { public int L, T, R, B; }
}
'@
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern; $Val = [System.Windows.Automation.ValuePattern]::Pattern
$Shots = 'C:\data\agenthub\uitest'
New-Item -ItemType Directory -Force $Shots | Out-Null
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Buttons($root) { $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) }
function FindName($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { foreach ($e in (Buttons $root)) { if ($e.Current.Name -like $like) { return $e } }; Start-Sleep -Milliseconds 250 }
  throw "not found by name: $like" }
function HasButton($root, $like) { @(Buttons $root | Where-Object { $_.Current.Name -like $like }).Count -gt 0 }
function Texts($root, $like) { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like $like }) }
function WaitText($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $t = Texts $root $like; if ($t.Count -gt 0) { return $t }; Start-Sleep -Milliseconds 250 }
  return @() }
function ById($root, $id) { $root.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty $id)) }
function Edit($root, $nameLike, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($e in $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))) { if ($e.Current.Name -like $nameLike) { return $e } }
    Start-Sleep -Milliseconds 250 }
  throw "edit not found: $nameLike" }
function SetText($el, $text) { $el.GetCurrentPattern($Val).SetValue($text) }
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
function Click($el) { $el.GetCurrentPattern($Invoke).Invoke() }
function Win($titleLike, [int]$wait = 20) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $script:procId))) {
      if ($w.Current.Name -like $titleLike) { return $w }
      foreach ($c in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))) { if ($c.Current.Name -like $titleLike) { return $c } } }
    Start-Sleep -Milliseconds 250 }
  throw "window not found: $titleLike" }
# A MessageBox reached through its WPF owner lists its controls as bare panes: find them by name, press them with BM_CLICK.
function Named($root, $like) { @($root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $like }) }
function Press($el) { [W]::PostMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
function Settings($T, $repos) {
  $settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
  $settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
  if ($repos) { $settings.Repositories = $repos }
  [IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20)) }

# ======== Part A: editing in the window, no run ========
$T = 'C:\data\agenthub\test-template-editing'
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$Project = "$T\My Project"
New-Item -ItemType Directory -Force $Project | Out-Null
Settings $T $null
$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$p = Start-Process $Exe -ArgumentList '--advanced-templates', "`"$Project`"" -PassThru; $script:procId = $p.Id
$file = "$Project\orchestration\staged-build.json"; $draftFile = "$Project\orchestration\staged-build.draft.json"
try {
  $w = Win '*Advanced Templates*'
  Start-Sleep 2
  Click (ById $w 'AddExampleBtn'); Start-Sleep 2
  $original = Get-Content $file -Raw

  "A1. Edit opens the draft: nothing is written until something changes"
  Click (FindName $w '*Edit'); Start-Sleep 1
  Check ((WaitText $w '*EDITING').Count -eq 1) 'the edit bar is shown'
  Check ((Texts $w 'No change yet*').Count -eq 1) 'and says nothing has changed'
  Check (-not (Test-Path $draftFile)) 'no draft file yet'
  Check (HasButton $w 'Move up: Build stage 1') 'each step has its tools'
  Check (HasButton $w 'Add a step to: Stage 2') 'each stage can be given a step'

  "A2. A step left without what it needs is a problem, and the draft cannot be saved"
  Click (FindName $w 'Edit: Build stage 1'); Start-Sleep 1
  $instruction = Edit $w 'Instruction'
  SetText $instruction ''
  Start-Sleep 2
  Check ((WaitText $w '*needs text*').Count -ge 1) 'the missing text is named'
  Check (-not (ById $w 'SaveDraftBtn').Current.IsEnabled) 'Save is disabled'
  Check (Test-Path $draftFile) 'the change went to the draft'
  Check ((Get-Content $file -Raw) -eq $original) 'the template itself is untouched'
  Shot $w 'editing-problem.png'
  SetText $instruction 'Build stage 1 of docs/PLAN.md, with tests and a changelog entry.'
  Start-Sleep 2
  Check ((Texts $w '*needs text*').Count -eq 0) 'fixed: the problem is gone'
  Check ((ById $w 'SaveDraftBtn').Current.IsEnabled) 'and Save is enabled'

  "A3. A new step is added, filled in and moved up"
  Click (FindName $w 'Add a step to: Stage 2'); Start-Sleep 1
  SetText (Edit $w 'Instruction') 'Run the linter and fix what it reports.'
  Start-Sleep 1
  Check ((WaitText $w 'Run the linter and fix what it reports.').Count -ge 1) 'the tree shows the new step by its text'
  Click (FindName $w 'Close the editor'); Start-Sleep 1
  Click (FindName $w 'Move up: Run the linter*'); Start-Sleep 2
  $draft = Get-Content $draftFile -Raw | ConvertFrom-Json
  $stage2 = @($draft.stages[1].steps)
  Check ($stage2.Count -eq 9 -and $stage2[7].text -like 'Run the linter*' -and $stage2[8].kind -eq 'open-pull-request') 'the draft has it just before the pull request'
  Check ($stage2[7].id -eq 'stage-2-step-1') 'with an id of its own'
  Shot $w 'editing.png'

  "A4. Save puts the draft in the template's place"
  Click (ById $w 'SaveDraftBtn'); Start-Sleep 2
  $saved = Get-Content $file -Raw
  Check ($saved -match 'Run the linter' -and $saved -match 'changelog entry') 'the file has both changes'
  Check (-not (Test-Path $draftFile)) 'the draft is gone'
  Check ((Texts $w '*EDITING').Count -eq 0) 'editing has ended'
  Check ((WaitText $w 'Saved *').Count -eq 1) 'the status line says it was saved'
  Check ((Texts $w '2 stages*16 steps').Count -ge 1) 'the tree has the new size'
  Check ((Get-Content "$Project\orchestration\.gitignore" -Raw) -match '\*\.draft\.json') 'drafts are kept out of commits'

  "A5. Done keeps a draft, which is offered again; Discard drops it"
  Click (FindName $w '*Edit'); Start-Sleep 1
  Click (FindName $w 'Remove: Run the linter*'); Start-Sleep 1
  Click (ById $w 'DoneEditingBtn'); Start-Sleep 2
  Check (Test-Path $draftFile) 'the draft is kept'
  Check ((WaitText $w '*waiting as a draft*').Count -eq 1) 'the view says a draft is waiting'
  Check ((Texts $w 'Run the linter and fix what it reports.').Count -ge 1) 'and still shows the saved template'
  Click (FindName $w '*Open the draft'); Start-Sleep 1
  Check ((Texts $w 'Run the linter and fix what it reports.').Count -eq 0) 'the draft is shown, without the removed step'
  Click (ById $w 'DiscardDraftBtn')
  $ask = Win 'Discard the draft'
  Press (Named $ask 'OK')[0]; Start-Sleep 2
  Check (-not (Test-Path $draftFile)) 'Discard removed the draft'
  Check ((Get-Content $file -Raw) -match 'Run the linter') 'and left the template as saved'
  Check ((Texts $w 'Run the linter and fix what it reports.').Count -ge 1) 'which is shown again'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

# ======== Part B: a paused run on a pane is given a saved change ========
$T = 'C:\data\agenthub\test-template-editing-run'
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Pane Test'; New-Item -ItemType Directory -Force "$proj\orchestration" | Out-Null
$runFile = "$proj\orchestration\staged-build.json"
[IO.File]::WriteAllText($runFile, @'
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
        { "id": "s1-check", "kind": "ask-owner", "text": "Stage 1 is built. Commit it?" },
        { "id": "s1-commit", "kind": "commit" }
      ]
    }
  ]
}
'@)
Settings $T @(@{ Id = 'panetest'; Name = 'Pane Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
$fake = Join-Path $T 'fake-orchestrator.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000005"",""structured_output"":{""to_owner"":null,""to_worker"":""echo step"",""note"":""Scripted run orchestrator"",""step_outcome"":""continuing""}}`r`n")
$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3
  Click (FindName $main 'Pane Test'); Start-Sleep 1
  Click (FindName $main 'Command Prompt'); Start-Sleep 6
  Click (FindName $main '*Orchestrate' 20)
  for ($i = 0; $i -lt 120; $i++) {
    $item = $null
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $p.Id))) {
      foreach ($m in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem)))) { if ($m.Current.Name -like '*Follow: Staged build*') { $item = $m } } }
    if ($item) { break }; Start-Sleep -Milliseconds 250 }
  Click $item
  $tree = Win '*Advanced Templates*'
  Start-Sleep 2
  Click (ById $tree 'GoBtn')
  Check ((WaitText $tree 'RUNNING').Count -eq 1) 'B0. the run is going'
  Start-Sleep 2
  Click (ById $tree 'PauseBtn')
  Check ((WaitText $tree 'RUN PAUSED').Count -eq 1) '    and is paused at its first step'

  "B1. Editing while the run is paused: the step it is at is locked, the others are not"
  Click (FindName $tree '*Edit'); Start-Sleep 1
  Check (-not (HasButton $tree 'Edit: Build stage 1')) 'the step the run is at cannot be edited'
  Check (-not (HasButton $tree 'Remove: Build stage 1')) 'nor removed'
  Check (HasButton $tree 'Edit: Stage 1 is built*') 'a step not yet started can'
  Check (-not (FindName $tree 'Move up: Wait for the worker*').Current.IsEnabled) 'and cannot be moved above the step the run is at'
  Check ((WaitText $tree '*has reached 1 step*').Count -eq 1) 'the view says what is locked'

  "B2. A step is added and saved; the run keeps its version until Apply"
  Click (FindName $tree 'Add a step to: Stage 1'); Start-Sleep 1
  SetText (Edit $tree 'Instruction') 'echo lint'
  Start-Sleep 1
  Click (ById $tree 'SaveDraftBtn'); Start-Sleep 2
  Check ((Get-Content $runFile -Raw) -match 'echo lint') 'the template is saved'
  Check ((WaitText $tree '*still follows the version it was given*').Count -eq 1) 'the status line says the run has not changed'
  Check ((Texts $tree 'Terminal 1*step 1 of 4*').Count -eq 1) 'the run is still at step 1 of 4'
  $apply = ById $tree 'ApplyBtn'
  Check ($null -ne $apply -and $apply.Current.IsEnabled) 'Apply to this run is offered'
  Shot $tree 'editing-run-saved.png'

  "B3. Apply: asked first, then the run follows the saved template"
  Click $apply
  $ask = Win 'Apply to this run'
  Check ((Named $ask '*1 step added*').Count -eq 1) 'the confirmation says what changes'
  Press (Named $ask 'OK')[0]; Start-Sleep 2
  Check ((WaitText $tree 'Terminal 1*step 1 of 5*').Count -eq 1) 'the run has 5 steps now, still at step 1'
  Check ((Texts $tree 'echo lint').Count -ge 1) 'the tree shows the new step'
  Check ($null -eq (ById $tree 'ApplyBtn')) 'Apply is gone: the run follows the saved version'
  Check ((Texts $tree '*PAUSED HERE').Count -eq 1) 'and it is still paused where it was'
  Check ((WaitText $main '*template was changed*').Count -ge 1) 'the orchestrator box notes the change'
  $record = Get-ChildItem "$T\runs" -Recurse -Filter run.json | Select-Object -First 1
  Check ($record -and ((Get-Content $record.FullName -Raw) -match 'echo lint')) 'the saved run has the new template'
  Shot $tree 'editing-run-applied.png'

  "B4. A change to the step the run has reached is refused"
  $json = Get-Content $runFile -Raw
  [IO.File]::WriteAllText($runFile, $json.Replace('echo building stage 1', 'echo building something else'))
  Click (ById $tree 'RefreshBtn'); Start-Sleep 2
  Click (ById $tree 'ApplyBtn')
  $ask = Win 'Apply to this run'
  Check ((Named $ask '*was changed, but the run has already reached it*').Count -eq 1) 'Apply says why it cannot'
  Shot $ask 'editing-run-refused.png'
  Press (Named $ask 'OK')[0]; Start-Sleep 1
  Check ((Texts $tree 'Terminal 1*step 1 of 5*').Count -eq 1) 'and the run is as it was'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.StartTime -gt (Get-Date).AddMinutes(-10) -and $_.MainWindowTitle -eq '' -and $_.Parent.Id -eq $p.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
