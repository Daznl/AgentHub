# LIVE UI test: real orchestrator + real worker in a pane, tiny context window, forced handoff. Spends Claude usage.
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

# ---- LIVE: a real orchestrator (claude -p) drives a real interactive Claude Code worker in a Cockpit pane, with a tiny
# context window (claudeContextWindowTokens) so a forced handoff and a fresh worker session happen within a few turns.
# Spends Claude usage. Passes when: several messages were sent and each arrived whole, a handoff happened, worker
# session 2 started and the orchestrator kept driving it, and the worker never asked a person anything it was stuck on.
$Window = 90000   # a fresh Claude Code session already uses about 46k; the 75% limit is 67.5k
$Minutes = 45
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$T\projects" | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"; $settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false; $settings.AnimatedBackground = $false
$settings | Add-Member -Force NoteProperty ClaudeContextWindowTokens $Window
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))
$spec = 'Tally: a tiny Node.js command-line tool with no dependencies. Build it in four separate slices, one per turn, committing after each. ' +
  'Slice 1: tally.js add <name> [n] adds n (default 1) to a named counter kept in tally.json. ' +
  'Slice 2: tally.js list prints every counter sorted by value, and tally.js reset <name> removes one. ' +
  'Slice 3: tally.js undo reverts the last add or reset, keeping a short history in tally.json. ' +
  'Slice 4: tally.js export --csv prints the counters as CSV. ' +
  'Each slice needs node:test tests that node --test runs, and a README section. Keep everything small.'
$proj = Join-Path "$T\projects" 'Tally'
$ideas = @(@{ Id = 'liveone'; Title = 'Tally'; Description = $spec; TargetDirectory = $proj; AgentCommand = 'claude'; ContextHandoffThreshold = 0.75; Status = 0; WorkflowId = 'template:builder' })
[IO.File]::WriteAllText("$T\ideas.json", (ConvertTo-Json -InputObject $ideas -Depth 5))

$env:AGENTHUB_SETTINGS_DIR = $T; $env:AGENTHUB_RUNS_DIR = "$T\runs"
$env:AGENTHUB_WORKFLOWS_DIR = "$T\workflows"; $env:AGENTHUB_PROJECTS_DIR = "$T\projects"
$log = Join-Path $Shots "live-$(Get-Date -Format yyyyMMdd-HHmmss).log"
function Say($s) { $line = "[$(Get-Date -Format HH:mm:ss)] $s"; $line
  for ($r = 0; $r -lt 10; $r++) {   # a watcher may have the log open: append with shared access
    try { $fs = New-Object IO.FileStream($log, 'Append', 'Write', 'ReadWrite'); $b = [Text.Encoding]::UTF8.GetBytes($line + "`r`n"); $fs.Write($b, 0, $b.Length); $fs.Close(); break } catch { Start-Sleep -Milliseconds 200 } } }
