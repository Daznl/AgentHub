<#
.SYNOPSIS
    Sets up AgentHub from a fresh clone: checks the prerequisites, offers to install the
    missing ones with winget, builds the solution and starts the app.

.DESCRIPTION
    Required: Windows 10 1809 or later, a .NET SDK 8 or later, the .NET 8 Desktop Runtime,
    Git and the Microsoft Edge WebView2 Runtime. Optional tools (gh, wt.exe, claude, codex,
    agy) are reported but never installed.

      -CheckOnly   Report what is installed and what is missing, then stop.
      -Yes         Install missing required tools without asking (still uses winget).
      -NoLaunch    Build, but do not start the app.
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Yes,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function Write-Ok([string]$Text)      { Write-Host "  [OK]       $Text" -ForegroundColor Green }
function Write-Missing([string]$Text) { Write-Host "  [MISSING]  $Text" -ForegroundColor Red }
function Write-Optional([string]$Text){ Write-Host "  [OPTIONAL] $Text" -ForegroundColor Yellow }

function Update-SessionPath {
    # A fresh winget install changes PATH in the registry only; pick it up in this session.
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Get-DotnetPath {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $default) { return $default }
    return $null
}

function Get-WebView2Version {
    $keys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($key in $keys) {
        $pv = (Get-ItemProperty -Path $key -Name pv -ErrorAction SilentlyContinue).pv
        if ($pv -and $pv -ne '0.0.0.0') { return $pv }
    }
    return $null
}

# Each check returns $null when satisfied, or the winget id that fixes it.
function Test-Prerequisites {
    $missing = New-Object System.Collections.Generic.List[object]

    $os = [Environment]::OSVersion.Version
    if ($os.Build -ge 17763) {
        Write-Ok "Windows $($os.Major).$($os.Minor) build $($os.Build)"
    } else {
        Write-Missing "Windows build $($os.Build): AgentHub needs Windows 10 version 1809 (build 17763) or later. Update Windows; this script cannot."
        $missing.Add([pscustomobject]@{ Name = 'Windows 10 1809+'; Id = $null })
    }

    $dotnet = Get-DotnetPath
    $sdks = @()
    $desktop8 = @()
    if ($dotnet) {
        $sdks = @(& $dotnet --list-sdks 2>$null | Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 8 })
        $desktop8 = @(& $dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 8\.' })
    }

    if ($sdks.Count -gt 0) {
        Write-Ok ".NET SDK $(($sdks[-1] -split ' ')[0])"
    } else {
        Write-Missing '.NET SDK 8 or later (builds AgentHub)'
        $missing.Add([pscustomobject]@{ Name = '.NET 8 SDK'; Id = 'Microsoft.DotNet.SDK.8' })
    }

    if ($desktop8.Count -gt 0) {
        Write-Ok ".NET Desktop Runtime $(($desktop8[-1] -split ' ')[1])"
    } elseif ($sdks.Count -gt 0) {
        # A newer SDK builds AgentHub, but net8.0-windows only runs on the 8.x Desktop Runtime.
        Write-Missing '.NET 8 Desktop Runtime (runs AgentHub and its tests; a newer runtime does not count)'
        $missing.Add([pscustomobject]@{ Name = '.NET 8 Desktop Runtime'; Id = 'Microsoft.DotNet.DesktopRuntime.8' })
    } else {
        Write-Missing '.NET 8 Desktop Runtime (comes with the .NET 8 SDK above)'
    }

    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        Write-Ok "Git ($((& git --version) -replace '^git version ', ''))"
    } else {
        Write-Missing 'Git'
        $missing.Add([pscustomobject]@{ Name = 'Git'; Id = 'Git.Git' })
    }

    $webview = Get-WebView2Version
    if ($webview) {
        Write-Ok "WebView2 Runtime $webview"
    } else {
        Write-Missing 'Microsoft Edge WebView2 Runtime (draws the terminal panes)'
        $missing.Add([pscustomobject]@{ Name = 'WebView2 Runtime'; Id = 'Microsoft.EdgeWebView2Runtime' })
    }

    return ,$missing
}

