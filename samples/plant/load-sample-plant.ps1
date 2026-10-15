<#
.SYNOPSIS
    Loads the sample plant (sample-plant.json) into a running WCS host.

.DESCRIPTION
    Replaces the whole plant of the host with the content of a PlantGraphDto file, through the host's
    public API: PUT /api/v1/plant/graph. It is the same call the web editor makes when you import a
    plant file, and it replaces everything: devices, positions in the drawing, junctions and door
    mappings. The engine must be stopped (a fresh host is: Wcs:AutoRun is false by default).

    The steps, which you can also do by hand with any HTTP client:
      1. POST   /api/v1/plant/edit-lock          { "holder": "<name>" }  -> the edit permission (60 s lease)
      2. PUT    /api/v1/plant/graph              the file, with the header X-Edit-Holder: <name>
      3. DELETE /api/v1/plant/edit-lock?holder=<name>                      -> give the permission back
    Every call carries the host's key in the header X-Api-Key (the host's Wcs:ApiKey setting).

.PARAMETER HostUrl
    The address of the WCS host. Default: http://127.0.0.1:5580, the host's default.

.PARAMETER ApiKey
    The host's key (Wcs:ApiKey). Default: the environment variable WCS_API_KEY.

.PARAMETER PlantFile
    The plant to load. Default: sample-plant.json next to this script.

.EXAMPLE
    ./load-sample-plant.ps1 -ApiKey my-wcs-key

.EXAMPLE
    $env:WCS_API_KEY = 'my-wcs-key'; ./load-sample-plant.ps1 -HostUrl http://127.0.0.1:5580
#>
[CmdletBinding()]
param(
    [string] $HostUrl = 'http://127.0.0.1:5580',
    [string] $ApiKey = $env:WCS_API_KEY,
    [string] $PlantFile = ''
)

$ErrorActionPreference = 'Stop'

# One HTTP call to the host. When the host refuses it, the error says what the host said: the title
# and the detail of its problem (application/problem+json), or who holds the edit lease. Without this,
# Windows PowerShell 5.1 shows only "(409) Conflict" (in the language of Windows) and drops the body.
function Invoke-Host {
    param(
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Uri,
        [Parameter(Mandatory)] [hashtable] $Headers,
        [string] $ContentType,
        $Body
    )

    $call = @{ Method = $Method; Uri = $Uri; Headers = $Headers }
    if ($PSBoundParameters.ContainsKey('Body')) {
        $call.ContentType = $ContentType
        $call.Body = $Body
    }

    $verb = $Method.ToUpperInvariant()
    try {
        Invoke-RestMethod @call
    }
    catch {
        $response = $_.Exception.Response
        if ($null -eq $response) {
            throw "Cannot reach the host: $verb $Uri failed with '$($_.Exception.Message)'. Is the host running at that address (quick start, step 4)?"
        }

        # PowerShell 7 keeps the body in ErrorDetails; Windows PowerShell 5.1 leaves it in the response.
        $text = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            $text = $_.ErrorDetails.Message
        }
        elseif ($response -is [System.Net.WebResponse]) {
            $stream = $response.GetResponseStream()
            if ($stream.CanSeek) { $stream.Position = 0 }
            $text = (New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)).ReadToEnd()
        }

        $why = $null
        if ($text) {
            try { $problem = $text | ConvertFrom-Json } catch { $problem = $null }
            if ($problem -and $problem.title) {
                $why = $problem.title
                if ($problem.detail) { $why = "$why - $($problem.detail)" }
            }
            elseif ($problem -and $problem.holder) {
                $until = $problem.expiresLocal
                try { $until = ([datetime]$until).ToString('HH:mm:ss') } catch { }
                $why = "the edit lease is held by $($problem.holder) until $until (host time): wait until it expires, or until its holder gives it back, then run the script again"
            }
            else {
                $why = $text
            }
        }
        if (-not $why) { $why = $_.Exception.Message }

        throw "The host refused $verb $Uri with $([int]$response.StatusCode): $why"
    }
}

try {
    # The default is resolved here, not in param(): Windows PowerShell 5.1 run with -File leaves
    # $PSScriptRoot empty while it evaluates the defaults of the parameters.
    if ([string]::IsNullOrWhiteSpace($PlantFile)) {
        $PlantFile = Join-Path $PSScriptRoot 'sample-plant.json'
    }

    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        throw 'No API key: pass -ApiKey or set the environment variable WCS_API_KEY to the host''s Wcs:ApiKey.'
    }
    if (-not (Test-Path -LiteralPath $PlantFile)) {
        throw "The plant file '$PlantFile' does not exist."
    }

    $api = $HostUrl.TrimEnd('/') + '/api/v1'
    $headers = @{ 'X-Api-Key' = $ApiKey }

    # The engine must be in Stop (NavStatus 0): the host refuses to replace the plant of a running
    # engine with 409. Asking first gives a clearer message than the refusal.
    $status = Invoke-Host -Method Get -Uri "$api/status" -Headers $headers
    if ($status.engineLoadError) {
        throw "The host could not load its engine: $($status.engineLoadError)"
    }
    if ($status.navStatus -ne 0) {
        throw "The engine is not stopped (navStatus $($status.navStatus)). Stop it first: POST $api/commands with { ""commandType"": 1002 }."
    }

    $holder = "load-sample-plant-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
    $body = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $PlantFile))

    Invoke-Host -Method Post -Uri "$api/plant/edit-lock" -Headers $headers -ContentType 'application/json' `
        -Body (@{ holder = $holder } | ConvertTo-Json) | Out-Null
    try {
        $editHeaders = $headers + @{ 'X-Edit-Holder' = $holder }
        Invoke-Host -Method Put -Uri "$api/plant/graph" -Headers $editHeaders -ContentType 'application/json; charset=utf-8' `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) | Out-Null
    }
    finally {
        Invoke-Host -Method Delete -Uri "$api/plant/edit-lock?holder=$holder" -Headers $headers | Out-Null
    }

    $graph = Invoke-Host -Method Get -Uri "$api/plant/graph" -Headers $headers
    Write-Host "Plant loaded into $HostUrl : $($graph.items.Count) devices, $($graph.junctions.Count) junctions, $($graph.doorMappings.Count) door mappings."
    Write-Host "Start the engine with: POST $api/commands  { ""commandType"": 1001 }"
}
catch {
    # One line that says what went wrong, instead of PowerShell's error record with its position.
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