# Run from a Claude Code shell, the app would hand its CLAUDECODE/CLAUDE_*/AI_AGENT variables (and any API key) to the worker,
# whose claude then runs in print mode and exits at once. the owner starts AgentHub from the taskbar without them.
Get-ChildItem env: | Where-Object { $_.Name -like 'CLAUDE*' -or $_.Name -like 'ANTHROPIC*' -or $_.Name -eq 'AI_AGENT' } | ForEach-Object { Remove-Item "env:$($_.Name)" }
$p = Start-Process $Exe -PassThru; $script:procId = $p.Id
try {
  $main = $null
  for ($i = 0; $i -lt 80 -and -not $main; $i++) { $main = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  Click (FindName $main '*Orchestration')
  $orch = $null
  for ($i = 0; $i -lt 40 -and -not $orch; $i++) { $orch = $main.FindFirst($TS::Children, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window))); Start-Sleep -Milliseconds 250 }
  Start-Sleep 2
  $list = Find $orch 'IdeasList'
  Click (FindName (ListItems $list)[0] '*Build Now')
  Say "Build Now pressed"

  $seen = 0; $deadline = (Get-Date).AddMinutes($Minutes); $n = 0
  while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    Start-Sleep 10; $n++
    $runDir = Get-ChildItem "$T\runs" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $runDir -or -not (Test-Path "$($runDir.FullName)\events.jsonl")) { continue }
    $ev = @(Get-Content "$($runDir.FullName)\events.jsonl" | ForEach-Object { $_ | ConvertFrom-Json })
    for ($k = $seen; $k -lt $ev.Count; $k++) {
      $e = $ev[$k]; $d = "$($e.detail)$($e.toWorker)"; if ($d.Length -gt 160) { $d = $d.Substring(0, 160) + '...' }
      Say ("{0} w{1} {2} {3} {4}" -f $e.kind, $e.workerSession, $e.action, $(if ($e.flag) { 'FLAG' } else { '' }), $d) }
    $seen = $ev.Count
    if ($n % 6 -eq 0) { Shot $main ("live-{0:000}.png" -f $n) }
    # The orchestrator box above the worker's pane: once a reply has been read it shows it, and the message that followed.
    if (-not $script:boxRead) { $r = @($main.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | Where-Object { $_.Current.Name -like 'Last read: *' -and $_.Current.Name -notlike 'Last read: nothing yet*' }); if ($r.Count -gt 0) { $script:boxRead = $r[0].Current.Name; Say "box: $($script:boxRead.Substring(0, [Math]::Min(140, $script:boxRead.Length)))"; Shot $main 'live-box-read.png' } }
    $s2turns = @($ev | Where-Object { $_.kind -eq 'turn' -and $_.workerSession -ge 2 -and $_.action -eq 'send' }).Count
    $done = Test-Path "$($runDir.FullName)\REVIEW.md"
    $paused = @($ev | Where-Object { $_.kind -eq 'paused' }).Count -gt 0
    if ($s2turns -ge 2 -or $done -or $paused) { Start-Sleep 50; break }   # let the delivery check of the last send finish
  }
  Shot $main 'live-end.png'
  $ev = @(Get-Content "$($runDir.FullName)\events.jsonl" | ForEach-Object { $_ | ConvertFrom-Json })
  $sends = @($ev | Where-Object { $_.kind -eq 'turn' -and $_.action -eq 'send' })
  "Results"
  Check ($script:boxRead -ne $null) 'the orchestrator box showed a reply it read from the worker'
  Check ($sends.Count -ge 3) "the orchestrator kept reading and sending ($($sends.Count) messages)"
  Check ((@($ev | Where-Object { $_.kind -eq 'delivery_cut' })).Count -eq 0) 'every message reached the worker whole (no delivery_cut)'
  Check ((@($ev | Where-Object { $_.kind -in 'handoff', 'forced_handoff' })).Count -ge 1) 'a handoff happened'
  Check ((@($ev | Where-Object { $_.workerSession -ge 2 })).Count -ge 1) 'worker session 2 started'
  Check ((@($sends | Where-Object { $_.workerSession -ge 2 })).Count -ge 1) 'the orchestrator drove the new session'
  Check ((@($ev | Where-Object { $_.kind -in 'error', 'paused' })).Count -eq 0) 'no errors and no pause'
  Check ((@($ev | Where-Object { $_.detail -like '*timed out*' })).Count -eq 0) 'every handoff turn end was noticed (no 10-minute timeout)'
  # Every message to the worker, compared with what the worker's transcripts recorded receiving.
  $tx = Get-ChildItem "$env:USERPROFILE\.claude\projects" -Directory | Where-Object { $_.Name -like '*test-settings-repo-projects-Tally' } | Select-Object -First 1
  $received = if ($tx) { (Get-ChildItem $tx.FullName -Filter *.jsonl | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }) -join "`n" } else { '' }
  $received = ($received -replace '\\n', ' ' -replace '\\"', '"' -replace '\s+', ' ')
  $whole = 0
  foreach ($s in $sends) {
    $sent = (($s.sentToWorker, $s.toWorker | Where-Object { $_ } | Select-Object -First 1) -replace '\s+', ' ')
    $head = $sent.Substring(0, [Math]::Min(60, $sent.Length)); $tail = $sent.Substring([Math]::Max(0, $sent.Length - 40))
    if ($received.Contains($head) -and $received.Contains($tail)) { $whole++ } else { Say "NOT FOUND whole in transcripts ($($sent.Length) chars): $head ... $tail" } }
  Check ($whole -eq $sends.Count) "the worker transcripts hold every message whole ($whole of $($sends.Count), longest $(($sends | ForEach-Object { "$($_.sentToWorker)".Length } | Measure-Object -Maximum).Maximum) chars)"
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Failures: $script:fails"
exit $script:fails
