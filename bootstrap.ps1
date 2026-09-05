$ErrorActionPreference = 'Stop'

Write-Host "AgentHub bootstrap" -ForegroundColor Cyan

function Require-Command($name, $required = $true) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) {
        Write-Host "  [OK] $name -> $($cmd.Source)" -ForegroundColor Green
        return $true
    }
    if ($required) {
        Write-Host "  [MISSING] $name" -ForegroundColor Red
    } else {
        Write-Host "  [OPTIONAL] $name not found" -ForegroundColor Yellow
    }
    return $false
}

$hasDotnet = Require-Command dotnet
$hasGit = Require-Command git
Require-Command wt.exe $false | Out-Null
Require-Command gh $false | Out-Null
Require-Command codex $false | Out-Null
Require-Command claude $false | Out-Null
Require-Command agy $false | Out-Null

if (-not $hasDotnet -or -not $hasGit) {
    Write-Host "`nInstall missing required tools, then run this script again." -ForegroundColor Yellow
    exit 1
}

Write-Host "`nBuilding AgentHub..." -ForegroundColor Cyan
dotnet restore .\AgentHub.sln
dotnet build .\AgentHub.sln -c Debug --no-restore

Write-Host "`nStarting AgentHub..." -ForegroundColor Cyan
$exePath = Join-Path $PSScriptRoot "src\AgentHub\bin\Debug\net8.0-windows\AgentHub.exe"
if (Test-Path $exePath) {
    Start-Process -FilePath $exePath -WorkingDirectory $PSScriptRoot
    Write-Host "  [OK] AgentHub launched successfully! Window opened on your desktop." -ForegroundColor Green
} else {
    dotnet run --project .\src\AgentHub\AgentHub.csproj --no-build
}
