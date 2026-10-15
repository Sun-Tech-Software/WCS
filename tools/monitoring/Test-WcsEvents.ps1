<#
.SYNOPSIS
    Looks for the WCS host's alarm texts in its log (a console log file and/or the Windows Event Log)
    and for unexpected restarts of its service.

.DESCRIPTION
    The host writes its warnings and errors through ASP.NET Core logging: to the console and, on
    Windows, to the Application event log. What this script looks for (the texts are the host's own,
    partly in Italian):

      CRITICAL  "RIFIUTATA DEFINITIVAMENTE"        a fact towards the WMS refused for good and parked.
                                                   Written ONCE, when it is parked: after a restart only
                                                   Test-WcsStorage.ps1 (Wcs.DbTool verify) still sees it.
      CRITICAL  "Motore non caricato"              the engine could not be loaded (the host retries).
      WARNING   "Il gestionale non ha risposto"    a call to the WMS failed (WMS down, wrong address or
                                                   key, an answer the WCS cannot read).
      WARNING   "WITHOUT CONNECTION"               a Connected device has no driver connection.
      WARNING   "served by the SIMULATOR"          a Connected device runs on the simulator.
      WARNING   any other error (Event Log level Error or Critical from the host; "fail:"/"crit:" lines
                in a console log), counted.

    Event Log: the events of the last -SinceMinutes minutes from the sources in -EventSource. Observed
    on Windows 11 with the host started from a console: the source is ".NET Runtime" (event id 1000,
    the message starts with "Category: <logger name>"); of those, only the ones whose category starts
    with "Wcs." are taken, because other .NET programs use that source too. A host running as a
    service may register its own source (by default the application name, "Wcs.Host"): that one is
    in -EventSource too. Check on your server which source the host uses.

    Log file: with -LogFile, the file a console host's output is redirected to. With -StateFile the
    script remembers how far it read and reads only the new lines at the next run (from the start if
    the file was replaced or truncated); without it, the whole file is read.

    Restarts: with -ServiceName (Windows), the Service Control Manager events 7031 and 7034 (the
    service terminated unexpectedly) in the System log within -SinceMinutes, and the Application Error
    (1000) and .NET Runtime (1026, unhandled exception) events of Wcs.Host.exe.

    Exit code: 0 OK, 1 WARNING, 2 CRITICAL, 3 UNKNOWN (see WcsMonitoring.Common.ps1).

.EXAMPLE
    pwsh -NoProfile -File tools/monitoring/Test-WcsEvents.ps1 -EventLog -ServiceName Wcs.Host -SinceMinutes 15
.EXAMPLE
    pwsh -NoProfile -File tools/monitoring/Test-WcsEvents.ps1 -LogFile C:\Wcs\host.log -StateFile C:\WcsMonitoring\host-log.state.json
