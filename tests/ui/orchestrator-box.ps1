# UI test: the orchestrator box shows above the pane an orchestration run drives, and only there. A scripted orchestrator (AGENTHUB_ORCHESTRATOR_COMMAND) and a cmd.exe worker: no Claude usage.
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

function Texts($root, $like) { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like $like }) }
function Rect($el) { $el.Current.BoundingRectangle }

# ---- fresh test state: one idea whose worker is plain cmd.exe, and a scripted orchestrator (no Claude usage) ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$T\projects" | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
$proj = Join-Path "$T\projects" 'Box Test'
$ideas = @(@{ Id = 'boxone'; Title = 'Box Test'; Description = 'A UI test of the orchestrator box.'; TargetDirectory = $proj; AgentCommand = 'cmd.exe'; ContextHandoffThreshold = 0.75; Status = 0; WorkflowId = 'template:builder' })
[IO.File]::WriteAllText("$T\ideas.json", (ConvertTo-Json -InputObject $ideas -Depth 5))
$fake = Join-Path $T 'fake-orchestrator.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000001"",""structured_output"":{""action"":""wait"",""to_worker"":null,""steer_handling"":""none"",""reason"":""UI test"",""progress"":""Scripted orchestrator for the box test"",""stage"":null,""stage_done"":false}}`r`n")

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3
  Click (FindName $main '*Add Terminal'); Start-Sleep 2   # a second, plain terminal beside the one the orchestrator will take

  "1. Before any build: no orchestrator box anywhere"
  Check ((Texts $main '*ORCHESTRATOR').Count -eq 0) 'no box on plain terminals'

  "2. Build Now: the box appears above the worker's pane only"
  Click (FindName $main '*Orchestration')
  $orch = $null
  for ($i = 0; $i -lt 40 -and -not $orch; $i++) { $orch = $main.FindFirst($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  Click (FindName (ListItems (Find $orch 'IdeasList'))[0] '*Build Now')
  $orch.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  $box = @()
  for ($i = 0; $i -lt 120 -and $box.Count -eq 0; $i++) { Start-Sleep -Milliseconds 250; $box = Texts $main '*ORCHESTRATOR' }
  Start-Sleep 2
  $box = Texts $main '*ORCHESTRATOR'
  Check ($box.Count -eq 1) "exactly one orchestrator box ($($box.Count))"
  $title = Texts $main 'Box Test' | Where-Object { $_.Current.AutomationId -eq 'SessionTitle' } | Select-Object -First 1
  Check ($null -ne $title) 'the worker pane is running'
  if ($box.Count -eq 1 -and $title) {
    $boxR = Rect $box[0]; $titleR = Rect $title
    Check ($boxR.Bottom -le $titleR.Top -and $boxR.Left -lt $titleR.Right -and $titleR.Left -lt $boxR.Right) "the box sits above the worker's pane (box bottom $([int]$boxR.Bottom), pane title top $([int]$titleR.Top))"
  }
  Check ((Texts $main 'Scripted orchestrator for the box test').Count -eq 1) "it shows the orchestrator's progress"
  Check ((Texts $main 'Box Test · worker #1').Count -eq 1) 'and the idea with its worker number'
  $sent = @(); for ($i = 0; $i -lt 160 -and $sent.Count -eq 0; $i++) { Start-Sleep -Milliseconds 250; $sent = Texts $main 'Last sent: *' }   # the first message waits until the worker is ready
  Check ($sent.Count -eq 1) 'and the last message sent to the worker'
  $read = Texts $main 'Last read: nothing yet*'
  Check ($read.Count -eq 1) 'and what it last read (nothing yet: a new worker is briefed from the spec)'
  if ($read.Count -eq 1 -and $sent.Count -eq 1) { Check ((Rect $read[0]).Bottom -le (Rect $sent[0]).Top) 'read above sent: the reply, then the message it led to' }
  Check ((@(Get-Content (Join-Path $T 'ideas.json') -Raw | ConvertFrom-Json))[0].AgentCommand -eq 'cmd.exe') 'the idea kept its own worker command (cmd.exe)'
  Shot $main 'b-box.png'

  "3. Pause and Resume from the box"
  Click (FindName $main '*Pause'); Start-Sleep 2
  Check ((Texts $main 'PAUSED').Count -eq 1) 'paused from the box'
  Check ((FindName $main '*Resume' 3) -ne $null) 'the button now says Resume'
  Shot $main 'b-paused.png'
  Click (FindName $main '*Resume'); Start-Sleep 3
  Check ((Texts $main 'PAUSED').Count -eq 0) 'resumed'
  Check ((Texts $main '*ORCHESTRATOR').Count -eq 1) 'still exactly one box'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
