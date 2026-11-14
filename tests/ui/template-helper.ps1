# UI test for the Template helper (stage 6): the crab in the Advanced templates window builds a template from a plan
# file and changes the one on screen, always into a draft that opens in the editor; a draft with problems is shown but
# cannot be saved; answers and failures write nothing; a change is not written over the owner's own edits made while it
# worked; and a template a run follows is changed as a draft the run then takes with Apply. UI Automation patterns only
# (no mouse, no keyboard, no focus stealing); a scripted helper (fake-template-helper.ps1) and a scripted orchestrator,
# so no Claude usage. With -Live, only the real helper instead (Claude Code on your login; uses model usage): it builds
# a template from a plan and changes it, through the window. Project folders have spaces in their paths. Screenshots go
# to C:\data\agenthub\uitest. Kept ASCII: Windows PowerShell 5.1 reads a script without a byte order mark as ANSI.
param([string]$Exe = 'C:\data\agenthub\build-test\AgentHub.exe', [switch]$Live)
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
function WaitText($root, $like, [int]$wait = 15) {
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
function Named($root, $like) { @($root.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like $like }) }
function Press($el) { [W]::PostMessage([IntPtr]$el.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
function Settings($T, $repos) {
  $settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
  $settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
  if ($repos) { $settings.Repositories = $repos }
  [IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20)) }
# The scripted helper: a .cmd that hands stdin to fake-template-helper.ps1, which logs each request to $log.
function FakeHelper($T) {
  $log = Join-Path $T 'helper requests'
  $cmd = Join-Path $T 'fake helper.cmd'
  [IO.File]::WriteAllText($cmd, "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\fake-template-helper.ps1`" -Log `"$log`"`r`n")
  $env:AGENTHUB_TEMPLATE_HELPER_COMMAND = $cmd
  return $log }
function LastRequest($log) { $f = Get-ChildItem $log -Filter 'request-*.txt' | Sort-Object Name | Select-Object -Last 1; if ($f) { Get-Content $f.FullName -Raw } else { '' } }
function RequestCount($log) { @(Get-ChildItem $log -Filter 'request-*.txt' -ErrorAction SilentlyContinue).Count }
# Asks the helper as the owner would: types the request into its box and presses Ask.
function AskHelper($w, $text) { SetText (Edit $w 'Question') $text; Start-Sleep -Milliseconds 300; Click (FindName $w 'Ask') }
# Waits for the helper to finish: its Ask button says Ask again (not Stop).
function WaitHelper($w, [int]$wait = 40) {
  Start-Sleep -Milliseconds 600
  for ($i = 0; $i -lt $wait * 4; $i++) { if (HasButton $w 'Ask') { Start-Sleep -Milliseconds 400; return }; Start-Sleep -Milliseconds 250 }
  throw 'the helper did not finish' }

# ======== -Live: the real helper (Claude Code on your login; uses model usage), through the window ========
if ($Live) {
  $T = 'C:\data\agenthub\test-template-helper-live'
  Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
  $Project = "$T\Recipe Box"
  New-Item -ItemType Directory -Force "$Project\docs" | Out-Null
  [IO.File]::WriteAllText("$Project\docs\PLAN.md", "# Recipe box: plan`n`n## Stage 1: storage`nA SQLite file of recipes (title, ingredients, steps) with a repository class and its tests.`n`n## Stage 2: command line`n``recipes add``, ``recipes list`` and ``recipes show <id>`` on the repository, with tests.`n`n## Stage 3: search`n``recipes find <word>`` over titles and ingredients, with tests.`n`nWorkflow: a branch per stage; one pull request into main after the last stage.`n")
  Settings $T $null
  Remove-Item Env:\AGENTHUB_TEMPLATE_HELPER_COMMAND -ErrorAction SilentlyContinue
  $env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"
  $p = Start-Process $Exe -ArgumentList '--advanced-templates', "`"$Project`"" -PassThru; $script:procId = $p.Id
  try {
    $w = Win '*Advanced Templates*'
    Start-Sleep 2
    "L1. The real helper builds a template from the plan"
    Click (FindName $w 'Template helper'); Start-Sleep 1
    Click (FindName $w 'Build a template from docs/PLAN.md'); WaitHelper $w 300
    $drafts = @(Get-ChildItem "$Project\orchestration" -Filter '*.draft.json' -ErrorAction SilentlyContinue)
    Check ($drafts.Count -eq 1) "a draft is written ($($drafts.Name -join ', '))"
    Check ((Texts $w '*EDITING').Count -eq 1) 'it is open in the editor'
    Check ((ById $w 'SaveDraftBtn').Current.IsEnabled) 'it has no problems: it can be saved'
    $d = Get-Content $drafts[0].FullName -Raw | ConvertFrom-Json
    Check (@($d.stages).Count -eq 3) "three stages ($(@($d.stages).Count))"
    Shot $w 'helper-live-built.png'
    Click (ById $w 'SaveDraftBtn'); Start-Sleep 2

    "L2. The real helper changes it on request"
    AskHelper $w 'Add an ask-owner step before the pull request that asks me to look at the diff first.'; WaitHelper $w 300
    $drafts = @(Get-ChildItem "$Project\orchestration" -Filter '*.draft.json')
    Check ($drafts.Count -eq 1) 'the change is a draft'
    Check ((ById $w 'SaveDraftBtn').Current.IsEnabled) 'with no problems'
    $c = Get-Content $drafts[0].FullName -Raw | ConvertFrom-Json
    $saved = Get-Content ($drafts[0].FullName -replace '\.draft\.json$', '.json') -Raw | ConvertFrom-Json
    $kept = @($saved.stages | ForEach-Object { $_.steps } | ForEach-Object { $_.id })
    $now = @($c.stages | ForEach-Object { $_.steps } | ForEach-Object { $_.id })
    Check (@($kept | Where-Object { $now -notcontains $_ }).Count -eq 0) 'every step it kept keeps its id'
    Check (@($c.stages | ForEach-Object { $_.steps } | Where-Object { $_.kind -eq 'ask-owner' }).Count -ge 1) 'there is an ask-owner step'
    Shot $w 'helper-live-changed.png'
  } finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  }
  "Failures: $script:fails"
  exit $script:fails
}

# ======== Part A: building and changing templates ========
$T = 'C:\data\agenthub\test-template-helper'
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$Project = "$T\My Project"
New-Item -ItemType Directory -Force "$Project\docs" | Out-Null
[IO.File]::WriteAllText("$Project\docs\PLAN.md", "# Plan`n`n## Stage 1: data model`n`nThe tables.`n`n## Stage 2: screens`n`nThe pages.`n")
[IO.File]::WriteAllText("$Project\README.md", "# My Project`n")
Settings $T $null
$log = FakeHelper $T
$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$p = Start-Process $Exe -ArgumentList '--advanced-templates', "`"$Project`"" -PassThru; $script:procId = $p.Id
$file = "$Project\orchestration\plan-build.json"; $draftFile = "$Project\orchestration\plan-build.draft.json"
try {
  $w = Win '*Advanced Templates*'
  Start-Sleep 2

  "A1. The crab opens the Template helper, which offers the plan it found"
  $crab = FindName $w 'Template helper'
  Check ((WaitText $w '*Template helper (the crab at the top right) builds one*').Count -eq 1) 'the empty project points at the helper'
  Click $crab; Start-Sleep 1
  $pane = ById $w 'TemplateHelper'
  Check ($pane -and -not $pane.Current.IsOffscreen) 'the crab opens the helper pane'
  Check ($crab.Current.ItemStatus -eq 'Helper open') 'and the crab is lit'
  Check (HasButton $w 'Build a template from docs/PLAN.md') 'the plan file is offered'
  Check (-not (HasButton $w 'Build a template from README.md')) 'a README is not taken for a plan'
  Check ((Texts $w 'No template is picked*').Count -eq 1) 'it says a request builds a new template'
  Shot $w 'helper-open.png'

  "A2. Build from the plan: a new template that is only a draft, open in the editor"
  Click (FindName $w 'Build a template from docs/PLAN.md'); WaitHelper $w
  $req = LastRequest $log
  Check ($req -match 'Build an advanced template from docs/PLAN.md') 'the request names the plan'
  Check ($req -match '<plan_files>docs/PLAN.md' -and $req -match [regex]::Escape("<project folder=`"$Project`"")) 'and is given the plan files and the project folder'
  Check (Test-Path $draftFile) 'the draft is written'
  Check (-not (Test-Path $file)) 'the template itself is not'
  Check ((WaitText $w '*draft of Plan build is open in the editor*2 stages, 8 steps*').Count -ge 1) 'the crab says the draft is open, with its size'
  Check ((Texts $w '*EDITING').Count -eq 1) 'the editor is open on it'
  Check ((ById $w 'SaveDraftBtn').Current.IsEnabled) 'and it can be saved'
  Check ((Texts $w 'Build the data model').Count -ge 1) 'the tree shows its steps'
  Check (@(Named $w 'Plan build').Count -ge 1) 'it is listed on the left'
  Check ((Texts $w '*Changes go to Plan build (its draft)*').Count -eq 1) 'and the helper says changes go to it now'
  Shot $w 'helper-built.png'

  "A3. Save makes it a template"
  Click (ById $w 'SaveDraftBtn'); Start-Sleep 2
  Check (Test-Path $file) 'plan-build.json is saved'
  Check (-not (Test-Path $draftFile)) 'the draft is gone'
  Check ((Get-Content $file -Raw | ConvertFrom-Json).stages.Count -eq 2) 'with its two stages'

  "A4. A change to the template on screen goes to its draft; a reused id is given a new one"
  $saved = Get-Content $file -Raw
  AskHelper $w 'Add a review step after each build'; WaitHelper $w
  $req = LastRequest $log
  Check ($req -match '<current_template id="plan-build" source="the saved file">') 'the helper is given the template on screen'
  Check ($req -match '<taken_step_ids>[^<]*dm-build') 'and the ids it must not reuse'
  Check (Test-Path $draftFile) 'the change is a draft'
  Check ((Get-Content $file -Raw) -eq $saved) 'the template is untouched'
  $draft = Get-Content $draftFile -Raw | ConvertFrom-Json
  $ids = @($draft.stages | ForEach-Object { $_.steps } | ForEach-Object { $_.id })
  Check ($ids -contains 'data-model-review' -and $ids -contains 'screens-review') 'it has a review step after each build'
  Check ($ids.Count -eq ($ids | Select-Object -Unique).Count) 'every step id is unique'
  Check ((@($draft.stages[0].steps)[-1]).kind -eq 'handoff' -and (@($draft.stages[0].steps)[-1]).id -ne 'dm-build') 'the handoff that reused an id has one of its own'
  Check ((WaitText $w '*AgentHub corrected some ids*').Count -ge 1) 'the crab says the id was corrected'
  Check ((Texts $w '*EDITING').Count -eq 1 -and (Texts $w 'Review the build').Count -ge 2) 'the draft is open in the editor'
  Shot $w 'helper-changed.png'

  "A5. A change with problems is shown but cannot be saved"
  AskHelper $w 'Make a broken change'; WaitHelper $w
  Check ((WaitText $w '*needs text*').Count -ge 1) 'the problem is listed'
  Check (-not (ById $w 'SaveDraftBtn').Current.IsEnabled) 'Save is disabled'
  Check ((WaitText $w '*1 problem to fix before it can be saved*').Count -ge 1) 'the crab says so'
  Check ((Get-Content $file -Raw) -eq $saved) 'the template is still untouched'
  Shot $w 'helper-problem.png'
  Click (ById $w 'DiscardDraftBtn')
  $ask = Win 'Discard the draft'
  Press (Named $ask 'OK')[0]; Start-Sleep 2
  Check (-not (Test-Path $draftFile)) 'discarded'

  "A6. A question is answered and writes nothing"
  $before = RequestCount $log
  AskHelper $w 'What does an escalation point do?'; WaitHelper $w
  Check ((RequestCount $log) -eq $before + 1) 'the helper was asked'
  Check ((WaitText $w 'An escalation point pauses the run*').Count -ge 1) 'the answer is shown'
  Check (-not (Test-Path $draftFile)) 'no draft is written'

  "A7. A failure is shown and writes nothing"
  AskHelper $w 'Please fail'; WaitHelper $w
  Check ((WaitText $w '*not signed in (scripted)*').Count -ge 1) 'the reason is shown in the pane'
  Check (-not (Test-Path $draftFile)) 'no draft is written'

  "A8. The owner edits while the helper works: their edit is not written over"
  AskHelper $w 'A slow review step please'
  Start-Sleep 1
  Click (FindName $w '*Edit'); Start-Sleep 1
  SetText (Edit $w 'Template name' 5) 'Plan build, my version'
  Start-Sleep 2
  WaitHelper $w
  Check ((WaitText $w '*changed while I was working*').Count -ge 1) 'the helper says it did not write over the edit'
  $draft = Get-Content $draftFile -Raw | ConvertFrom-Json
  Check ($draft.name -eq 'Plan build, my version') 'the owner''s draft is kept'
  Check (@($draft.stages | ForEach-Object { $_.steps } | Where-Object { $_.id -like '*-review' }).Count -eq 0) 'without the helper''s change'
  Click (ById $w 'DiscardDraftBtn')
  $ask = Win 'Discard the draft'
  Press (Named $ask 'OK')[0]; Start-Sleep 1

  "A9. The X and the crab close the pane"
  Click (FindName $w 'Close helper'); Start-Sleep 1
  $pane = ById $w 'TemplateHelper'
  Check (-not $pane -or $pane.Current.IsOffscreen) 'X closes it'
  Check ($crab.Current.ItemStatus -eq '') 'and the crab is unlit'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

# ======== Part B: changing a template a run follows ========
$T = 'C:\data\agenthub\test-template-helper-run'
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Pane Test'; New-Item -ItemType Directory -Force "$proj\orchestration" | Out-Null
$runFile = "$proj\orchestration\plan-build.json"
[IO.File]::WriteAllText($runFile, @'
{
  "version": 1,
  "name": "Plan build",
  "stages": [
    {
      "id": "data-model",
      "title": "Stage 1",
      "steps": [
        { "id": "dm-start", "kind": "instruct", "title": "Get ready", "text": "echo getting ready" },
        { "id": "dm-build", "kind": "instruct", "title": "Build stage 1", "text": "echo building stage 1" },
        { "id": "dm-check", "kind": "ask-owner", "text": "Stage 1 is built. Commit it?" }
      ]
    }
  ]
}
'@)
Settings $T @(@{ Id = 'panetest'; Name = 'Pane Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
$log = FakeHelper $T
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
    foreach ($x in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $p.Id))) {
      foreach ($m in $x.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem)))) { if ($m.Current.Name -like '*Follow: Plan build*') { $item = $m } } }
    if ($item) { break }; Start-Sleep -Milliseconds 250 }
  Click $item
  $tree = Win '*Advanced Templates*'
  Start-Sleep 2
  Click (ById $tree 'GoBtn')
  Check ((WaitText $tree 'RUNNING').Count -eq 1) 'B0. the run is going'
  Start-Sleep 2
  Click (ById $tree 'PauseBtn')
  Check ((WaitText $tree 'RUN PAUSED').Count -eq 1) '    and is paused at its first step'

  "B1. The helper is told about the run, and its change is a draft the run does not take by itself"
  Click (FindName $tree 'Template helper'); Start-Sleep 1
  AskHelper $tree 'Add a review step after each build'; WaitHelper $tree
  $req = LastRequest $log
  Check ($req -match '<run pane="Terminal 1" state="paused">') 'the request says a run follows the template'
  Check ($req -match 'Reached steps \(keep exactly as they are, in their place\): dm-start') 'and which steps it has reached'
  Check ((Texts $tree '*EDITING').Count -eq 1) 'the draft is open in the editor'
  Check ((WaitText $tree '*has reached 1 step*').Count -ge 1) 'which says what is locked for the run'
  Check ((Texts $tree 'Terminal 1*step 1 of 3*').Count -eq 1) 'the run is as it was'
  Shot $tree 'helper-run-draft.png'

  "B2. Saved and applied, the run follows the helper's change"
  Click (ById $tree 'SaveDraftBtn'); Start-Sleep 2
  Click (ById $tree 'ApplyBtn')
  $ask = Win 'Apply to this run'
  Check ((Named $ask '*2 steps added*').Count -eq 1) 'Apply says what changes (the review and the handoff)'
  Press (Named $ask 'OK')[0]; Start-Sleep 2
  Check ((WaitText $tree 'Terminal 1*step 1 of 5*').Count -eq 1) 'the run has 5 steps now, still at step 1'
  Check ((Texts $tree '*PAUSED HERE').Count -eq 1) 'and is still paused where it was'
  Shot $tree 'helper-run-applied.png'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
