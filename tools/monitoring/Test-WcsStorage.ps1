<#
.SYNOPSIS
    Storage check of a WCS installation, on the host's machine: free disk space, database sizes and
    growth, the latest backup (age and verification), and facts parked towards the WMS.

.DESCRIPTION
    Runs where the data folder is (it reads files, not the API). Never writes a database: the
    database checks run Wcs.DbTool verify, which opens the files read-only.

    Checks:
      disk      free space on the volume of the data folder.
      files     wcs.db, the monthly history files (wcs-history-YYYY-MM.db) and PLC log files
                (wcs-plc-YYYY-MM.db) with their -wal; a -wal above -MaxWalMB.
      growth    (with -StateFile) appends one line of sizes per run to a CSV file and reports the
                growth per day of each kind of file over the lines it holds; above
                -MaxGrowthMBPerDay is WARNING.
      backup    (with -BackupRoot) the newest folder under it that holds wcs-backup.json (the folders
                Wcs.DbTool backup writes): its age against -MaxBackupAgeHours; with -DbToolPath also
                "Wcs.DbTool verify <that folder>" (integrity, schema, SHA-256 of every file).
      parked    (with -DbToolPath) "Wcs.DbTool verify <data folder>\wcs.db", read-only on the live
                database: the rows parked in the durable queues towards the WMS (facts the WMS refused
                for good). The host writes "RIFIUTATA DEFINITIVAMENTE" in its log once, when a fact is
                parked; this count still sees it after a restart. -ParkedFrom Backup reads the count
                from the newest backup instead, without touching the live database.
      queues    (with -DbToolPath, from the same output) the facts waiting in the durable queues
                towards the WMS; above -MaxQueuedFacts (0: report only) is WARNING.

    Exit code: 0 OK, 1 WARNING, 2 CRITICAL, 3 UNKNOWN (see WcsMonitoring.Common.ps1).

    The default thresholds are starting points, not recommendations: set them from the measurements
    of your plant (guide: integration/performance-and-sizing.md).

.EXAMPLE
    pwsh -NoProfile -File tools/monitoring/Test-WcsStorage.ps1 -DataDirectory C:\Wcs\Data `
        -BackupRoot D:\WcsBackups\Scheduled -DbToolPath C:\WcsTools\DbTool\Wcs.DbTool.exe `
        -StateFile D:\WcsMonitoring\sizes.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $DataDirectory,
    [ValidateRange(0, 100)] [double] $WarnFreePercent = 20,
    [ValidateRange(0, 100)] [double] $CriticalFreePercent = 10,
    [ValidateRange(0, 1000000)] [double] $CriticalFreeGB = 0,
    [ValidateRange(1, 1000000)] [double] $MaxWalMB = 1024,
    [string] $StateFile,
    [ValidateRange(0, 1000000)] [double] $MaxGrowthMBPerDay = 0,
    [string] $BackupRoot,
    [ValidateRange(1, 100000)] [double] $MaxBackupAgeHours = 26,
    [string] $DbToolPath,
    [ValidateSet('Live', 'Backup', 'None')] [string] $ParkedFrom = 'Live',
    [ValidateRange(0, 100000000)] [int] $MaxQueuedFacts = 0,
    [ValidateRange(1, 3600)] [int] $ToolTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WcsMonitoring.Common.ps1')

function Format-MB([double] $bytes) { '{0:N1} MB' -f ($bytes / 1MB) }

