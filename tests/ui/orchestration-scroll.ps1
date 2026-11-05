# UI test: the Orchestration editor scrolls on a short window and the handoff box is never cut off, on a TEST AgentHub with its own settings,
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
function Value($el) { $el.GetCurrentPattern($Val).Current.Value }
function ReadOnly($el) { $el.GetCurrentPattern($Val).Current.IsReadOnly }
function SetText($el, $text) { $el.GetCurrentPattern($Val).SetValue($text) }
$Scroll = [System.Windows.Automation.ScrollPattern]::Pattern; $Transform = [System.Windows.Automation.TransformPattern]::Pattern
function Rect($el) { $el.Current.BoundingRectangle }

# ---- fresh test state: one idea whose folder has a long handoff ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$T\projects", "$T\archive-repo", "$T\proj\.agenthub" | Out-Null
git -C "$T\archive-repo" init -q; git -C "$T\archive-repo" -c user.email=t@t -c user.name=t commit -q --allow-empty -m init
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
[IO.File]::WriteAllText("$T\proj\.agenthub\handoff.md", "# Handoff marker`n" + ((1..200 | ForEach-Object { "line $_ of the handoff" }) -join "`n"))
[IO.File]::WriteAllText("$T\ideas.json", ('[{"Id":"uitestidea","Title":"Scroll test","Description":"x","TargetDirectory":"' + ("$T\proj" -replace '\\', '\\') + '","AgentCommand":"claude","ContextHandoffThreshold":0.75,"Status":0,"WorkflowId":"template:builder"}]'))

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
  $box = Find $orch 'HandoffPreviewBox'; $scroller = Find $orch 'EditorScroll'
  Check ((Value $box) -like '# Handoff marker*') 'handoff loaded'

  "1. Small window (the minimum, 960 x 600)"
  $orch.GetCurrentPattern($Transform).Resize(960, 600); Start-Sleep 2
  Shot $orch 's-small-top.png'
  $sp = $scroller.GetCurrentPattern($Scroll)
  Check ($sp.Current.VerticallyScrollable) 'editor scrolls'
  Check ((Rect $box).Height -ge 170) "handoff box keeps its height (got $([int](Rect $box).Height)px)"
  $sp.SetScrollPercent(-1, 100); Start-Sleep 1
  Shot $orch 's-small-bottom.png'
  $w = Rect $orch; $b = Rect $box
  Check ($b.Bottom -le $w.Bottom -and $b.Top -ge $w.Top) "scrolled to the end, the whole handoff box is inside the window (box $([int]$b.Top)-$([int]$b.Bottom), window $([int]$w.Top)-$([int]$w.Bottom))"
  Check (-not $box.Current.IsOffscreen) 'handoff box on screen'

  "2. As tall as the screen allows"
  Add-Type -AssemblyName System.Windows.Forms
  $screenH = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea.Height
  $orch.GetCurrentPattern($Transform).Move(0, 0); $orch.GetCurrentPattern($Transform).Resize(1240, $screenH); Start-Sleep 2
  Shot $orch 's-tall.png'
  $sp = $scroller.GetCurrentPattern($Scroll); $b = Rect $box; $sc = Rect $scroller
  Check ((Rect $box).Height -ge 170) "handoff box at least its minimum (got $([int]$b.Height)px, window $screenH px)"
  if ($sp.Current.VerticallyScrollable) { '  (still scrolls at this screen height: the editor needs more than the panel has)' }
  else { Check ([math]::Abs($sc.Bottom - $b.Bottom) -lt 30) "everything fits, so the box reaches the bottom of the panel (box bottom $([int]$b.Bottom), panel $([int]$sc.Bottom))" }
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
