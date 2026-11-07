# UI test: a spec typed but not saved is kept (reload, switching ideas, Build Now), on a TEST AgentHub with its own settings,
# ideas, workflows folder and projects folder. UI Automation patterns only: no mouse, no keyboard, no focus stealing.
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
function SetText($el, $text) { $el.GetCurrentPattern($Val).SetValue($text) }
function ListItems($list) { $list.FindAll($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) }
function IdeaById($id) { (Ideas) | Where-Object { $_.Id -eq $id } | Select-Object -First 1 }

# ---- fresh test state: one idea with the placeholder spec ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$T\projects" | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
[IO.File]::WriteAllText("$T\ideas.json", '[{"Id":"ideaone","Title":"New App #1","Description":"Describe the goal, architecture, features, and requirements for this application.","TargetDirectory":"C:\\Users\\me\\Desktop\\ClaudeCProjects\\Pie","AgentCommand":"claude","ContextHandoffThreshold":0.75,"Status":0,"WorkflowId":"template:builder"}]')
$para1 = 'A habit tracker for my phone: log habits daily, streaks, a weekly chart, works offline.'
$para2 = 'A second idea typed and never saved: a recipe scaler.'
$para3 = 'Edited again, then Build Now pressed without Save.'

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  Click (FindName $main '*Orchestration')
  $orch = $null
  for ($i = 0; $i -lt 40 -and -not $orch; $i++) { $orch = $main.FindFirst($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  $desc = Find $orch 'EditDescriptionBox'; $list = Find $orch 'IdeasList'

  "1. Type a spec, then Add Idea (a reload) without saving"
  SetText $desc $para1
  Click (FindName $orch '*Add Idea'); Start-Sleep 2
  Check ((IdeaById 'ideaone').Description -eq $para1) "the typed spec was saved (got '$((IdeaById 'ideaone').Description)')"

  "2. Type in the new idea, switch back to the first without saving"
  SetText $desc $para2
  $newId = ((Ideas) | Where-Object { $_.Id -ne 'ideaone' } | Select-Object -First 1).Id
  (ListItems $list)[0].GetCurrentPattern($SelItem).Select(); Start-Sleep 2
  Check ((IdeaById $newId).Description -eq $para2) "the idea left behind kept its typed spec (got '$((IdeaById $newId).Description)')"
  Check ((Value $desc) -eq $para1) "the first idea shows its spec (got '$(Value $desc)')"

  "3. Edit, then Build Now without saving (stopped by an unfilled workflow so nothing really builds)"
  SetText $desc $para3
  Pick (Find $orch 'EditWorkflowCombo') '*Data Processing (template)'
  $build = (ListItems $list)[0].FindFirst($TS::Descendants, (Cond $A::NameProperty '▶ Build Now'))
  if (-not $build) { $build = FindName (ListItems $list)[0] '*Build Now' }
  Click $build
  $dlg = Dialog 'Workflow'
  Shot $orch 'u-buildnow.png'
  $dlg.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  Check ((IdeaById 'ideaone').Description -eq $para3) "Build Now saved the spec first (got '$((IdeaById 'ideaone').Description)')"
  Check ((IdeaById 'ideaone').WorkflowId -eq 'template:data-processing') 'and the workflow picked'
  Check ((IdeaById 'ideaone').Status -eq 0) 'status untouched (still queued: nothing was built)'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