Write-Host 'AgentHub bootstrap' -ForegroundColor Cyan
Write-Host "`nRequired" -ForegroundColor Cyan
$missing = Test-Prerequisites

Write-Host "`nOptional (not installed by this script)" -ForegroundColor Cyan
$optional = [ordered]@{
    'gh'     = 'GitHub CLI: sign-in and the repository browser (https://cli.github.com/)'
    'wt.exe' = 'Windows Terminal: external tabbed launches'
    'claude' = 'Claude Code'
    'codex'  = 'OpenAI Codex CLI'
    'agy'    = 'Google Antigravity CLI'
}
foreach ($name in $optional.Keys) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { Write-Ok "$name" } else { Write-Optional "$name not found. $($optional[$name])" }
}

if ($missing.Count -gt 0) {
    $installable = @($missing | Where-Object { $_.Id })
    Write-Host ''
    if ($installable.Count -gt 0) {
        Write-Host 'Install the missing tools with:' -ForegroundColor Yellow
        foreach ($m in $installable) { Write-Host "  winget install --id $($m.Id) -e" }
        Write-Host 'Or download them: https://dotnet.microsoft.com/download/dotnet/8.0, https://git-scm.com/download/win, https://developer.microsoft.com/microsoft-edge/webview2/'
    }

    if ($CheckOnly) { exit 1 }

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($installable.Count -eq 0 -or -not $winget) {
        if (-not $winget -and $installable.Count -gt 0) {
            Write-Host "`nwinget is not available. Install the tools from the links above, then run this script again." -ForegroundColor Yellow
        }
        exit 1
    }

    $answer = 'y'
    if (-not $Yes) {
        $answer = Read-Host "`nInstall them now with winget? [y/N]"
    }
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host 'Install them, open a new terminal, then run this script again.' -ForegroundColor Yellow
        exit 1
    }

    foreach ($m in $installable) {
        Write-Host "`nInstalling $($m.Name)..." -ForegroundColor Cyan
        & winget install --id $m.Id -e --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) {
            Write-Host "winget could not install $($m.Name) (exit code $LASTEXITCODE). Install it from the links above, then run this script again." -ForegroundColor Red
            exit 1
        }
    }

    Update-SessionPath
    Write-Host "`nChecking again" -ForegroundColor Cyan
    $missing = Test-Prerequisites
    if ($missing.Count -gt 0) {
        Write-Host "`nStill missing after install. Open a new terminal and run this script again." -ForegroundColor Red
        exit 1
    }
} elseif ($CheckOnly) {
    Write-Host "`nAll required tools are installed." -ForegroundColor Green
    exit 0
}

$dotnet = Get-DotnetPath

Write-Host "`nBuilding AgentHub..." -ForegroundColor Cyan
& $dotnet restore .\AgentHub.sln
if ($LASTEXITCODE -ne 0) { Write-Host 'dotnet restore failed.' -ForegroundColor Red; exit 1 }
& $dotnet build .\AgentHub.sln -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed.' -ForegroundColor Red; exit 1 }

if ($NoLaunch) {
    Write-Host "`nBuilt. Start it with: dotnet run --project .\src\AgentHub\AgentHub.csproj" -ForegroundColor Green
    exit 0
}

Write-Host "`nStarting AgentHub..." -ForegroundColor Cyan
$exePath = Join-Path $PSScriptRoot 'src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe'
Start-Process -FilePath $exePath -WorkingDirectory $PSScriptRoot
Write-Host '  AgentHub is open on your desktop.' -ForegroundColor Green
Write-Host "`nTo install it as a pinned taskbar app, see 'Install as a taskbar app' in README.md:" -ForegroundColor Cyan
Write-Host "  .\scripts\Deploy-Taskbar.ps1 -InstallDir 'C:\Apps\AgentHub'"
