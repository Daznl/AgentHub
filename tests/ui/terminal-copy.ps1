# UI test: a program in a Cockpit terminal copies with OSC 52 (as Claude Code does for the selection it draws) and the text
# lands on the Windows clipboard. A Command Prompt session and a scripted orchestrator that types the command in: no Claude
# usage, no keystrokes sent to the desktop. The clipboard's text is saved first and put back at the end.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'))
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
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern; $Val = [System.Windows.Automation.ValuePattern]::Pattern
$T = 'C:\data\agenthub\test-terminal-copy'; $Shots = 'C:\data\agenthub\uitest'
New-Item -ItemType Directory -Force $Shots | Out-Null
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Buttons($root) { $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) }
function FindName($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { foreach ($e in (Buttons $root)) { if ($e.Current.Name -like $like) { return $e } }; Start-Sleep -Milliseconds 250 }
  throw "not found by name: $like" }
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
function Click($el) { $el.GetCurrentPattern($Invoke).Invoke() }
function MenuItem($like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $script:procId))) {
      foreach ($m in $w.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::MenuItem)))) { if ($m.Current.Name -like $like) { return $m } } }
    Start-Sleep -Milliseconds 250 }
  throw "menu item not found: $like" }
function Edit($root, $nameLike, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) {
    foreach ($e in $root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit)))) { if ($e.Current.Name -like $nameLike) { return $e } }
    Start-Sleep -Milliseconds 250 }
  throw "edit not found: $nameLike" }
function ClipText { try { Get-Clipboard -Raw } catch { $null } }

$expected = 'AgentHub copy test ' + [char]0x2713 + ' ' + (Get-Date).ToString('HHmmss')
$saved = ClipText

# ---- test state: one repository holding the OSC 52 emitter, an orchestrator that types the command into the session ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
$proj = Join-Path $T 'Copy Test'; New-Item -ItemType Directory -Force $proj | Out-Null
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expected))
# A read request first (must never be answered or change anything), then the write.
[IO.File]::WriteAllText((Join-Path $proj 'emit.ps1'), "[Console]::Write([char]27 + ']52;c;?' + [char]7); [Console]::Write([char]27 + ']52;c;$b64' + [char]7); 'emitted'")
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings.Repositories = @(@{ Id = 'copytest'; Name = 'Copy Test'; LocalPath = $proj; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
$fake = Join-Path $T 'fake-orchestrator.cmd'
[IO.File]::WriteAllText($fake, "@echo off`r`nmore > nul`r`necho {""type"":""result"",""is_error"":false,""result"":""ok"",""session_id"":""00000000-0000-0000-0000-000000000003"",""structured_output"":{""to_owner"":""Running the copy."",""to_worker"":""powershell -NoProfile -ExecutionPolicy Bypass -File emit.ps1"",""note"":""copy test""}}`r`n")

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"; $env:AGENTHUB_ORCHESTRATOR_COMMAND = $fake
Set-Clipboard -Value 'before the copy'
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3

  "1. A Command Prompt session in the repository, with an orchestrator to type the command"
  Click (FindName $main 'Copy Test'); Start-Sleep 1
  Click (FindName $main 'Command Prompt'); Start-Sleep 6
  Click (FindName $main '*Orchestrate' 20)
  Click (MenuItem 'Orchestrator with no preset context'); Start-Sleep 2
  $box = Edit $main 'Message the orchestrator'
  $box.GetCurrentPattern($Val).SetValue('copy it')
  Click (FindName $main 'Send')

  "2. The program's OSC 52 copy reaches the Windows clipboard"
  $got = $null
  for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Milliseconds 250; $got = ClipText; if ($got -eq $expected) { break } }
  Check ($got -eq $expected) "clipboard holds the copied text (got: '$got')"
  Shot $main 'copy-osc52.png'
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  if ($null -ne $saved -and $saved -ne '') { Set-Clipboard -Value $saved } else { Set-Clipboard -Value $null }
}
"Failures: $script:fails"
exit $script:fails
