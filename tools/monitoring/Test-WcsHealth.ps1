<#
.SYNOPSIS
    Health check of a running WCS host through its API: reachable, engine loaded and in the expected
    state, devices not OffLine or in Error, driver connections, and optionally the WMS and the local
    process or service.

.DESCRIPTION
    Read-only: every call is a GET, so give it the host's read-only key (Wcs:ReadOnlyApiKey). The key is
    read from an environment variable (WCS_MONITOR_API_KEY by default) and never printed.

    Checks:
      status       GET /api/v1/status: answered; engineLoadError empty; navStatus the expected one;
                   the host clock against this machine's.
      devices      GET /api/v1/plant/items/status: devices in Error (99), Warning (89) or OffLine (6).
      connections  GET /api/v1/plant/items and /api/v1/plc/connections: every device of connection type
                   Connected points to an existing driver settings row; the status each row last
                   persisted (GET /api/v1/plc/connections/{id}/status) is reported as it is, because
                   what its values mean belongs to the driver.
      wms          (with -ProbeWms) GET /api/v1/wms/configuration-check: the host calls the WMS; 503
                   means the WMS is unreachable. It costs the WMS a configuration check: do not run it
                   every few seconds.
      process      (with -ProcessName, on this machine) the process exists; its uptime.
      service      (with -ServiceName, Windows) the service is Running.

    Exit code: 0 OK, 1 WARNING, 2 CRITICAL, 3 UNKNOWN (see WcsMonitoring.Common.ps1).

.PARAMETER BaseUrl
    The host's address. Default: http://127.0.0.1:5580.

.PARAMETER ApiKeyVariable
    The NAME of the environment variable holding the key. Default: WCS_MONITOR_API_KEY.

.PARAMETER ExpectedEngineState
    Run, Stop, Pause or Any. Default: Run (a production plant works in Run). Another state is CRITICAL,
    except Recover (a transient state while the engine loads), which is WARNING.

.PARAMETER OffLineLevel
    The level of a device OffLine: WARNING (default; OffLine can be an operator's choice, command 2002)
    or CRITICAL.

.PARAMETER IgnoreDevice
    Device names (or item ids) left out of the device check, for example a device kept OffLine on
    purpose.

.EXAMPLE
    $env:WCS_MONITOR_API_KEY = '<read-only key>'
    pwsh -NoProfile -File tools/monitoring/Test-WcsHealth.ps1 -BaseUrl http://127.0.0.1:5580 -ServiceName Wcs.Host
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'http://127.0.0.1:5580',
    [string] $ApiKeyVariable = 'WCS_MONITOR_API_KEY',
    [ValidateRange(1, 300)] [int] $TimeoutSeconds = 10,
    [ValidateSet('Run', 'Stop', 'Pause', 'Any')] [string] $ExpectedEngineState = 'Run',
    [ValidateSet('WARNING', 'CRITICAL')] [string] $OffLineLevel = 'WARNING',
    [string[]] $IgnoreDevice = @(),
    [ValidateRange(1, 86400)] [int] $MaxClockSkewSeconds = 120,
    [switch] $ProbeWms,
    [string] $ProcessName,
    [ValidateRange(0, 100000)] [int] $MinUptimeMinutes = 0,
    [string] $ServiceName
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'WcsMonitoring.Common.ps1')

$navStates = @{ 0 = 'Stop'; 1 = 'Run'; 2 = 'Recover'; 3 = 'Dispose'; 4 = 'Pause' }
$deviceStates = @{ 0 = 'Ready'; 1 = 'Moving'; 2 = 'Loading'; 3 = 'Unloading'; 4 = 'Busy'; 5 = 'Recovering'; 6 = 'OffLine'; 7 = 'Locked'; 89 = 'Warning'; 99 = 'Error' }

# Local process and service first: they do not need the key.
if ($ProcessName) {
    $processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        Add-WcsResult CRITICAL 'process' "no process named $ProcessName on this machine"
    } else {
        $started = ($processes | Sort-Object StartTime -Descending | Select-Object -First 1).StartTime
        $uptime = (Get-Date) - $started
        $text = "$($processes.Count) process(es) $ProcessName, the newest started $($started.ToString('yyyy-MM-dd HH:mm:ss')) (up $([int]$uptime.TotalMinutes) min)"
        if ($processes.Count -gt 1) {
            Add-WcsResult WARNING 'process' "${text}: more than one host process on this machine"
        } elseif ($MinUptimeMinutes -gt 0 -and $uptime.TotalMinutes -lt $MinUptimeMinutes) {
            Add-WcsResult WARNING 'process' "${text}: restarted less than $MinUptimeMinutes min ago"
        } else {
            Add-WcsResult OK 'process' $text
        }
    }
}

if ($ServiceName) {
    if (-not $IsWindows) {
        Add-WcsResult UNKNOWN 'service' '-ServiceName is for Windows services; on this system watch the process instead'
    } else {
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if (-not $service) { Add-WcsResult CRITICAL 'service' "no service named $ServiceName" }
        elseif ($service.Status -ne 'Running') { Add-WcsResult CRITICAL 'service' "$ServiceName is $($service.Status)" }
        else { Add-WcsResult OK 'service' "$ServiceName is Running (start type $($service.StartType))" }
    }
}

$key = Get-WcsApiKey $ApiKeyVariable
if ($null -eq $key) { Complete-WcsCheck 'Test-WcsHealth' }

