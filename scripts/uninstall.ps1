<#
.SYNOPSIS
    Removes SoloSpeaker from this machine, restoring any endpoint it muted.

.DESCRIPTION
    This script is the uninstall path that design.md 7.3 requires: it invokes
    'SoloSpeaker.exe --restore' to replay and clear the mutation ledger BEFORE deleting
    the executable. Order matters. Deleting the binary first strands a muted endpoint
    with nothing left on disk able to repair it.

    If --restore fails, the script ABORTS WITHOUT REMOVING ANYTHING. That is deliberate:
    a failed restore means an endpoint may still be muted, and the binary plus the ledger
    are exactly the two things capable of repairing it later. An earlier version warned
    and continued, deleting both -- which produced design.md's highest-priority failure,
    a machine muted with nothing able to unmute it.

.PARAMETER InstallRoot
    Where install.ps1 placed the executable. Defaults to %LOCALAPPDATA%\SoloSpeaker\bin.

.PARAMETER KeepConfig
    Keep config.json, which holds pairId, pairKey, and the roster. Use this when
    reinstalling, so the pairing ceremony does not have to be repeated. This does not
    govern the ledger; the restore gate above does.

.PARAMETER Force
    Remove SoloSpeaker even though --restore failed. Only use this after confirming by
    hand, from the Windows volume mixer, that this machine is audible.

.EXAMPLE
    .\uninstall.ps1

.EXAMPLE
    .\uninstall.ps1 -KeepConfig
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path $env:LOCALAPPDATA 'SoloSpeaker\bin'),
    [switch] $KeepConfig,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$taskName = 'SoloSpeaker'
$stateRoot = Join-Path $env:LOCALAPPDATA 'SoloSpeaker'
$targetExe = Join-Path $InstallRoot 'SoloSpeaker.exe'

# 1. Stop the running instance. Its graceful-exit path restores endpoints and clears the
#    ledger; step 2 then covers the case where it did not exit gracefully.
#
#    CloseMainWindow cannot stop a tray app: MainWindowHandle is zero for a process with no
#    visible top-level window, so the call returns false having sent nothing. Until the app
#    grows a real shutdown channel (a --shutdown switch or a named event - a work item 12
#    requirement), the only correct behaviour here is to notice and refuse to continue.
$running = Get-Process -Name 'SoloSpeaker' -ErrorAction SilentlyContinue
foreach ($process in $running) {
    if ($PSCmdlet.ShouldProcess("PID $($process.Id)", 'Stop SoloSpeaker')) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(10000)) {
            throw "SoloSpeaker (PID $($process.Id)) is still running after 10s. Nothing has been removed. Close it by hand, confirm this machine is audible, then re-run."
        }
    }
}

# 2. Replay the ledger before the binary disappears. This is the whole reason this script
#    exists rather than a folder delete, and steps 4 and 5 are gated on it succeeding.
#
#    Start-Process -Wait, NOT the call operator. SoloSpeaker.exe is a Windows-subsystem
#    binary, and PowerShell does not wait on one: '& $exe' returns immediately and leaves
#    $LASTEXITCODE reflecting the launch rather than the exit. Verified by experiment -- a
#    WinExe returning 42 reported $LASTEXITCODE 0. This script therefore used to read
#    success unconditionally and delete the binary and the ledger on that reading, which is
#    precisely the Goal 1 failure the branch below exists to prevent.
$restored = $false
if (Test-Path -LiteralPath $targetExe) {
    if ($PSCmdlet.ShouldProcess($targetExe, 'Run --restore')) {
        $restoreProcess = Start-Process -FilePath $targetExe -ArgumentList '--restore' -Wait -PassThru -NoNewWindow
        $restoreExitCode = $restoreProcess.ExitCode
        if ($restoreExitCode -eq 0) {
            $restored = $true
        }
        else {
            Write-Warning "'--restore' exited with code $restoreExitCode. An endpoint may still be muted."
        }
    }
    else {
        # -WhatIf: nothing ran, so nothing was restored, and nothing should be deleted.
        $restored = $false
    }
}
else {
    Write-Warning "SoloSpeaker.exe was not found at '$targetExe', so the ledger could not be replayed."
}

# The Goal 1 branch. design.md section 2 goal 1 is that no reachable state leaves a machine
# muted with nothing able to unmute it -- so when the repair fails, the two things capable
# of performing it later are exactly what must NOT be deleted. Removing the binary and
# %LOCALAPPDATA%\SoloSpeaker here would destroy both the tool and the record it reads.
if (-not $restored) {
    Write-Host ''
    Write-Warning 'Uninstall aborted. Nothing has been removed.'
    Write-Host ''
    Write-Host "The mutation ledger and SoloSpeaker.exe are both still in place, so the"
    Write-Host "repair can still be attempted:"
    Write-Host ''
    Write-Host "    $targetExe --restore"
    Write-Host ''
    Write-Host 'If that keeps failing, unmute this machine from the Windows volume mixer'
    Write-Host 'and then re-run this script with -Force to remove SoloSpeaker anyway.'
    if (-not $Force) {
        exit 1
    }
    Write-Warning '-Force was supplied; continuing with removal despite the failed restore.'
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
#    than left behind as a stale secret. -KeepConfig preserves the pairing; it is not a
#    ledger switch, because the ledger is governed by the restore gate above.
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
    Write-Host 'Uninstalled with -Force after a failed restore.'
    Write-Host 'Confirm this machine is audible before you walk away from it.'
}
Write-Host ''
Write-Host 'Remember to uninstall on the peer machine too. A paired machine left running'
Write-Host 'keeps its persisted ownership but will simply find no peer, so it stays'
Write-Host 'audible -- design.md 5.5 never mutes without an affirmatively present peer.'
