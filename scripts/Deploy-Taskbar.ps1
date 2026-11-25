<#
.SYNOPSIS
    Puts the current commit into the AgentHub folder the pinned taskbar icon launches,
    and proves it by reading the commit stamped into the installed DLL.

.DESCRIPTION
    `git pull` and `dotnet publish` do not change the app the user opens: that runs from a
    separate folder. This script is the one route into that folder.

      -Check          Report only. Exit 0 if the taskbar app has the latest code, 1 if not.
      -CloseRunning   Allow closing a running taskbar app. Without it the script stops
                      before touching anything, so live work is never replaced unasked.
      -NoLaunch       Do not start the app afterwards (used for testing).
      -InstallDir     Override the folder; by default it is read from the taskbar shortcut.

    The build must come from a clean tree that contains origin/main.
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$CloseRunning,
    [switch]$NoLaunch,
    [string]$InstallDir,
    [string]$BackupRoot = 'C:\data\agenthub',

    # Internal: the detached second phase, which survives the app closing even when this
    # script was started from a pane inside AgentHub.
    [string]$SwapFrom,
    [string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
$log = Join-Path $BackupRoot 'deploy-taskbar.log'

function Write-Log([string]$Message) {
    "$(Get-Date -Format s) $Message" | Out-File -FilePath $log -Append -Encoding utf8
}

function Get-StampedCommit([string]$Dll) {
    if (-not (Test-Path -LiteralPath $Dll)) { return $null }
    $parts = (Get-Item -LiteralPath $Dll).VersionInfo.ProductVersion -split '\+'
    if ($parts.Count -lt 2) { return $null }
    return $parts[1]
}

function Get-InstallProcesses([string]$Dir) {
    $exe = Join-Path $Dir 'AgentHub.exe'
    return @(Get-Process -Name AgentHub -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
}

function Invoke-Robocopy([string]$From, [string]$To) {
    # No /MIR: files that exist only in the destination are kept.
    robocopy $From $To /E /R:5 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy '$From' -> '$To' failed with exit code $LASTEXITCODE" }
}

if ($SwapFrom) {
    try {
        Write-Log "swap start: $ExpectedCommit -> $InstallDir"
        if (Test-Path -LiteralPath (Join-Path $InstallDir 'AgentHub.dll')) {
            $old = Get-StampedCommit (Join-Path $InstallDir 'AgentHub.dll')
            $short = if ($old) { $old.Substring(0, 7) } else { 'unknown' }
            $backup = Join-Path $BackupRoot "$(Split-Path $InstallDir -Leaf).backup-$(Get-Date -Format yyyyMMdd-HHmmss)-$short"
            Invoke-Robocopy $InstallDir $backup
            Write-Log "backup ok: $backup"
        }
        Get-InstallProcesses $InstallDir | Stop-Process -Force
        Start-Sleep -Seconds 3
        Invoke-Robocopy $SwapFrom $InstallDir
        Write-Log "installed: $(Get-StampedCommit (Join-Path $InstallDir 'AgentHub.dll'))"
    }
    catch {
        Write-Log "ERROR: $_"
    }
    finally {
        # Relaunch even after a failure so the user is never left without the app.
        if (-not $NoLaunch -and (Test-Path -LiteralPath (Join-Path $InstallDir 'AgentHub.exe'))) {
            Start-Process -FilePath (Join-Path $InstallDir 'AgentHub.exe') -WorkingDirectory $InstallDir
            Write-Log 'relaunched'
        }
    }
    return
}

function Invoke-Git {
    $output = & git -C $repo @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
    return $output
}

$repo = Split-Path $PSScriptRoot -Parent

if (-not $InstallDir) {
    # Windows names a re-pinned shortcut "AgentHub (2).lnk" and so on: find the pin by what it launches, not by its name.
    $pins = Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
    $shell = New-Object -ComObject WScript.Shell
    $targets = @(Get-ChildItem -LiteralPath $pins -Filter *.lnk -ErrorAction SilentlyContinue |
        ForEach-Object { $shell.CreateShortcut($_.FullName).TargetPath } |
        Where-Object { $_ -and (Split-Path $_ -Leaf) -eq 'AgentHub.exe' } | Select-Object -Unique)
    if ($targets.Count -eq 0) { throw "No pinned AgentHub shortcut in '$pins'. Pass -InstallDir (normally C:\Apps\AgentHub)." }
    if ($targets.Count -gt 1) { throw "More than one pinned AgentHub shortcut: $($targets -join ', '). Pass -InstallDir." }
    $InstallDir = Split-Path $targets[0] -Parent
}

Invoke-Git fetch origin --quiet | Out-Null
$head = (Invoke-Git rev-parse HEAD).Trim()
$remote = (Invoke-Git rev-parse origin/main).Trim()
$installed = Get-StampedCommit (Join-Path $InstallDir 'AgentHub.dll')

# Only source changes matter: a docs-only commit does not make the app out of date.
function Test-AppCurrent([string]$Target) {
    if (-not $installed) { return $false }
    # Windows PowerShell turns redirected stderr into an error record, which Stop would throw on.
    $ErrorActionPreference = 'Continue'
    & git -C $repo cat-file -e "$installed^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { return $false }
    & git -C $repo diff --quiet $installed $Target -- src
    return $LASTEXITCODE -eq 0
}

if ($Check) {
    $current = Test-AppCurrent $remote
    Write-Host "Taskbar folder : $InstallDir"
    Write-Host "Installed      : $installed"
    Write-Host "origin/main    : $remote"
    Write-Host "Local HEAD     : $head"
    Get-Process -Name AgentHub -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "Running        : $($_.Path)" }
    if ($current) { Write-Host 'RESULT: UP TO DATE - the taskbar app has the latest code.'; exit 0 }
    Write-Host 'RESULT: OUT OF DATE - run scripts\Deploy-Taskbar.ps1.'
    exit 1
}

# Tracked changes anywhere, or new files under src, would make the build differ from the commit. A stray untracked
# file elsewhere (an image dropped in docs, say) cannot, so it does not block the install.
$dirty = @(Invoke-Git status --porcelain --untracked-files=no) + @(Invoke-Git status --porcelain -- src)
if ($dirty | Where-Object { $_ }) { throw "Working tree has uncommitted changes; commit them so the installed build matches a real commit:`n$($dirty -join "`n")" }
& git -C $repo merge-base --is-ancestor $remote $head
if ($LASTEXITCODE -ne 0) { throw "HEAD ($head) does not contain origin/main ($remote). Pull or rebase first." }

if ($installed -and (Test-AppCurrent $head)) {
    Write-Host "Already current: installed $installed has the same app code as $head."
    exit 0
}

$running = Get-InstallProcesses $InstallDir
if ($running.Count -gt 0 -and -not $CloseRunning) {
    Write-Host "AgentHub is running from '$InstallDir'. Nothing was changed."
    Write-Host 'Confirm with the user that it may close, then re-run with -CloseRunning.'
    exit 2
}

$project = Join-Path $repo 'src\AgentHub\AgentHub.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$publish = Join-Path $repo 'src\AgentHub\bin\Release\net8.0-windows\win-x64\publish'
$built = Get-StampedCommit (Join-Path $publish 'AgentHub.dll')
if ($built -ne $head) { throw "Publish output is stamped '$built', expected '$head'." }

New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null
$swapArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
    '-SwapFrom', "`"$publish`"", '-ExpectedCommit', $head,
    '-InstallDir', "`"$InstallDir`"", '-BackupRoot', "`"$BackupRoot`"")
if ($NoLaunch) { $swapArgs += '-NoLaunch' }
$swap = Start-Process powershell.exe -ArgumentList $swapArgs -WindowStyle Hidden -PassThru
$swap | Wait-Process -Timeout 180

$now = Get-StampedCommit (Join-Path $InstallDir 'AgentHub.dll')
if ($now -ne $head) { throw "Deploy FAILED: installed build is '$now', expected '$head'. See '$log'." }
if (-not $NoLaunch -and (Get-InstallProcesses $InstallDir).Count -eq 0) {
    throw "Installed $head but AgentHub did not start from '$InstallDir'. See '$log'."
}
Write-Host "DEPLOYED: $head is installed in '$InstallDir'."
