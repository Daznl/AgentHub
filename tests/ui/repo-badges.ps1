# UI test for the repository badges after the models stopped carrying brushes (BadgeTone + BadgeToneBrushConverter):
# each badge must still be drawn in its old colour. Local Repos: a folder that exists (green "Up to date") and one that
# does not (red "Missing"), in paths with spaces. GitHub Repos: when gh can list repositories, the visibility and
# "Not Cloned Locally" badges. Reads the badge's background pixel from a capture of the window. TEST AgentHub with its own
# settings; UI Automation only. Screenshots go to C:\data\agenthub\uitest.
param([string]$Exe = 'C:\data\agenthub\build-linux-core-split\AgentHub.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
'@
[W]::SetProcessDPIAware() | Out-Null # physical pixels, so automation rectangles and the capture agree
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern
$T = 'C:\data\agenthub\test-settings-badges'; $Shots = 'C:\data\agenthub\uitest'
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Find($root, $id, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $e = $root.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty $id)); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "not found: $id" }
function TextElement($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    $e = $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
    if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  return $null }
function Capture($w) {
  $h = [IntPtr]$w.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $g.Dispose()
  return @{ Bitmap = $bmp; Left = $r.L; Top = $r.T } }
# The badge's background: just left of its text, inside the border's padding, at the text's middle height.
function BadgeColour($cap, $text) {
  $b = $text.Current.BoundingRectangle
  $c = $cap.Bitmap.GetPixel([int]($b.Left - $cap.Left - 3), [int]($b.Top + $b.Height / 2 - $cap.Top))
  return '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B }
function Click($el) { $el.GetCurrentPattern($Invoke).Invoke() }
function MainWindow([int]$wait = 20) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    $w = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $script:procId)); if ($w) { return $w }; Start-Sleep -Milliseconds 250 }
  throw 'main window not found' }

# ---- test state: two repositories, one present and one missing, both in paths with spaces ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$present = "$T\Present Repo"; $missing = "$T\Missing Repo"
New-Item -ItemType Directory -Force $present, $Shots | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings.Repositories = @(
  [pscustomobject]@{ Id = 'badge-present'; Name = 'Present Repo'; LocalPath = $present; RemoteUrl = $null; AddedAt = (Get-Date).ToString('o') },
  [pscustomobject]@{ Id = 'badge-missing'; Name = 'Missing Repo'; LocalPath = $missing; RemoteUrl = $null; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))

$env:AGENTHUB_SETTINGS_DIR = $T
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $w = MainWindow
  Start-Sleep 3

  "1. Local Repos: each badge in its old colour"
  Click (Find $w 'ViewReposBtn'); Start-Sleep 1
  $upToDate = TextElement $w '*Up to date*'
  $gone = TextElement $w '*Missing*'
  Check ($null -ne $upToDate) 'the present repository says Up to date'
  Check ($null -ne $gone) 'the missing repository says Missing'
  $cap = Capture $w
  $cap.Bitmap.Save((Join-Path $Shots 'badges-local.png'))
  if ($upToDate) { $c = BadgeColour $cap $upToDate; Check ($c -eq '#065F46') "Up to date badge is #065F46 (got $c)" }
  if ($gone) { $c = BadgeColour $cap $gone; Check ($c -eq '#991B1B') "Missing badge is #991B1B (got $c)" }
  $cap.Bitmap.Dispose()

  "2. GitHub Repos: visibility and not-cloned badges (when gh lists repositories)"
  Click (Find $w 'ViewGitHubBtn')
  $vis = $null
  for ($i = 0; $i -lt 45 * 2 -and -not $vis; $i++) {
    $vis = $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) |
      Where-Object { $_.Current.Name -in 'Private', 'Public' } | Select-Object -First 1
    if (-not $vis) { Start-Sleep -Milliseconds 500 } }
  if (-not $vis) { '  SKIP  gh listed no repositories here (not signed in, or no network)' }
  else {
    Start-Sleep 2
    $cap = Capture $w
    $cap.Bitmap.Save((Join-Path $Shots 'badges-github.png'))
    $c = BadgeColour $cap $vis
    $want = if ($vis.Current.Name -eq 'Private') { '#312E81' } else { '#064E3B' }
    Check ($c -eq $want) "$($vis.Current.Name) badge is $want (got $c)"
    $notCloned = TextElement $w 'Not Cloned Locally' 5
    if ($notCloned) { $c = BadgeColour $cap $notCloned; Check ($c -eq '#27272A') "Not Cloned Locally badge is #27272A (got $c)" }
    else { '  SKIP  every listed repository is cloned here' }
    $cap.Bitmap.Dispose()
  }
}
finally {
  Stop-Process -Id $script:procId -Force -ErrorAction SilentlyContinue
  Remove-Item Env:\AGENTHUB_SETTINGS_DIR -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
if ($script:fails -gt 0) { exit 1 }
