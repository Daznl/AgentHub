# UI test: a new user's past sessions show up without registering anything. On a TEST AgentHub with its own settings,
# no registered repositories and no panes, reading the real transcripts in ~/.claude/projects and ~/.codex/sessions
# (this computer needs some sessions outside AppData). UI Automation patterns only: no mouse, no keyboard.
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -Name W -Namespace '' -MemberDefinition @'
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
'@
$Shots = 'C:\data\agenthub\uitest'; New-Item -ItemType Directory -Force $Shots | Out-Null
function Shot($el, $file) {
  $h = [IntPtr]$el.Current.NativeWindowHandle; $r = New-Object W+RECT; [W]::GetWindowRect($h, [ref]$r) | Out-Null
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp)
  $dc = $g.GetHdc(); [W]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc); $bmp.Save((Join-Path $Shots $file)); $g.Dispose(); $bmp.Dispose() }
$A = [System.Windows.Automation.AutomationElement]; $TS = [System.Windows.Automation.TreeScope]
$CT = [System.Windows.Automation.ControlType]
$Invoke = [System.Windows.Automation.InvokePattern]::Pattern
$Select = [System.Windows.Automation.SelectionItemPattern]::Pattern
$T = 'C:\data\agenthub\test-settings-found-folders'
$script:fails = 0
function Check($ok, $what) { if ($ok) { "  PASS  $what" } else { "  FAIL  $what"; $script:fails++ } }
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function Find($root, $prop, $val, [int]$wait = 10) {
  for ($i = 0; $i -lt $wait * 4; $i++) { $e = $root.FindFirst($TS::Descendants, (Cond $prop $val)); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
  throw "not found: $val" }
function Visible($root, $name) { $e = $root.FindFirst($TS::Descendants, (Cond $A::NameProperty $name)); [bool]$e -and -not $e.Current.IsOffscreen }
function Settle { Start-Sleep -Milliseconds 700 }

# ---- a new user: the user's settings with no repositories, no panes, no archive, its own sessions folder ----
Remove-Item $T -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $T | Out-Null
$settings = Get-Content "$env:APPDATA\AgentHub\settings.json" -Raw | ConvertFrom-Json
$settings.Repositories = @(); $settings.PaneLayout = @(); $settings.SessionsDirectory = "$T\sessions"
$settings.HistoryArchiveEnabled = $false; $settings.SessionsOutliveAgentHub = $false
[IO.File]::WriteAllText("$T\settings.json", ($settings | ConvertTo-Json -Depth 20))

$env:AGENTHUB_SETTINGS_DIR = $T
$p = Start-Process $Exe -PassThru
try {
  $win = $null
  for ($i = 0; $i -lt 60 -and -not $win; $i++) { Start-Sleep -Milliseconds 250; $win = $A::RootElement.FindFirst($TS::Children, (Cond $A::ProcessIdProperty $p.Id)) }

  "Pane launcher"
  $header = Find $win $A::NameProperty 'PAST SESSIONS ON THIS COMPUTER' 60
  Check (-not $header.Current.IsOffscreen) 'step 1 shows a past-sessions section'
  $list = Find $win $A::NameProperty 'Folders with past sessions'
  $rows = $list.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Button))
  Check ($rows.Count -ge 1 -and $rows.Count -le 8) "the newest folders are listed, at most 8 (got $($rows.Count))"
  Check (@($rows | Where-Object { $_.Current.Name -notlike '📁 *' }).Count -eq 0) 'each row is a folder'
  Check (@($rows | Where-Object { $_.Current.HelpText -match 'AppData' }).Count -eq 0) 'no scratch folders under AppData'
  Shot $win 'found-folders-launcher.png'

  $more = $win.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty 'FoundMoreBtn'))
  if ($more -and -not $more.Current.IsOffscreen) {
    $more.GetCurrentPattern($Invoke).Invoke(); Settle
    $all = $list.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Button))
    Check ($all.Count -gt $rows.Count) "Show all lists every folder (got $($all.Count))"
    $more = $win.FindFirst($TS::Descendants, (Cond $A::AutomationIdProperty 'FoundMoreBtn'))
    Check (-not $more -or $more.Current.IsOffscreen) 'and the Show all button goes'
  }

  $first = $list.FindFirst($TS::Descendants, (Cond $A::ControlTypeProperty $CT::Button))
  $name = $first.Current.Name
  $first.GetCurrentPattern($Invoke).Invoke(); Settle
  Check (Visible $win $name) "picking '$name' chooses it as the folder"
  Check (Visible $win 'PowerShell') 'and moves on to the function type'
  Shot $win 'found-folders-picked.png'

  "History window"
  (Find $win $A::NameProperty '🕘 History').GetCurrentPattern($Invoke).Invoke()
  $hist = $null
  for ($i = 0; $i -lt 40 -and -not $hist; $i++) { Start-Sleep -Milliseconds 250
    $hist = $A::RootElement.FindFirst($TS::Descendants, (Cond $A::NameProperty 'AgentHub - Session history')) }
  if (-not $hist) { throw 'History window did not open' }
  $items = @()
  for ($i = 0; $i -lt 60 -and $items.Count -eq 0; $i++) { Start-Sleep -Milliseconds 250
    $items = @($hist.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::ListItem)) | Where-Object { $_.Current.Name -like '📁 *' }) }
  Shot $hist 'found-folders-history-open.png'
  $group = $hist.FindFirst($TS::Descendants, (Cond $A::NameProperty 'Found on this computer'))
  Check ([bool]$group) 'History lists found folders in their own group'
  Check ($items.Count -ge 1) "found folders are selectable ($($items.Count))"

  # A Git repository offers ➕ Add to repositories; a plain folder does not.
  $withOffer = $null; $withoutOffer = $null
  foreach ($item in $items | Select-Object -First 25) {
    $item.GetCurrentPattern($Select).Select(); Settle
    if (Visible $hist '➕ Add to repositories') { if (-not $withOffer) { $withOffer = $item.Current.Name } } elseif (-not $withoutOffer) { $withoutOffer = $item.Current.Name }
    if ($withOffer -and $withoutOffer) { break }
  }
  $count = (Find $hist $A::AutomationIdProperty 'SessionCountText').Current.Name
  Check ($count -match 'session') "the selected folder lists its sessions ('$count')"
  Check ([bool]$withoutOffer) "a plain folder has no Add button ($withoutOffer)"
  if ($withOffer) {
    $repo = $items | Where-Object { $_.Current.Name -eq $withOffer } | Select-Object -First 1
    $repo.GetCurrentPattern($Select).Select(); Settle
    Shot $hist 'found-folders-history.png'
    (Find $hist $A::NameProperty '➕ Add to repositories').GetCurrentPattern($Invoke).Invoke()
    Start-Sleep -Milliseconds 300
    Check (Visible $hist 'Adding…') 'the button says it is adding while Git runs'
    $plain = $withOffer.Substring(3)
    for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250
      $names = @($hist.FindAll($TS::Descendants, (Cond $A::ControlTypeProperty $CT::ListItem)) | ForEach-Object { $_.Current.Name })
      if ($names -contains $plain) { break } }
    Shot $hist 'found-folders-registered.png'
    Check ($names -contains $plain -and $names -notcontains $withOffer) "Add to repositories moves '$plain' up to the repositories (list starts: $(($names | Select-Object -First 4) -join ' | '))"
    Check (-not (Visible $hist '➕ Add to repositories')) 'and the button goes'
    $saved = Get-Content "$T\settings.json" -Raw | ConvertFrom-Json
    Check (@($saved.Repositories | Where-Object { $_.Name -eq $plain }).Count -eq 1) 'and it is saved in the settings'
  } else {
    Check $false 'no found folder is a Git repository to try Add to repositories on'
  }
} finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}
"Screenshots: $Shots"
"Failures: $script:fails"
exit $script:fails
