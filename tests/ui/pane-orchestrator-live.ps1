# Live test (uses Claude usage; normal permission mode, never --dangerously-skip-permissions): three Claude Code sessions side by side, each with a pane orchestrator attached and a small job,
# with the context window set small (70k) so every session hands off and restarts within minutes. Watches until every pane
# is on session #2 and its fresh session has been briefed, or the time runs out. UI Automation only.
# The repositories live under Desktop\agenthub-live-test\{alpha,beta,gamma} (git init them first): Desktop is a folder Claude Code
# already trusts, so no trust prompt blocks the launcher-started sessions. Settings, sessions and transcripts go under C:\data\agenthub\test-pane-live.
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'),
  [int]$Minutes = 35,
  [int]$WindowTokens = 70000
)
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
$T = 'C:\data\agenthub\test-pane-live'; $Shots = 'C:\data\agenthub\uitest\live'
$R = Join-Path $env:USERPROFILE 'OneDrive - IGO Limited\Desktop\agenthub-live-test'
New-Item -ItemType Directory -Force $Shots | Out-Null
$script:fails = 0
function Log($s) { $line = "$(Get-Date -Format HH:mm:ss) $s"; $line; Add-Content -Path (Join-Path $Shots 'live.log') -Value $line -Encoding UTF8 }
function Check($ok, $what) { if ($ok) { Log "  PASS  $what" } else { Log "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Buttons($root) { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button))) | ForEach-Object { $_ }) }
function Named($root, $like) { @(Buttons $root | Where-Object { $_.Current.Name -like $like }) }
function FindName($root, $like, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $all = Named $root $like; if ($all.Count -gt 0) { return $all[0] }; Start-Sleep -Milliseconds 250 }
  throw "not found by name: $like" }
function Texts($root, $like) { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like $like }) }
function Edits($root, $like) { @($root.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Edit))) | Where-Object { $_.Current.Name -like $like }) }
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

# ---- test state: three repositories, Claude Code without permission prompts, a 70k context window ----
$S = Join-Path $T 'settings'
Remove-Item $S -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $S | Out-Null
Remove-Item (Join-Path $Shots 'live.log') -ErrorAction SilentlyContinue
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings.AttentionFlashEnabled = $true; $settings.ClaudeContextWindowTokens = $WindowTokens
# Plain Claude Code: every security check (folder trust, permissions) is yours to answer in the pane (owner decision 2026-11-07).
# The box shows WAITING FOR YOU and holds the orchestrator's message until you have.
$settings.Agents = @(@{ Id = 'claude'; Name = 'Claude Code'; Command = 'claude'; Arguments = ''; Enabled = $true })
$settings.Repositories = @(
  @{ Id = 'alpha'; Name = 'Live Alpha'; LocalPath = "$R\alpha"; AddedAt = (Get-Date).ToString('o') },
  @{ Id = 'beta';  Name = 'Live Beta';  LocalPath = "$R\beta";  AddedAt = (Get-Date).ToString('o') },
  @{ Id = 'gamma'; Name = 'Live Gamma'; LocalPath = "$R\gamma"; AddedAt = (Get-Date).ToString('o') })
[IO.File]::WriteAllText("$S\settings.json", ($settings | ConvertTo-Json -Depth 20))

$jobs = @{
  'Live Alpha' = 'The job: build a small Python command-line todo app in this folder (todo.py with add, list and done commands, JSON file storage) with pytest tests. Commit after each step. Tell the session one step at a time, check its claims, and report to me in one line after each turn.'
  'Live Beta'  = 'The job: build a small Python command-line unit converter in this folder (convert.py: length, mass and temperature, with a table of units) with pytest tests. Commit after each step. One step at a time; report to me in one line after each turn.'
  'Live Gamma' = 'The job: build a small Python Markdown-to-HTML converter in this folder (md2html.py handling headings, paragraphs, lists, bold and italics) with pytest tests. Commit after each step. One step at a time; report to me in one line after each turn.'
}