# status: the only route that always answers, engine loaded or not
$status = Invoke-WcsGet -BaseUrl $BaseUrl -Path '/api/v1/status' -ApiKey $key -TimeoutSeconds $TimeoutSeconds
if (-not $status.Ok) {
    Add-WcsResult CRITICAL 'status' "$($status.Error) [$BaseUrl]"
    Complete-WcsCheck 'Test-WcsHealth'
}
$s = $status.Body
$state = $navStates[[int]$s.navStatus] ?? "unknown ($($s.navStatus))"
$engineLoaded = [string]::IsNullOrEmpty($s.engineLoadError)
if (-not $engineLoaded) {
    Add-WcsResult CRITICAL 'status' "the engine is not loaded: $($s.engineLoadError) (the host retries by itself; see the host log)"
} elseif ($ExpectedEngineState -eq 'Any' -or $state -eq $ExpectedEngineState) {
    Add-WcsResult OK 'status' "engine $state, version $($s.version)"
} elseif ($state -eq 'Recover') {
    Add-WcsResult WARNING 'status' "engine in Recover (loading), expected $ExpectedEngineState"
} else {
    Add-WcsResult CRITICAL 'status' "engine in $state, expected $ExpectedEngineState"
}
if ($s.utcNow) {
    $hostUtc = ([datetime]$s.utcNow).ToUniversalTime()
    $skew = [math]::Abs(((Get-Date).ToUniversalTime() - $hostUtc).TotalSeconds)
    if ($skew -gt $MaxClockSkewSeconds) {
        Add-WcsResult WARNING 'clock' "the host clock differs from this machine's by $([int]$skew) s: logs and history will not line up"
    }
}

if ($engineLoaded) {
    # devices
    $items = Invoke-WcsGet -BaseUrl $BaseUrl -Path '/api/v1/plant/items/status' -ApiKey $key -TimeoutSeconds $TimeoutSeconds
    if (-not $items.Ok) {
        Add-WcsResult CRITICAL 'devices' $items.Error
    } else {
        $all = @($items.Body)
        $watched = @($all | Where-Object { $IgnoreDevice -notcontains $_.name -and $IgnoreDevice -notcontains [string]$_.itemId })
        $describe = { param($list) ($list | ForEach-Object { if ($_.name) { $_.name } else { $_.itemId } }) -join ', ' }
        $inError = @($watched | Where-Object { [int]$_.status -eq 99 })
        $inWarning = @($watched | Where-Object { [int]$_.status -eq 89 })
        $offLine = @($watched | Where-Object { [int]$_.status -eq 6 })
        if ($inError) { Add-WcsResult CRITICAL 'devices' "$($inError.Count) in Error (the last command to the device did not go through): $(& $describe $inError)" }
        if ($inWarning) { Add-WcsResult WARNING 'devices' "$($inWarning.Count) in Warning (the last movement failed; retry with command 2001 once the cause is fixed): $(& $describe $inWarning)" }
        if ($offLine) { Add-WcsResult $OffLineLevel 'devices' "$($offLine.Count) OffLine: $(& $describe $offLine)" }
        if (-not ($inError -or $inWarning -or $offLine)) {
            $ignored = $all.Count - $watched.Count
            Add-WcsResult OK 'devices' "$($watched.Count) device(s), none in Error, Warning or OffLine$(if ($ignored) { " ($ignored ignored)" })"
        }
    }

    # driver connections
    $config = Invoke-WcsGet -BaseUrl $BaseUrl -Path '/api/v1/plant/items' -ApiKey $key -TimeoutSeconds $TimeoutSeconds
    $rows = Invoke-WcsGet -BaseUrl $BaseUrl -Path '/api/v1/plc/connections' -ApiKey $key -TimeoutSeconds $TimeoutSeconds
    if (-not $config.Ok) { Add-WcsResult CRITICAL 'connections' $config.Error }
    elseif (-not $rows.Ok) { Add-WcsResult CRITICAL 'connections' $rows.Error }
    else {
        $connected = @(@($config.Body) | Where-Object { [int]$_.connectionType -eq 1 })
        $ids = @(@($rows.Body) | ForEach-Object { [long]$_.id })
        $orphans = @($connected | Where-Object { $ids -notcontains [long]$_.deviceConnectionId })
        if ($orphans) {
            Add-WcsResult CRITICAL 'connections' "$($orphans.Count) Connected device(s) point to no driver settings row: $(($orphans | ForEach-Object { "$($_.name) -> $($_.deviceConnectionId)" }) -join ', ')"
        } else {
            Add-WcsResult OK 'connections' "$($connected.Count) Connected device(s), $($ids.Count) driver settings row(s)"
        }
        foreach ($id in $ids) {
            $one = Invoke-WcsGet -BaseUrl $BaseUrl -Path "/api/v1/plc/connections/$id/status" -ApiKey $key -TimeoutSeconds $TimeoutSeconds
            if ($one.Ok) {
                $c = $one.Body
                Add-WcsResult OK 'connections' "row ${id}: opProgress $($c.opProgress), sender $($c.statusSenderChannel), receiver $($c.statusReceiverChannel), outbound $($c.outboundChannelStatus), inbound $($c.inboundChannelStatus) (values defined by the driver)"
            } elseif ($one.Status -eq 404) {
                Add-WcsResult OK 'connections' "row ${id}: the driver has persisted no status yet"
            } else {
                Add-WcsResult WARNING 'connections' "row ${id}: $($one.Error)"
            }
        }
    }

    if ($ProbeWms) {
        $wms = Invoke-WcsGet -BaseUrl $BaseUrl -Path '/api/v1/wms/configuration-check?mode=0' -ApiKey $key -TimeoutSeconds ([math]::Max($TimeoutSeconds, 35))
        if ($wms.Ok) {
            $findings = @($wms.Body).Count
            Add-WcsResult OK 'wms' "the WMS answered the configuration check ($findings finding(s) about its data)"
        } else {
            Add-WcsResult CRITICAL 'wms' $wms.Error
        }
    }
}

Complete-WcsCheck 'Test-WcsHealth'