#>
[CmdletBinding()]
param(
    [string[]] $LogFile = @(),
    [string] $StateFile,
    [switch] $EventLog,
    [string[]] $EventSource = @('Wcs.Host', '.NET Runtime'),
    [ValidateRange(1, 525600)] [int] $SinceMinutes = 15,
    [string] $ServiceName,
    [string] $ExecutableName = 'Wcs.Host.exe',
    [ValidateRange(1, 100)] [int] $Examples = 2
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WcsMonitoring.Common.ps1')

$rules = @(
    @{ Level = 'CRITICAL'; Check = 'parked'; Text = 'RIFIUTATA DEFINITIVAMENTE'; What = 'fact(s) towards the WMS refused for good and parked: follow the parked-facts procedure (known-limitations.md), do not edit wcs.db' }
    @{ Level = 'CRITICAL'; Check = 'engine'; Text = 'Motore non caricato'; What = 'failed engine load(s)' }
    @{ Level = 'WARNING'; Check = 'wms'; Text = 'Il gestionale non ha risposto'; What = 'failed call(s) to the WMS' }
    @{ Level = 'WARNING'; Check = 'driver'; Text = 'WITHOUT CONNECTION'; What = 'device(s) of connection type Connected without a driver connection' }
    @{ Level = 'WARNING'; Check = 'driver'; Text = 'served by the SIMULATOR'; What = 'Connected device(s) served by the simulator' }
)

function Get-OneLine([string] $text) {
    $flat = ($text -replace '\s+', ' ').Trim()
    if ($flat.Length -gt 200) { $flat = $flat.Substring(0, 200) + '...' }
    $flat
}

# Applies the rules to a list of entries (@{ Text; Error }) read from one source.
function Test-Entries([object[]] $entries, [string] $source) {
    $matchedAny = $false
    foreach ($rule in $rules) {
        $hits = @($entries | Where-Object { $_.Text.Contains($rule.Text, [System.StringComparison]::Ordinal) })
        if ($hits) {
            $matchedAny = $true
            $sample = ($hits | Select-Object -First $Examples | ForEach-Object { '"' + (Get-OneLine $_.Text) + '"' }) -join ' | '
            Add-WcsResult $rule.Level $rule.Check "$($hits.Count) $($rule.What), in $source. E.g. $sample"
        }
    }
    $others = @($entries | Where-Object { $_.Error } | Where-Object { $text = $_.Text; -not ($rules | Where-Object { $text.Contains($_.Text, [System.StringComparison]::Ordinal) }) })
    if ($others) {
        $sample = ($others | Select-Object -First $Examples | ForEach-Object { '"' + (Get-OneLine $_.Text) + '"' }) -join ' | '
        Add-WcsResult WARNING 'errors' "$($others.Count) other error(s) in $source. E.g. $sample"
    } elseif (-not $matchedAny) {
        Add-WcsResult OK 'log' "nothing to report in $source ($($entries.Count) entr(ies) read)"
    }
}

if (-not $LogFile -and -not $EventLog -and -not $ServiceName) {
    Add-WcsResult UNKNOWN 'arguments' 'nothing to check: give -LogFile, -EventLog or -ServiceName'
    Complete-WcsCheck 'Test-WcsEvents'
}

# Log files
$state = @{}
if ($StateFile -and (Test-Path -LiteralPath $StateFile)) {
    try { (Get-Content -LiteralPath $StateFile -Raw | ConvertFrom-Json -AsHashtable).GetEnumerator() | ForEach-Object { $state[$_.Key] = $_.Value } }
    catch { Add-WcsResult WARNING 'state' "could not read $StateFile; reading the log files from the start" }
}
foreach ($path in $LogFile) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Add-WcsResult UNKNOWN 'log' "log file not found: $path"
        continue
    }
    $full = (Resolve-Path -LiteralPath $path).ProviderPath
    $offset = 0L
    if ($state.ContainsKey($full)) { $offset = [long]$state[$full] }
    $stream = [System.IO.FileStream]::new($full, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    try {
        if ($offset -gt $stream.Length) { $offset = 0 }  # replaced or truncated
        [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
        $buffer = [System.IO.MemoryStream]::new()
        $stream.CopyTo($buffer)
        $bytes = $buffer.ToArray()
        # Up to the last complete line: a line being written is read at the next run. The offset is
        # counted in bytes, whatever the encoding of the file (a redirected console may not write
        # UTF-8: the alarm texts are ASCII, so they match anyway).
        $end = [System.Array]::LastIndexOf($bytes, [byte]10)
        $text = if ($end -ge 0) { [System.Text.Encoding]::UTF8.GetString($bytes, 0, $end + 1) } else { '' }
        $state[$full] = $offset + $end + 1
    } finally {
        $stream.Dispose()
    }
    $lines = @($text -split "`r?`n" | Where-Object { $_ })
    $entries = [System.Collections.Generic.List[object]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # ASP.NET Core console format: "fail: Category[id]" then the message, indented, on the next line
        $isError = $lines[$i] -match '^(fail|crit): '
        if ($isError -and $i + 1 -lt $lines.Count) {
            $entries.Add(@{ Text = $lines[$i] + ' ' + $lines[$i + 1].Trim(); Error = $true }); $i++
        } else {
            $entries.Add(@{ Text = $lines[$i]; Error = $false })
        }
    }
    $where = if ($offset -gt 0) { "$path (new lines since the last run)" } else { $path }
    Test-Entries $entries.ToArray() $where
}
if ($StateFile -and $LogFile) {
    $folder = Split-Path -Parent $StateFile
    if ($folder -and -not (Test-Path -LiteralPath $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
    $state | ConvertTo-Json | Set-Content -LiteralPath $StateFile -Encoding utf8
}

$since = (Get-Date).AddMinutes(-$SinceMinutes)

# Windows Event Log
if ($EventLog) {
    if (-not $IsWindows) {
        Add-WcsResult UNKNOWN 'eventlog' '-EventLog is Windows only; read the host log file with -LogFile'
    } else {
        $events = @()
        foreach ($source in $EventSource) {
            try {
                $events += @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $source; StartTime = $since; Level = 1, 2, 3 } -ErrorAction Stop)
            } catch {
                # No event in the window, or a source never registered on this machine: nothing to read
                if ($_.FullyQualifiedErrorId -notmatch '^(NoMatchingEventsFound|NoMatchingProvidersFound)') {
                    Add-WcsResult UNKNOWN 'eventlog' "could not read source ${source}: $(Get-OneLine $_.Exception.Message)"
                }
            }
        }
        $ours = @($events | Where-Object {
            $_.ProviderName -ne '.NET Runtime' -or ($_.Id -eq 1000 -and $_.Message -match '^\s*Category: Wcs\.')
        })
        $entries = @($ours | ForEach-Object { @{ Text = [string]$_.Message; Error = ($_.Level -le 2) } })
        Test-Entries $entries "the Application event log, last $SinceMinutes min ($($EventSource -join ', '))"
    }
}

# Unexpected restarts
if ($ServiceName) {
    if (-not $IsWindows) {
        Add-WcsResult UNKNOWN 'restarts' '-ServiceName is for Windows services; watch the process uptime with Test-WcsHealth.ps1 -ProcessName -MinUptimeMinutes'
    } else {
        $names = @($ServiceName)
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service) { $names += $service.DisplayName }
        $crashes = @()
        try {
            $crashes += @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager'; Id = 7031, 7034; StartTime = $since } -ErrorAction Stop |
                Where-Object { $names -contains [string]$_.Properties[0].Value })
        } catch { }
        try {
            $crashes += @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'Application Error', '.NET Runtime'; Id = 1000, 1026; StartTime = $since } -ErrorAction Stop |
                Where-Object { ($_.ProviderName -eq 'Application Error' -and [string]$_.Properties[0].Value -eq $ExecutableName) -or
                               ($_.ProviderName -eq '.NET Runtime' -and $_.Id -eq 1026 -and $_.Message -match [regex]::Escape($ExecutableName)) })
        } catch { }
        if ($crashes) {
            $sample = ($crashes | Sort-Object TimeCreated -Descending | Select-Object -First $Examples | ForEach-Object { "$($_.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')) $($_.ProviderName) $($_.Id)" }) -join ' | '
            Add-WcsResult CRITICAL 'restarts' "$($crashes.Count) unexpected stop(s) of $ServiceName in the last $SinceMinutes min: $sample"
        } else {
            Add-WcsResult OK 'restarts' "no unexpected stop of $ServiceName in the last $SinceMinutes min"
        }
    }
}

Complete-WcsCheck 'Test-WcsEvents'
