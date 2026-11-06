# UI test for the Orchestration window's Repository picker and New project, on a TEST AgentHub with its own settings,
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

# ---- fresh test state ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$T\projects", "$T\unlisted-folder" | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
[IO.File]::WriteAllText("$T\ideas.json", '[]')
$repoCountAtStart = @(Repos).Count

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
  $combo = Find $orch 'EditRepoCombo'

  "1. Empty queue"
  Shot $orch 'a-empty.png'
  Check (-not $combo.Current.IsEnabled) 'repository picker is disabled with no idea'
  Check ((Find $orch 'RepoWarningText').Current.Name -like '*Add Idea first*') 'it says to add an idea first'
  Check (-not (Find $orch 'NewProjectBtn').Current.IsEnabled) 'New project is disabled with no idea'

  "2. Add an idea, pick a repository, save"
  Click (FindName $orch '*Add Idea'); Start-Sleep 2
  Check $combo.Current.IsEnabled 'picker enabled once an idea is selected'
  $n = (Items $combo).Count; $combo.GetCurrentPattern($Exp).Collapse()
  Check ($n -eq $repoCountAtStart) "dropdown lists all $repoCountAtStart repositories (got $n)"
  Pick $combo 'Pie'
  Check ((Selected $combo) -eq 'Pie') "Pie selected (got '$(Selected $combo)')"
  Shot $orch 'b-picked.png'
  Click (FindName $orch '*Save Changes'); Start-Sleep 1
  $ok = Dialog 'Saved'; $ok.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  $idea = (Ideas)[0]
  Check ($idea.TargetDirectory -eq "$env:USERPROFILE\Desktop\ClaudeCProjects\Pie") "ideas.json has Pie's folder (got '$($idea.TargetDirectory)')"
  Check ((Selected $combo) -eq 'Pie') "still Pie after the save reload (got '$(Selected $combo)')"

  "3. A folder that is not one of AgentHub's repositories"
  $ideas = Ideas; $ideas[0].TargetDirectory = "$T\unlisted-folder"
  [IO.File]::WriteAllText("$T\ideas.json", (ConvertTo-Json @($ideas) -Depth 10))
  Click (FindName $orch '*Add Idea'); Start-Sleep 2      # a second idea, then back to the first so it reloads from disk
  $list = Find $orch 'IdeasList'
  $first = $list.FindFirst($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))
  $first.GetCurrentPattern($SelItem).Select(); Start-Sleep 1
  Check ((Selected $combo) -like '*unlisted-folder') "shown flagged (got '$(Selected $combo)')"
  Check ((Find $orch 'RepoWarningText').Current.Name -like "*not one of AgentHub's repositories*") 'warning shown'
  Shot $orch 'c-unlisted.png'
  Click (Find $orch 'AddUnlistedBtn'); Start-Sleep 2
  Check (@(Repos | Where-Object { $_.LocalPath -eq "$T\unlisted-folder" }).Count -eq 1) 'added to settings.json repositories'
  Check (Test-Path "$T\unlisted-folder\.git") 'git init done'
  Check ((Selected $combo) -eq 'unlisted-folder') "now selected as a repository (got '$(Selected $combo)')"

  "4. New project"
  Click (Find $orch 'NewProjectBtn')
  $prompt = Dialog 'New project'
  $edit = $prompt.FindFirst($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))
  $edit.GetCurrentPattern($Val).SetValue('uitest project')
  Click (FindName $prompt 'OK' 5); Start-Sleep 3
  Check (Test-Path "$T\projects\uitest project\.git") 'folder created with git'
  Check (@(Repos | Where-Object { $_.Name -eq 'uitest project' }).Count -eq 1) 'added to repositories'
  Check ((Ideas)[0].TargetDirectory -eq "$T\projects\uitest project") "idea points at it (got '$((Ideas)[0].TargetDirectory)')"
  Check ((Selected $combo) -eq 'uitest project') "selected in the picker (got '$(Selected $combo)')"
  Shot $orch 'd-new-project.png'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