$env:AGENTHUB_SETTINGS_DIR = $S; $env:AGENTHUB_RUNS_DIR = "$T\runs"; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = "$T\pane-orchestrators"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"
foreach ($v in 'CLAUDECODE', 'CLAUDE_CODE_ENTRYPOINT', 'CLAUDE_CODE_SESSION_ID', 'CLAUDE_CODE_CHILD_SESSION', 'AGENTHUB_ORCHESTRATOR_COMMAND') { Remove-Item "env:$v" -ErrorAction SilentlyContinue }
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 3
  Click (FindName $main '*Add Terminal'); Start-Sleep 1
  Click (FindName $main '*Add Terminal'); Start-Sleep 2
  Log "1. Three Claude Code sessions"
  $order = @('Live Alpha', 'Live Beta', 'Live Gamma')
  foreach ($repo in $order) {
    # Each idle pane shows the launcher; the first launcher in tree order is the next idle pane.
    Click (FindName $main $repo); Start-Sleep 1
    Click (FindName $main 'Claude Code'); Start-Sleep 1
    Click (FindName $main '➕ New session'); Start-Sleep 8
    Log "   started $repo"
  }
  Start-Sleep 10
  Shot $main 'l-sessions.png'
  Check ((Named $main '*Orchestrate').Count -eq 3) 'three running sessions each offer 🧭 Orchestrate'

  Log "2. An orchestrator on each, with its job"
  foreach ($repo in $order) {
    Click (Named $main '*Orchestrate')[0]
    Click (MenuItem 'Orchestrator with no preset context'); Start-Sleep 2
  }
  Check ((Texts $main 'LISTENING').Count -eq 3) 'three boxes listening'
  $inputs = Edits $main 'Message the orchestrator'; $sends = Named $main 'Send'
  Check ($inputs.Count -eq 3 -and $sends.Count -eq 3) 'three message lines'
  for ($k = 0; $k -lt 3; $k++) {
    $inputs[$k].GetCurrentPattern($Val).SetValue($jobs[$order[$k]]); Click $sends[$k]; Start-Sleep 1
  }
  Start-Sleep 5
  Shot $main 'l-attached.png'

  Log "3. Watching for handoffs (up to $Minutes min)"
  $deadline = (Get-Date).AddMinutes($Minutes); $n = 0; $done = $false; $seenHandoff = @{}; $seenSecond = @{}
  while ((Get-Date) -lt $deadline -and -not $done) {
    Start-Sleep 20; $n++
    $titles = Texts $main '🧭 Orchestrator*' | ForEach-Object { $_.Current.Name }
    $states = @('LISTENING', 'ATTACHED', 'THINKING…', 'HANDING OFF') | ForEach-Object { $s = $_; "$s=$((Texts $main $s).Count)" }
    $handoffs = (Texts $main 'Handoff: context window at*').Count
    $second = @($titles | Where-Object { $_ -like '*session #2*' }).Count
    $briefed = (Texts $main '*Session #1 ended*').Count
    Log ("   tick $n · $($states -join ' ') · handoff notes $handoffs · on session #2: $second · restarted: $briefed · titles: " + ($titles -join ' | '))
    if ($n % 6 -eq 0) { Shot $main ("l-tick-{0:d3}.png" -f $n) }
    if ($handoffs -ge 1 -and -not $seenHandoff.Count) { $seenHandoff['t'] = $n; Shot $main 'l-handoff-requested.png' }
    if ($second -ge 1 -and -not $seenSecond.Count) { $seenSecond['t'] = $n; Shot $main 'l-first-restart.png' }
    if ($second -ge 3) {
      # Each fresh session must have been given its first instruction: a sent message after the restart note.
      $sentAfter = 0
      foreach ($box in @($main.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like '*Session #1 ended*' })) { $sentAfter++ }
      if ($sentAfter -ge 3) { Start-Sleep 60; $done = $true }
    }
  }
  Shot $main 'l-final.png'
  $titles = Texts $main '🧭 Orchestrator*' | ForEach-Object { $_.Current.Name }
  Check ((Texts $main 'Handoff: context window at*').Count -ge 3) 'every session was asked for a handoff when its context filled'
  Check (@($titles | Where-Object { $_ -like '*session #2*' }).Count -eq 3) 'every pane is on session #2 (fresh session started in the same pane)'
  Check ((Texts $main '*Session #1 ended*').Count -eq 3) 'every box notes the restart'
  Check ((Texts $main '*ORCHESTRATOR' | Where-Object { $_.Current.Name -ceq '🧠 ORCHESTRATOR' }).Count -eq 3) 'all three boxes still attached'
  Check ((Named $main 'Relaunch').Count -eq 3) 'all three sessions running'
  foreach ($n2 in 'alpha', 'beta', 'gamma') {
    $h = Join-Path $R "$n2\.agenthub\handoff.md"
    Check (Test-Path $h) "$n2 wrote .agenthub\handoff.md"
    $commits = (git -C (Join-Path $R $n2) log --oneline | Measure-Object -Line).Lines
    Log "   $n2 commits: $commits"
  }
  Log "4. Detach all three"
  for ($k = 0; $k -lt 3; $k++) { Click (Named $main '*Detach')[0]; Start-Sleep 1 }
  Check ((Texts $main '*ORCHESTRATOR' | Where-Object { $_.Current.Name -ceq '🧠 ORCHESTRATOR' }).Count -eq 0) 'no boxes after detach'
  Check ((Named $main 'Relaunch').Count -eq 3) 'sessions still running after detach'
  Shot $main 'l-detached.png'
} catch {
  Log "ERROR: $_"; $script:fails++
  try { Shot $main 'l-error.png' } catch {}
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
Log "Failures: $script:fails"
exit $script:fails
