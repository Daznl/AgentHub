@echo off
rem Runs Deploy-Taskbar.ps1 where the execution policy blocks PowerShell scripts. Same switches: -Check, -CloseRunning, -InstallDir.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Deploy-Taskbar.ps1" %*
