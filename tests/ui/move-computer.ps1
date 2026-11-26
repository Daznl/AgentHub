# UI test: Settings → Move to another computer, on a TEST AgentHub with its own settings and small made-up folders
# (a few files), so it is quick and never reads or writes the owner's real sessions. Safe to run as often as needed.
# Computer A packs; computer B (other folders, one file already there and different) unpacks; a restart merges settings.
# UI Automation patterns only: no mouse, no keyboard, no focus stealing. Screenshots go to C:\data\agenthub\uitest\.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.IO.Compression.FileSystem
Add-Type -Name W -Namespace '' -MemberDefinition @'
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
'@
$Shots = 'C:\data\agenthub\uitest'; New-Item -ItemType Directory -Force $Shots | Out-Null
$Root = 'C:\data\agenthub\test-runs\move ui'   # a space on purpose
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern
$Value = [System.Windows.Automation.ValuePattern]::Pattern
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Find($root, $name, [int]$wait = 15) {
  for ($i = 0; $i -lt $wait * 4; $i++) { try { $e = $root.FindFirst($TS::Descendants, (Cond $A::NameProperty $name)); if ($e) { return $e } } catch { }; Start-Sleep -Milliseconds 250 }
  return $null }
function Shot($el, $file) {
  Start-Sleep -Milliseconds 700   # let WPF render the last change before the capture
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose()
  "  shot  $Shots\$file" }
function MoveWindow($proc) {
  # Owned by the main window, so UI Automation lists it under the main window rather than at the top level
  $title = 'AgentHub - Move to another computer'
  for ($i = 0; $i -lt 120; $i++) {
    try {
      foreach ($w in $A::RootElement.FindAll($TS::Children, (Cond $A::ProcessIdProperty $proc.Id))) {
        if ($w.Current.Name -eq $title) { return $w }
        $c = $w.FindFirst($TS::Children, (Cond $A::NameProperty $title)); if ($c) { return $c } }
    } catch { }
    Start-Sleep -Milliseconds 250 }
  throw 'Move window did not open' }
function Texts($win) {
  # Status text changes while working; an element can vanish mid-listing, so a failed listing is tried again
  for ($try = 0; $try -lt 5; $try++) { try { return @($win.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }) } catch { Start-Sleep -Milliseconds 200 } }
  return @() }
function WaitText($win, $pattern, [int]$seconds) {
  for ($i = 0; $i -lt $seconds * 2; $i++) {
    $t = Texts $win | Where-Object { $_ -match $pattern } | Select-Object -First 1
    if ($t) { return $t }; Start-Sleep -Milliseconds 500 }
  return $null }
function Write-File($path, $text) { New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null; [IO.File]::WriteAllText($path, $text) }

# One made-up computer: its own Claude/Codex homes, data folders and settings (the owner's settings, panes cleared).
function Computer($name) {
  $c = [ordered]@{ Root = "$Root\$name"; Claude = "$Root\$name\claude home"; Codex = "$Root\$name\codex home"; Settings = "$Root\$name\settings"
    Archive = "$Root\$name\history"; Runs = "$Root\$name\runs"; Orch = "$Root\$name\pane-orchestrators"; Flows = "$Root\$name\workflows" }
  New-Item -ItemType Directory -Force $c.Settings | Out-Null
  $s = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
  $s.PaneLayout = @(); $s.SessionsDirectory = "$($c.Root)\sessions"; $s.HistoryArchiveEnabled = $false; $s.SessionsOutliveAgentHub = $false
  $s.HistoryArchiveDirectory = $c.Archive
  [IO.File]::WriteAllText("$($c.Settings)\settings.json", ($s | ConvertTo-Json -Depth 20))
  return $c }
function Use($c) {
  $env:AGENTHUB_SETTINGS_DIR = $c.Settings; $env:CLAUDE_CONFIG_DIR = $c.Claude; $env:CODEX_HOME = $c.Codex
  $env:AGENTHUB_RUNS_DIR = $c.Runs; $env:AGENTHUB_PANE_ORCHESTRATORS_DIR = $c.Orch; $env:AGENTHUB_WORKFLOWS_DIR = $c.Flows }

Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
$pcA = Computer 'computer a'; $pcB = Computer 'computer b'
$proj = 'C--Work-Repo'
Write-File "$($pcA.Claude)\CLAUDE.md" 'rules from computer A'
Write-File "$($pcA.Claude)\.credentials.json" '{"token":"never packed"}'
Write-File "$($pcA.Claude)\skills\demo\SKILL.md" 'a skill'
Write-File "$($pcA.Claude)\projects\$proj\s1.jsonl" ('{"type":"user","cwd":"C:\\Work\\Repo","message":{"content":"one"}}' + "`n" + '{"type":"user","cwd":"C:\\Work\\Repo","message":{"content":"two"}}' + "`n")
Write-File "$($pcA.Archive)\claude\$proj\cleaned-up.jsonl" ('{"type":"user","message":{"content":"only in the archive"}}' + "`n")
Write-File "$($pcA.Codex)\config.toml" 'model = "x"'
Write-File "$($pcA.Codex)\auth.json" '{"key":"never packed"}'
Write-File "$($pcA.Runs)\r1\run.json" '{}'
Write-File "$($pcB.Claude)\CLAUDE.md" 'rules already on computer B'
Write-File "$($pcB.Claude)\projects\$proj\s1.jsonl" ('{"type":"user","cwd":"C:\\Work\\Repo","message":{"content":"one"}}' + "`n")
$Zip = "$Root\out\AgentHub-move-test.zip"

