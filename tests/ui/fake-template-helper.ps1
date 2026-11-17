# A scripted Template helper for tests/ui/template-helper.ps1: reads the request AgentHub sends on stdin, keeps a copy,
# and answers as Claude Code's JSON envelope would, by what the request asks. No Claude usage. Kept ASCII.
param([string]$Log)
$ErrorActionPreference = 'Stop'
$request = [Console]::In.ReadToEnd()
New-Item -ItemType Directory -Force $Log | Out-Null
$n = @(Get-ChildItem $Log -Filter 'request-*.txt').Count + 1
[IO.File]::WriteAllText((Join-Path $Log ("request-{0:D2}.txt" -f $n)), $request)
$ask = ''
if ($request -match '(?s)<request>\s*(.*?)\s*</request>') { $ask = $Matches[1] }

function Step($id, $kind, $title, $text, $branch, $target, $escalate) {
  [ordered]@{ id = $id; kind = $kind; title = $title; text = $text; branch = $branch; target = $target; escalate = [bool]$escalate } }
function Reply($answer, $action, $template, [bool]$isError = $false, $result = '') {
  $out = [ordered]@{ type = 'result'; is_error = $isError; result = $result; session_id = '00000000-0000-0000-0000-000000000009' }
  if (-not $isError) { $out.structured_output = [ordered]@{ answer = $answer; suggestions = @('Add a review step after each build'); action = $action; template = $template } }
  [Console]::Out.Write(($out | ConvertTo-Json -Depth 30 -Compress)) }
function Current {
  if ($request -match '(?s)<current_template id="[^"]*" source="[^"]*">\s*(.*?)</current_template>') { return ($Matches[1] | ConvertFrom-Json) }
  throw 'no template on screen' }

if ($ask -match 'fail') { Reply $null $null $null $true 'Claude Code is not signed in (scripted).'; exit 0 }
if ($ask -match 'escalation') { Reply 'An escalation point pauses the run and asks you whenever the orchestrator cannot settle something there from your instructions.' 'none' $null; exit 0 }
if ($ask -match 'slow') { Start-Sleep -Seconds 8 }

if ($ask -match 'Build .*PLAN\.md') {
  $template = [ordered]@{ name = 'Plan build'; description = 'Builds docs/PLAN.md stage by stage.'; stages = @(
    [ordered]@{ id = 'data-model'; title = 'Stage 1: data model'; after = $null; steps = @(
      (Step 'dm-branch' 'create-branch' $null $null 'feature/data-model' $null $false),
      (Step 'dm-build' 'instruct' 'Build the data model' 'Read docs/PLAN.md, then build stage 1 (the data model) with tests. Do not start stage 2.' $null $null $false),
      (Step 'dm-watch' 'wait-for-worker' $null $null $null $null $true),
      (Step 'dm-commit' 'commit' $null $null $null $null $false)) },
    [ordered]@{ id = 'screens'; title = 'Stage 2: screens'; after = $null; steps = @(
      (Step 'sc-session' 'new-session' $null $null $null $null $false),
      (Step 'sc-build' 'instruct' 'Build the screens' 'Read docs/PLAN.md, then build stage 2 (the screens) with tests.' $null $null $false),
      (Step 'sc-watch' 'wait-for-worker' $null $null $null $null $true),
      (Step 'sc-pr' 'open-pull-request' $null $null $null 'main' $false)) }) }
  Reply 'I read docs/PLAN.md and built a stage for each of its two stages, a branch first and a pull request at the end.' 'create' $template
  exit 0
}

if ($ask -match 'review step') {
  $t = Current
  foreach ($stage in $t.stages) {
    $steps = New-Object System.Collections.ArrayList
    foreach ($s in $stage.steps) { [void]$steps.Add($s); if ($s.kind -eq 'instruct' -and $s.title -like 'Build*') { [void]$steps.Add((Step ($stage.id + '-review') 'instruct' 'Review the build' 'Review the diff of this stage and fix what you find.' $null $null $false)) } }
    $stage.steps = @($steps) }
  # A careless helper: the new handoff reuses a step id already in the template, which AgentHub must give a new one.
  $t.stages[0].steps = @($t.stages[0].steps) + @((Step 'dm-build' 'handoff' $null $null $null $null $false))
  $t.PSObject.Properties.Remove('version')
  Reply 'I added a review step after each build step, and a handoff at the end of the first stage.' 'change' $t
  exit 0
}

if ($ask -match 'broken') {
  $t = Current
  $t.stages[0].steps = @($t.stages[0].steps) + @((Step 'needs-words' 'instruct' 'Explain the plan' $null $null $null $false))
  $t.PSObject.Properties.Remove('version')
  Reply 'I added a step that explains the plan.' 'change' $t
  exit 0
}

Reply "I am not sure what to do with: $ask" 'none' $null
