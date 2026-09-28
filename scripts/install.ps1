<#
.SYNOPSIS
    Installs SoloSpeaker on this machine.

.DESCRIPTION
    Deployment option A (see docs/adr/0003-single-file-exe-packaging.md): there is no
    installer. This script copies the self-contained executable into a per-user location
    and registers a logon task to start it.

    Nothing here requires administrator rights. Render-endpoint mute and RegisterHotKey
    are per-user operations, and an elevated SoloSpeaker could not receive hotkeys from a
    non-elevated foreground window.

.PARAMETER SourceExe
    Path to the published SoloSpeaker.exe. Defaults to the sibling artifacts/publish
    folder produced by 'dotnet publish'.

.PARAMETER InstallRoot
    Destination folder. Defaults to %LOCALAPPDATA%\SoloSpeaker\bin.

.PARAMETER StartupDelay
    Delay after logon before starting. See the comment on the task registration below for
    why this is not zero.

.EXAMPLE
    .\install.ps1 -SourceExe .\SoloSpeaker.exe
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $SourceExe = (Join-Path $PSScriptRoot '..\artifacts\publish\SoloSpeaker.exe'),
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'SoloSpeaker\bin'),
    [timespan] $StartupDelay = ([timespan]::FromSeconds(30))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'SoloSpeaker'

if (-not (Test-Path -LiteralPath $SourceExe)) {
    throw "Executable not found at '$SourceExe'. Run 'dotnet publish src/SoloSpeaker.App/SoloSpeaker.App.csproj -c Release -o artifacts/publish' first."
}

$targetExe = Join-Path $InstallRoot 'SoloSpeaker.exe'

# An already-running instance holds a lock on the executable, and replacing the binary
# underneath a process that is mid-mute is exactly how an endpoint gets orphaned. Stop it
# first and let its graceful-exit path restore audio and clear the ledger (design.md 7.6).
$running = Get-Process -Name 'SoloSpeaker' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping the running instance so it can restore audio before upgrade...'
    foreach ($process in $running) {
        if ($PSCmdlet.ShouldProcess("PID $($process.Id)", 'Stop SoloSpeaker')) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(10000)) {
                throw "SoloSpeaker (PID $($process.Id)) did not exit within 10s. Close it by hand, confirm audio is restored, then re-run."
            }
        }
    }
}

if ($PSCmdlet.ShouldProcess($InstallRoot, 'Create install directory')) {
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
}

if ($PSCmdlet.ShouldProcess($targetExe, 'Copy executable')) {
    Copy-Item -LiteralPath $SourceExe -Destination $targetExe -Force

    # A file copied from a network share or downloaded from the CI artifact carries the
    # mark of the web, and SmartScreen will block it. Option A has no code signing, so
    # clearing MOTW here is the deliberate substitute.
    Unblock-File -LiteralPath $targetExe
}

# Task Scheduler rather than the Run key, for two reasons. The delay is expressible, and
# startup ledger replay (design.md 7.3) needs the audio endpoint enumerable -- a Run-key
# launch races the audio service at logon, and a failed enumeration raises the error tray
# state on every boot.
if ($PSCmdlet.ShouldProcess($taskName, 'Register logon task')) {
    $action = New-ScheduledTaskAction -Execute $targetExe
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
    $trigger.Delay = 'PT{0}S' -f [int]$StartupDelay.TotalSeconds

    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -DontStopOnIdleEnd `
        -ExecutionTimeLimit ([timespan]::Zero) `
        -RestartCount 3 `
        -RestartInterval ([timespan]::FromMinutes(1))

    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Trigger $trigger `
        -Settings $settings `
        -RunLevel Limited `
        -Force | Out-Null
}

Write-Host ''
Write-Host "Installed to $targetExe"
Write-Host "Registered logon task '$taskName' with a $([int]$StartupDelay.TotalSeconds)s delay."
Write-Host ''
Write-Host 'Next: pair this machine with its peer. Both machines must be paired before'
Write-Host 'either will mute -- an unpaired machine has no roster, and the design.md 5.5'
Write-Host 'predicate is false without one, so both stay audible.'
Write-Host ''
Write-Host '  First machine:  SoloSpeaker.exe --pair-init'
Write-Host '  Second machine: SoloSpeaker.exe --pair-join <bundle>'
Write-Host ''
Write-Host 'To remove: scripts\uninstall.ps1'