"1. Pack on computer A"
Use $pcA
$p = Start-Process $Exe -ArgumentList '--move' -PassThru
try {
  $win = MoveWindow $p
  $pack = Find $win '📦 Pack'
  for ($i = 0; $i -lt 60 -and -not $pack.Current.IsEnabled; $i++) { Start-Sleep -Milliseconds 250 }
  Check $pack.Current.IsEnabled 'Pack is ready once the files are counted'
  Check ($null -ne (WaitText $win '^nothing here$' 2)) 'empty items say "nothing here"'
  (Find $win 'Save the zip as').GetCurrentPattern($Value).SetValue($Zip)
  Shot $win 'move-pack.png'
  $pack.GetCurrentPattern($Invoke).Invoke()
  $done = WaitText $win '^Packed ' 60
  Check ($null -ne $done) "packed: $($done -split "`n" | Select-Object -First 1)"
  Shot $win 'move-packed.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

$z = [IO.Compression.ZipFile]::OpenRead($Zip)
try {
  $names = @($z.Entries | ForEach-Object FullName)
  Check (-not ($names | Where-Object { $_ -match '(^|/)(\.credentials\.json|auth\.json)$' })) 'no sign-in files in the zip'
  Check ($names -contains "items/claude-projects/$proj/s1.jsonl") 'sessions packed'
  Check ($names -contains "items/history-claude/$proj/cleaned-up.jsonl") 'archive-only session packed'
} finally { $z.Dispose() }

"2. Unpack on computer B"
Use $pcB
$p = Start-Process $Exe -ArgumentList '--move', "`"$Zip`"" -PassThru
try {
  $win = MoveWindow $p
  $unpack = Find $win '📥 Unpack'
  for ($i = 0; $i -lt 40 -and -not $unpack.Current.IsEnabled; $i++) { Start-Sleep -Milliseconds 250 }
  Check $unpack.Current.IsEnabled 'Unpack is ready once the package is read'
  Check ($null -ne (WaitText $win '^Packed on ' 2)) 'shows where the package came from'
  Check ($null -ne (Find $win ("New folder for " + $pcA.Claude) 2)) 'offers the folder pairs to rewrite'
  Shot $win 'move-unpack.png'
  $unpack.GetCurrentPattern($Invoke).Invoke()
  $summary = WaitText $win 'added, .* completed' 60
  Check ($null -ne $summary) "unpacked: $($summary -split "`n" | Select-Object -First 1)"
  Check ($null -ne (Find $win 'Use the imported copy' 5)) 'the differing CLAUDE.md is offered, not overwritten'
  Check ($null -ne (Find $win '↻ Restart AgentHub' 5)) 'restart offered to merge the settings'
  Shot $win 'move-unpacked.png'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

Check ((Get-Content "$($pcB.Claude)\CLAUDE.md" -Raw) -eq 'rules already on computer B') "computer B's CLAUDE.md kept"
Check ((Get-Content "$($pcB.Claude)\projects\$proj\s1.jsonl" -Raw) -match 'two') 'the session B had only the start of is completed'
Check (Test-Path "$($pcB.Archive)\claude\$proj\cleaned-up.jsonl") 'archive-only session in B''s archive'
Check (Test-Path "$($pcB.Claude)\skills\demo\SKILL.md") 'skill copied'
Check (-not (Test-Path "$($pcB.Claude)\.credentials.json")) 'no sign-in copied'

"3. Restart: settings merged at start"
$p = Start-Process $Exe -PassThru
try {
  for ($i = 0; $i -lt 60 -and (Test-Path "$($pcB.Settings)\settings.import-pending.json"); $i++) { Start-Sleep -Milliseconds 500 }
  Check (-not (Test-Path "$($pcB.Settings)\settings.import-pending.json")) 'pending settings merged'
  Check ((Get-ChildItem $pcB.Settings -Filter 'settings.before-import-*.json').Count -eq 1) 'settings before the merge kept'
} finally { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }

Remove-Item $Root -Recurse -Force -ErrorAction SilentlyContinue
if ($script:fails -gt 0) { "FAILED: $script:fails"; exit 1 } else { 'ALL PASS' }
