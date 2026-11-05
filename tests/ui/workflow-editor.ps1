# UI test for the workflow editor (edit a template, save as own workflow, use it), on a TEST AgentHub with its own settings,
# ideas, archive repo and projects folder. UI Automation patterns only: no mouse, no keyboard, no focus stealing.
param([string]$Exe = 'C:\data\agenthub\build-repo\AgentHub.exe')
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
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern; $Sel = [System.Windows.Automation.SelectionPattern]::Pattern
$SelItem = [System.Windows.Automation.SelectionItemPattern]::Pattern; $Exp = [System.Windows.Automation.ExpandCollapsePattern]::Pattern
$Val = [System.Windows.Automation.ValuePattern]::Pattern
$T = 'C:\data\agenthub\test-settings-repo'; $Shots = 'C:\data\agenthub\uitest'
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Find($root, $id, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $e = $root.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty $id)); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "not found: $id" }
function FindName($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { foreach ($e in $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button)))) { if ($e.Current.Name -like $like) { return $e } }; Start-Sleep -Milliseconds 250 }
  throw "not found by name: $like" }
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
function Click($el) { $el.GetCurrentPattern($Invoke).Invoke() }
function Selected($combo) { ($combo.GetCurrentPattern($Sel).Current.GetSelection() | ForEach-Object { $_.Current.Name }) -join ',' }
function Items($combo) {
  $combo.GetCurrentPattern($Exp).Expand()
  for ($i = 0; $i -lt 20; $i++) {   # the dropdown draws its items a moment after it opens
    Start-Sleep -Milliseconds 250
    $all = $combo.FindAll($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))
    if ($all.Count -gt 0) { break } }
  return ,$all }
function Pick($combo, $like) {
  $item = (Items $combo) | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
  if (-not $item) { throw "no item $like" }
  $item.GetCurrentPattern($SelItem).Select(); $combo.GetCurrentPattern($Exp).Collapse(); Start-Sleep -Milliseconds 500 }
# Dialogs (MessageBox, the New project prompt) are windows of the process, at the top level or under their owner.
function Dialog($titleLike, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $script:procId))) {
      if ($w.Current.Name -like $titleLike) { return $w }
      foreach ($c in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))) { if ($c.Current.Name -like $titleLike) { return $c } } }
    Start-Sleep -Milliseconds 250 }
  throw "dialog not found: $titleLike" }
function Ideas { $x = Get-Content (Join-Path $T 'ideas.json') -Raw | ConvertFrom-Json; return ,@($x | ForEach-Object { $_ }) }
function Repos { (Get-Content "$T\settings.json" -Raw | ConvertFrom-Json).Repositories }

function Value($el) { $el.GetCurrentPattern($Val).Current.Value }
function ReadOnly($el) { $el.GetCurrentPattern($Val).Current.IsReadOnly }
function SetText($el, $text) { $el.GetCurrentPattern($Val).SetValue($text) }

# ---- fresh test state: one idea, no own workflows ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$T\projects", "$T\archive-repo" | Out-Null
git -C "$T\archive-repo" init -q; git -C "$T\archive-repo" -c user.email=t@t -c user.name=t commit -q --allow-empty -m init
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
[IO.File]::WriteAllText("$T\ideas.json", '[{"Id":"uitestidea","Title":"Sales data","Description":"Process the sales data","TargetDirectory":"C:\\Users\\danie\\Desktop\\ClaudeCProjects\\Pie","AgentCommand":"claude","ContextHandoffThreshold":0.75,"Status":0,"WorkflowId":"template:builder"}]')
$templateFile = Join-Path (Split-Path $Exe) 'workflows\data-processing.md'
$templateBefore = (Get-FileHash $templateFile).Hash

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"; $env:AGENTHUB_RUNS_REPO = "$T\archive-repo"
$env:AGENTHUB_RUNS_WORKTREE = "$T\archive"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  Click (FindName $main '*Orchestration')
  $orch = $null
  for ($i = 0; $i -lt 40 -and -not $orch; $i++) { $orch = $main.FindFirst($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  Click (Find $orch 'WorkflowsBtn')
  $wf = Dialog '*Orchestrator Workflows*'
  Start-Sleep 2

  "1. A template can be edited"
  $list = Find $wf 'WorkflowList'
  $item = $list.FindAll($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) | Where-Object { $_.Current.Name -like '*Data Processing (template)' } | Select-Object -First 1
  Check ($item -ne $null) 'Data Processing template listed'
  $item.GetCurrentPattern($SelItem).Select(); Start-Sleep 1
  $inputs = Find $wf 'InputsBox'; $instr = Find $wf 'InstructionsBox'; $name = Find $wf 'NameBox'; $model = Find $wf 'ModelCombo'
  Check (-not (ReadOnly $inputs)) 'Inputs box can be typed in'
  Check (-not (ReadOnly $instr)) 'Orchestrator instructions can be typed in'
  Check ($model.Current.IsEnabled) 'model can be chosen'
  Check ((Selected $model) -eq 'sonnet') "model shows sonnet (got '$(Selected $model)')"
  Check ((Value $inputs) -like 'pipeline_location: <paste*') "inputs show the placeholder (got '$(Value $inputs)')"
  Check ((Find $wf 'SaveBtn').Current.Name -like '*Save as my workflow') 'Save says it makes your own copy'
  Shot $wf 'w-template.png'

  "2. Fill in, add instructions, pick a model, save"
  SetText $name 'Sales pipeline'
  SetText $inputs 'pipeline_location: C:\work\sales\pipeline.xlsx'
  SetText $instr ((Value $instr) + "`n`n## Also for this pipeline`n- After each stage, compare row counts with the previous stage.")
  Pick $model 'opus'
  Check ((Selected $model) -eq 'opus') "model now opus (got '$(Selected $model)')"
  Click (Find $wf 'SaveBtn'); Start-Sleep 3
  $saved = "$T\archive\workflows\sales-pipeline.md"
  Check (Test-Path $saved) 'saved as workflows/sales-pipeline.md'
  $text = if (Test-Path $saved) { Get-Content $saved -Raw } else { '' }
  Check ($text -match 'model: opus') 'file has model: opus'
  Check ($text -match [regex]::Escape('input.pipeline_location: C:\work\sales\pipeline.xlsx')) 'file has the pipeline location'
  Check ($text -match 'compare row counts') 'file has the added instructions'
  Check ($text -match 'based-on: Data Processing') 'file says it is based on Data Processing'
  Check ((Get-FileHash $templateFile).Hash -eq $templateBefore) 'template file unchanged'
  Check ((git -C "$T\archive-repo" log --format=%s agenthub-workflows) -match 'Sales pipeline') 'committed to the archive branch'
  Check ((Find $wf 'SaveBtn').Current.Name -eq '💾 Save') "now editing the own copy (button '$((Find $wf 'SaveBtn').Current.Name)')"
  Shot $wf 'w-saved.png'

  "3. Use it for the idea"
  Click (Find $wf 'UseBtn'); Start-Sleep 2
  Check ((Ideas)[0].WorkflowId -eq 'sales-pipeline') "idea uses sales-pipeline (got '$((Ideas)[0].WorkflowId)')"
  Check ((Selected (Find $orch 'EditWorkflowCombo')) -eq 'Sales pipeline') "Orchestration window shows it (got '$(Selected (Find $orch 'EditWorkflowCombo'))')"
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
