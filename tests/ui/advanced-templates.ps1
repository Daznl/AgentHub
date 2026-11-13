# UI test for the Advanced templates window (empty project, add the example, the tree, a file with problems), on a TEST
# AgentHub with its own settings and its own project folder (a path with spaces). UI Automation patterns only: no mouse,
# no keyboard, no focus stealing. Screenshots go to C:\data\agenthub\uitest.
param([string]$Exe = 'C:\data\agenthub\build-advanced-templates\AgentHub.exe', [switch]$KeepOpen)
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
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern; $SelItem = [System.Windows.Automation.SelectionItemPattern]::Pattern
$T = 'C:\data\agenthub\test-settings-advanced'; $Shots = 'C:\data\agenthub\uitest'
$Project = "$T\My Project"
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Find($root, $id, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $e = $root.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty $id)); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "not found: $id" }
function Texts($root) { $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name } }
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
function Click($el) { $el.GetCurrentPattern($Invoke).Invoke() }
function Window($titleLike, [int]$wait = 20) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $script:procId))) {
      if ($w.Current.Name -like $titleLike) { return $w }
      foreach ($c in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))) { if ($c.Current.Name -like $titleLike) { return $c } } }
    Start-Sleep -Milliseconds 250 }
  throw "window not found: $titleLike" }
function ListItems($list) { $list.FindAll($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) }

# ---- fresh test state: a project with no templates ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $Project, $Shots | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))

$env:AGENTHUB_SETTINGS_DIR = $T
$p = Start-Process $Exe -ArgumentList '--advanced-templates', "`"$Project`"" -PassThru; $script:procId = $p.Id
try {
  $w = Window '*Advanced Templates*'
  Start-Sleep 2

  "1. A project without templates says so and offers the example"
  Check ((Texts $w) -match 'no advanced templates yet') 'says there are none yet'
  Check ((Texts $w) -match [regex]::Escape("$Project\orchestration")) 'names the folder, spaces and all'
  Shot $w 'advanced-empty.png'
  Click (Find $w 'AddExampleBtn'); Start-Sleep 2

  "2. The example is written and drawn as a tree"
  $file = "$Project\orchestration\staged-build.json"
  Check (Test-Path $file) 'orchestration\staged-build.json written'
  $list = Find $w 'TemplateList'
  Check ((ListItems $list).Count -eq 1) 'one template listed'
  $t = Texts $w
  Check ($t -contains 'Staged build') 'its name is the heading'
  Check ($t -like '2 stages*15 steps') 'its size is shown'
  Check ($t -contains 'START' -and $t -contains 'DONE') 'the tree runs from Start to Done'
  Check ($t -contains 'Stage 1' -and $t -contains 'Stage 2') 'both stages are headings'
  Check ($t -contains 'Create branch feature/stage-1') 'a git step reads as what it does'
  Check (($t | Where-Object { $_ -like '*pauses for you' }).Count -eq 2) 'both escalation points are flagged'
  Check ($t -contains 'Open a pull request into main') 'the last step opens the pull request'
  Shot $w 'advanced-tree.png'

  "3. A file with problems is listed and says what is wrong"
  $broken = Get-Content $file -Raw | ConvertFrom-Json
  $broken.name = 'Loop'; $broken.stages[0] | Add-Member after @('stage-2')
  [IO.File]::WriteAllText("$Project\orchestration\loop.json", ($broken | ConvertTo-Json -Depth 20))
  [IO.File]::WriteAllText("$Project\orchestration\not-json.json", '{ "name": ')
  Click (Find $w 'RefreshBtn'); Start-Sleep 2
  $items = ListItems $list
  Check ($items.Count -eq 3) "three files listed (got $($items.Count))"
  ($items | Where-Object { $_.Current.Name -eq 'Loop' }).GetCurrentPattern($SelItem).Select(); Start-Sleep 1
  $t = Texts $w
  Check ($t -match 'problem stops this template being run') 'the loop is reported as a problem'
  Check ($t -match 'A template cannot loop') 'and named'
  Check ($t -contains 'Stage 1') 'it is still drawn'
  Shot $w 'advanced-problem.png'
  ($items | Where-Object { $_.Current.Name -eq 'not-json' }).GetCurrentPattern($SelItem).Select(); Start-Sleep 1
  Check ((Texts $w) -match 'This file cannot be read') 'a file that is not JSON says it cannot be read'
  Shot $w 'advanced-unreadable.png'
  ($items | Where-Object { $_.Current.Name -eq 'Staged build' }).GetCurrentPattern($SelItem).Select(); Start-Sleep 1
} finally {
  if (-not $KeepOpen) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
"Failures: $script:fails"
exit $script:fails