# Sum of "of which parked N" in the output of Wcs.DbTool verify; $null when the line is not there.
function Get-ParkedCount([string] $output) {
    $found = [regex]::Matches($output, 'of which parked (\d+)')
    if ($found.Count -eq 0) { return $null }
    ($found | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
}

# The lines of a Wcs.DbTool run that say what failed (or its last line), on one line.
function Get-FailureLines([string] $output) {
    $lines = @($output -split "`r?`n" | Where-Object { $_.Trim() })
    $failed = @($lines | Where-Object { $_ -match 'FAILED' -and $_ -notmatch '^Verification FAILED' })
    if (-not $failed) { $failed = @($lines | Select-Object -Last 1) }
    ($failed | ForEach-Object { $_.Trim() -replace '\s{2,}', ' ' }) -join ' / '
}

function Test-Parked([string] $output, [string] $where) {
    $parked = Get-ParkedCount $output
    if ($null -eq $parked) {
        Add-WcsResult UNKNOWN 'parked' "Wcs.DbTool verify printed no count of parked rows for $where"
    } elseif ($parked -gt 0) {
        Add-WcsResult CRITICAL 'parked' "$parked fact(s) towards the WMS parked in $where (refused for good; they hold back later facts): follow the parked-facts procedure in known-limitations.md, do not edit wcs.db"
    } else {
        Add-WcsResult OK 'parked' "no parked fact in $where"
    }

    # The durable queues towards the WMS: facts not delivered yet (parked ones included). A backlog
    # that keeps growing means the WMS is not taking them.
    $queued = 0
    $parts = foreach ($name in 'transitions to the WMS', 'extraction outcomes to send', 'picked-extraction reports', 'cell give-backs') {
        $m = [regex]::Match($output, [regex]::Escape($name) + ' (\d+)')
        if ($m.Success) { $queued += [int]$m.Groups[1].Value; "$name $($m.Groups[1].Value)" }
    }
    if ($parts) {
        $textQueues = "$queued fact(s) waiting in the durable queues towards the WMS in ${where}: $($parts -join ', ')"
        if ($MaxQueuedFacts -gt 0 -and $queued -gt $MaxQueuedFacts) { Add-WcsResult WARNING 'queues' "$textQueues (more than $MaxQueuedFacts)" }
        else { Add-WcsResult OK 'queues' $textQueues }
    }
}

if (-not (Test-Path -LiteralPath $DataDirectory -PathType Container)) {
    Add-WcsResult CRITICAL 'files' "the data folder $DataDirectory does not exist"
    Complete-WcsCheck 'Test-WcsStorage'
}
$data = (Resolve-Path -LiteralPath $DataDirectory).ProviderPath

# disk
$drive = [System.IO.DriveInfo]::new([System.IO.Path]::GetPathRoot($data))
$freePercent = 100.0 * $drive.AvailableFreeSpace / $drive.TotalSize
$freeGB = $drive.AvailableFreeSpace / 1GB
$text = '{0:N1} GB free of {1:N1} GB ({2:N1}%) on {3}' -f $freeGB, ($drive.TotalSize / 1GB), $freePercent, $drive.Name
if ($freePercent -lt $CriticalFreePercent -or ($CriticalFreeGB -gt 0 -and $freeGB -lt $CriticalFreeGB)) { Add-WcsResult CRITICAL 'disk' $text }
elseif ($freePercent -lt $WarnFreePercent) { Add-WcsResult WARNING 'disk' $text }
else { Add-WcsResult OK 'disk' $text }

# files
$files = @(Get-ChildItem -LiteralPath $data -File)
$size = { param($pattern) ($files | Where-Object Name -like $pattern | Measure-Object Length -Sum).Sum ?? 0 }
$wcs = & $size 'wcs.db*'
$history = & $size 'wcs-history-*.db*'
$plc = & $size 'wcs-plc-*.db*'
$historyMonths = @($files | Where-Object Name -match '^wcs-history-\d{4}-\d{2}\.db$').Count
$plcMonths = @($files | Where-Object Name -match '^wcs-plc-\d{4}-\d{2}\.db$').Count
if (-not ($files | Where-Object Name -eq 'wcs.db')) {
    Add-WcsResult CRITICAL 'files' "no wcs.db in $data"
} else {
    Add-WcsResult OK 'files' ("wcs.db {0}; history {1} in {2} monthly file(s); PLC log {3} in {4} monthly file(s)" -f (Format-MB $wcs), (Format-MB $history), $historyMonths, (Format-MB $plc), $plcMonths)
}
foreach ($wal in ($files | Where-Object Name -like '*.db-wal')) {
    if ($wal.Length -gt $MaxWalMB * 1MB) {
        Add-WcsResult WARNING 'files' "$($wal.Name) is $(Format-MB $wal.Length): the log is not being checkpointed into its database (a long reader?)"
    }
}

# growth
if ($StateFile) {
    $now = (Get-Date).ToUniversalTime()
    $line = [pscustomobject]@{ utc = $now.ToString('o'); wcsBytes = $wcs; historyBytes = $history; plcBytes = $plc; freeBytes = $drive.AvailableFreeSpace }
    $folder = Split-Path -Parent $StateFile
    if ($folder -and -not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
    $line | Export-Csv -LiteralPath $StateFile -Append -NoTypeInformation -Encoding utf8
    $history_ = @(Import-Csv -LiteralPath $StateFile)
    $first = $history_[0]
    $days = ($now - ([datetime]$first.utc).ToUniversalTime()).TotalDays
    if ($history_.Count -lt 2 -or $days -lt (1 / 24)) {
        Add-WcsResult OK 'growth' "first measurements recorded in $StateFile; growth is reported once they span an hour"
    } else {
        $rate = { param($from, $to) ([double]$to - [double]$from) / 1MB / $days }
        $g = [ordered]@{
            'wcs.db'  = & $rate $first.wcsBytes $wcs
            'history' = & $rate $first.historyBytes $history
            'PLC log' = & $rate $first.plcBytes $plc
        }
        $textGrowth = ($g.Keys | ForEach-Object { '{0} {1:+0.00;-0.00;0} MB/day' -f $_, $g[$_] }) -join ', '
        $textGrowth += " over {0:N1} day(s), {1} measurement(s)" -f $days, $history_.Count
        $over = @($g.Keys | Where-Object { $MaxGrowthMBPerDay -gt 0 -and $g[$_] -gt $MaxGrowthMBPerDay })
        if ($over) { Add-WcsResult WARNING 'growth' "$textGrowth; above $MaxGrowthMBPerDay MB/day: $($over -join ', ')" }
        else { Add-WcsResult OK 'growth' $textGrowth }
        $freeRate = ([double]$first.freeBytes - [double]$drive.AvailableFreeSpace) / $days
        if ($freeRate -gt 0) {
            Add-WcsResult OK 'growth' ('at the current rate the volume fills in {0:N0} day(s)' -f ($drive.AvailableFreeSpace / $freeRate))
        }
    }
}

$tool = $null
if ($DbToolPath) {
    if (Test-Path -LiteralPath $DbToolPath -PathType Leaf) { $tool = (Resolve-Path -LiteralPath $DbToolPath).ProviderPath }
    else { Add-WcsResult UNKNOWN 'dbtool' "Wcs.DbTool not found at $DbToolPath" }
}

# backup
$newestBackup = $null
if ($BackupRoot) {
    if (-not (Test-Path -LiteralPath $BackupRoot -PathType Container)) {
        Add-WcsResult CRITICAL 'backup' "the backup folder $BackupRoot does not exist"
    } else {
        $newestBackup = Get-ChildItem -LiteralPath $BackupRoot -Directory |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'wcs-backup.json') } |
            Sort-Object { (Get-Item -LiteralPath (Join-Path $_.FullName 'wcs-backup.json')).LastWriteTimeUtc } -Descending |
            Select-Object -First 1
        if (-not $newestBackup) {
            Add-WcsResult CRITICAL 'backup' "no backup in $BackupRoot (no folder with wcs-backup.json)"
        } else {
            $manifest = Get-Content -LiteralPath (Join-Path $newestBackup.FullName 'wcs-backup.json') -Raw | ConvertFrom-Json
            $taken = ([datetime]$manifest.createdUtc).ToUniversalTime()
            $age = ((Get-Date).ToUniversalTime() - $taken).TotalHours
            $textBackup = '{0}: taken {1:yyyy-MM-dd HH:mm} UTC ({2:N1} h ago), {3} file(s)' -f $newestBackup.Name, $taken, $age, @($manifest.files).Count
            if ($age -gt $MaxBackupAgeHours) { Add-WcsResult CRITICAL 'backup' "${textBackup}: older than $MaxBackupAgeHours h" }
            else { Add-WcsResult OK 'backup' $textBackup }
            if ($tool) {
                $run = Invoke-WcsTool -FilePath $tool -Arguments @('verify', $newestBackup.FullName) -TimeoutSeconds $ToolTimeoutSeconds
                if ($run.TimedOut) { Add-WcsResult UNKNOWN 'backup' "Wcs.DbTool verify did not finish within $ToolTimeoutSeconds s" }
                elseif ($run.ExitCode -ne 0) { Add-WcsResult CRITICAL 'backup' "Wcs.DbTool verify $($newestBackup.Name) failed (exit code $($run.ExitCode)): $(Get-FailureLines $run.Output)" }
                else { Add-WcsResult OK 'backup' "Wcs.DbTool verify $($newestBackup.Name): every file verified" }
                if ($ParkedFrom -eq 'Backup' -and -not $run.TimedOut) { Test-Parked $run.Output "the backup $($newestBackup.Name)" }
            }
        }
    }
}

# parked facts, live
if ($tool -and $ParkedFrom -eq 'Live') {
    $run = Invoke-WcsTool -FilePath $tool -Arguments @('verify', (Join-Path $data 'wcs.db')) -TimeoutSeconds $ToolTimeoutSeconds
    if ($run.TimedOut) { Add-WcsResult UNKNOWN 'parked' "Wcs.DbTool verify did not finish within $ToolTimeoutSeconds s" }
    elseif ($run.ExitCode -ne 0) { Add-WcsResult CRITICAL 'parked' "Wcs.DbTool verify of the live wcs.db failed (exit code $($run.ExitCode)): $(Get-FailureLines $run.Output)" }
    else { Test-Parked $run.Output 'the live wcs.db' }
}
if ($ParkedFrom -eq 'Backup' -and (-not $tool -or -not $BackupRoot)) { Add-WcsResult UNKNOWN 'parked' '-ParkedFrom Backup needs -DbToolPath and -BackupRoot' }

Complete-WcsCheck 'Test-WcsStorage'
