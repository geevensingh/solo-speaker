<#
.SYNOPSIS
    Removes SoloSpeaker from this machine, restoring any endpoint it muted.

.DESCRIPTION
    This script is the uninstall path that design.md 7.3 requires: it invokes
    'SoloSpeaker.exe --restore' to replay and clear the mutation ledger BEFORE deleting
    the executable. Order matters. Deleting the binary first strands a muted endpoint
    with nothing left on disk able to repair it.

    If --restore fails or the executable is already gone, the script says so plainly
    rather than exiting quietly, because the symptom of the failure it is covering for is
    a silent machine.

.PARAMETER InstallRoot
    Where install.ps1 placed the executable. Defaults to %LOCALAPPDATA%\SoloSpeaker\bin.

.PARAMETER KeepConfig
    Keep config.json, which holds pairId, pairKey, and the roster. Use this when
    reinstalling, so the pairing ceremony does not have to be repeated.

.EXAMPLE
    .\uninstall.ps1

.EXAMPLE
    .\uninstall.ps1 -KeepConfig
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'SoloSpeaker\bin'),
    [switch] $KeepConfig
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'SoloSpeaker'
$stateRoot = Join-Path $env:LOCALAPPDATA 'SoloSpeaker'
$targetExe = Join-Path $InstallRoot 'SoloSpeaker.exe'

# 1. Stop the running instance. Its graceful-exit path restores endpoints and clears the
#    ledger; step 2 then covers the case where it did not exit gracefully.
$running = Get-Process -Name 'SoloSpeaker' -ErrorAction SilentlyContinue
foreach ($process in $running) {
    if ($PSCmdlet.ShouldProcess("PID $($process.Id)", 'Stop SoloSpeaker')) {
        $process.CloseMainWindow() | Out-Null
        $process.WaitForExit(10000) | Out-Null
    }
}

# 2. Replay the ledger before the binary disappears. This is the whole reason this script
#    exists rather than a folder delete.
$restored = $false
if (Test-Path -LiteralPath $targetExe) {
    if ($PSCmdlet.ShouldProcess($targetExe, 'Run --restore')) {
        & $targetExe '--restore'
        if ($LASTEXITCODE -eq 0) {
            $restored = $true
        }
        else {
            Write-Warning "'--restore' exited with code $LASTEXITCODE. An endpoint may still be muted."
            Write-Warning 'Check the Windows volume mixer and unmute by hand if needed.'
        }
    }
}
else {
    Write-Warning "SoloSpeaker.exe was not found at '$targetExe', so the ledger could not be replayed."
    Write-Warning 'If a machine is unexpectedly silent, unmute it from the Windows volume mixer.'
}

# 3. Remove the logon task.
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($task -and $PSCmdlet.ShouldProcess($taskName, 'Unregister logon task')) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}

# 4. Remove the binary.
if ((Test-Path -LiteralPath $InstallRoot) -and $PSCmdlet.ShouldProcess($InstallRoot, 'Delete install directory')) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}

# 5. Remove local state. config.json holds pairKey, so it is deleted by default rather
#    than left behind as a stale secret.
if (-not $KeepConfig) {
    if ((Test-Path -LiteralPath $stateRoot) -and $PSCmdlet.ShouldProcess($stateRoot, 'Delete configuration and state')) {
        Remove-Item -LiteralPath $stateRoot -Recurse -Force
    }
}
else {
    Write-Host "Kept configuration under $stateRoot (pairing preserved)."
}

Write-Host ''
if ($restored) {
    Write-Host 'Uninstalled. Audio endpoints were restored from the ledger.'
}
else {
    Write-Host 'Uninstalled, but the ledger was not replayed successfully.'
    Write-Host 'Confirm this machine is audible before you walk away from it.'
}
Write-Host ''
Write-Host 'Remember to uninstall on the peer machine too. A paired machine left running'
Write-Host 'keeps its persisted ownership but will simply find no peer, so it stays'
Write-Host 'audible -- design.md 5.5 never mutes without an affirmatively present peer.'
