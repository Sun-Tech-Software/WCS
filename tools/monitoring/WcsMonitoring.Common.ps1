# Shared by the WCS monitoring scripts (dot-sourced). PowerShell 7.
#
# Every check adds one or more results (OK, WARNING, CRITICAL, UNKNOWN). Complete-WcsCheck prints them,
# one per line, and ends the script with the exit code of the worst one:
#
#   0  OK        every check passed
#   1  WARNING   something needs a look, nothing is down yet
#   2  CRITICAL  something is down or wrong and needs a person now
#   3  UNKNOWN   a check could not run (missing key, bad argument, tool not found)
#
# CRITICAL wins over WARNING, WARNING over UNKNOWN. The codes are the ones Nagios-style monitoring
# systems expect, so the scripts plug into them as they are.
#
# The API key is read from an environment variable whose NAME is given, and is never printed: the
# results carry status codes and the host's messages, never request headers.

Set-StrictMode -Version Latest

$script:WcsResults = [System.Collections.Generic.List[object]]::new()
$script:WcsLevels = @{ OK = 0; WARNING = 1; CRITICAL = 2; UNKNOWN = 3 }

function Add-WcsResult {
    param(
        [Parameter(Mandatory)] [ValidateSet('OK', 'WARNING', 'CRITICAL', 'UNKNOWN')] [string] $Level,
        [Parameter(Mandatory)] [string] $Check,
        [Parameter(Mandatory)] [string] $Message
    )
    $script:WcsResults.Add([pscustomobject]@{ Level = $Level; Check = $Check; Message = $Message })
}

function Get-WcsExitCode {
    $levels = @($script:WcsResults | ForEach-Object Level)
    if ($levels -contains 'CRITICAL') { return 2 }
    if ($levels -contains 'WARNING') { return 1 }
    if ($levels -contains 'UNKNOWN') { return 3 }
    return 0
}

function Complete-WcsCheck {
    param([Parameter(Mandatory)] [string] $Name)
    $code = Get-WcsExitCode
    $word = @('OK', 'WARNING', 'CRITICAL', 'UNKNOWN')[$code]
    foreach ($r in $script:WcsResults) { Write-Output ('{0,-8} {1}: {2}' -f $r.Level, $r.Check, $r.Message) }
    $counts = $script:WcsResults | Group-Object Level | Sort-Object Name | ForEach-Object { "$($_.Count) $($_.Name)" }
    Write-Output "$Name ${word} ($($counts -join ', '); exit code $code)"
    exit $code
}

# The key from the environment variable named $VariableName; $null (and an UNKNOWN result) if absent.
function Get-WcsApiKey {
    param([Parameter(Mandatory)] [string] $VariableName)
    $value = [System.Environment]::GetEnvironmentVariable($VariableName)
    if ([string]::IsNullOrWhiteSpace($value)) {
        Add-WcsResult UNKNOWN 'api-key' "the environment variable $VariableName is not set: put the host's read-only key (Wcs:ReadOnlyApiKey) in it"
        return $null
    }
    return $value
}

# GET on the host API. Returns @{ Ok; Status; Body; Error }: Ok is true for a 2xx with a JSON body read;
# Status is the HTTP status (0 when no answer came); Error says why, in words, without the key.
function Invoke-WcsGet {
    param(
        [Parameter(Mandatory)] [string] $BaseUrl,
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $ApiKey,
        [int] $TimeoutSeconds = 10
    )
    $uri = $BaseUrl.TrimEnd('/') + $Path
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -Headers @{ 'X-Api-Key' = $ApiKey; Accept = 'application/json' } `
            -TimeoutSec $TimeoutSeconds -SkipHttpErrorCheck -UseBasicParsing -ErrorAction Stop
    } catch {
        $reason = $_.Exception.Message
        if ($_.Exception -is [System.Threading.Tasks.TaskCanceledException] -or $reason -match 'Timeout|timed out') {
            $reason = "no answer within $TimeoutSeconds s"
        }
        return @{ Ok = $false; Status = 0; Body = $null; Error = "GET $Path failed: $reason" }
    }
    $status = [int]$response.StatusCode
    $text = if ($response.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
    if ($status -lt 200 -or $status -ge 300) {
        $detail = ''
        try {
            $problem = $text | ConvertFrom-Json -ErrorAction Stop
            $parts = @($problem.PSObject.Properties | Where-Object Name -in 'title', 'detail', 'unavailable' | ForEach-Object { "$($_.Name)=$($_.Value)" })
            if ($parts) { $detail = ' (' + ($parts -join '; ') + ')' }
        } catch { }
        $hint = switch ($status) {
            401 { ': the key was refused (wrong key, or the host was restarted with another one)' }
            403 { ': the key may not call this route' }
            503 { ': the engine is not loaded, or the WMS is unreachable' }
            default { '' }
        }
        return @{ Ok = $false; Status = $status; Body = $null; Error = "GET $Path answered $status$hint$detail" }
    }
    try {
        $body = if ([string]::IsNullOrWhiteSpace($text)) { $null } else { $text | ConvertFrom-Json -NoEnumerate -ErrorAction Stop }
    } catch {
        return @{ Ok = $false; Status = $status; Body = $null; Error = "GET $Path answered $status with a body that is not JSON" }
    }
    return @{ Ok = $true; Status = $status; Body = $body; Error = $null }
}

# Runs an executable with a time limit. Returns @{ ExitCode; Output; TimedOut }.
function Invoke-WcsTool {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [int] $TimeoutSeconds = 300
    )
    $start = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($a in $Arguments) { $start.ArgumentList.Add($a) }
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($start)
    $out = $process.StandardOutput.ReadToEndAsync()
    $err = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { }
        return @{ ExitCode = -1; Output = ''; TimedOut = $true }
    }
    $process.WaitForExit()
    return @{ ExitCode = $process.ExitCode; Output = ($out.Result + $err.Result); TimedOut = $false }
}
