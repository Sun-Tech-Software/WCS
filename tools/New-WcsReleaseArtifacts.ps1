#Requires -Version 7
<#
.SYNOPSIS
    Builds and validates the three distributable WCS archives.

.DESCRIPTION
    Publishes Wcs.Host, Wcs.DbTool and Wcs.SampleWms, adds the legal and public documentation files,
    validates every ZIP, writes and verifies SHA256SUMS.txt and, with -SmokeTest, extracts the archives
    and starts their applications. It never creates a GitHub release and never changes a tracked file.

.EXAMPLE
    pwsh -File tools/New-WcsReleaseArtifacts.ps1 -SmokeTest

.EXAMPLE
    pwsh -File tools/New-WcsReleaseArtifacts.ps1 -OutputDirectory release/dist -NoRestore -SmokeTest

.EXAMPLE
    pwsh -File tools/New-WcsReleaseArtifacts.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Root = (Split-Path -Parent $PSScriptRoot),
    [string] $OutputDirectory,
    [switch] $SmokeTest,
    [switch] $NoRestore,
    [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:CommonReleaseFiles = @(
    'LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'CHANGELOG.md', 'SAFETY.md', 'SECURITY.md'
)

function Test-ArchiveLayout {
    param(
        [Parameter(Mandatory)] [string] $ArchiveName,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Entries,
        [Parameter(Mandatory)] [string[]] $ApplicationFiles
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $script:CommonReleaseFiles + $ApplicationFiles) {
        if (-not $Entries.Contains($file) -or [long]$Entries[$file] -le 0) {
            $problems.Add("${ArchiveName}: $file missing or empty")
        }
    }
    if (-not @($Entries.Keys | Where-Object { $_ -like 'licenses/*' })) {
        $problems.Add("${ArchiveName}: licenses/ missing")
    }
    foreach ($file in $Entries.Keys) {
        if ($file -cmatch '(^|/)(appsettings\.Secrets\.json|appsettings\.Development\.json|[^/]+\.db(-wal|-shm)?)$|^Data/') {
            $problems.Add("${ArchiveName}: $file must not be distributed")
        }
    }
    return $problems.ToArray()
}

function Write-ChecksumManifest {
    param([Parameter(Mandatory)] [string] $Directory)

    $lines = @(Get-ChildItem -LiteralPath $Directory -File |
        Where-Object Name -ne 'SHA256SUMS.txt' |
        Sort-Object Name |
        ForEach-Object {
            '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $_.Name
        })
    [System.IO.File]::WriteAllText(
        (Join-Path $Directory 'SHA256SUMS.txt'),
        (($lines -join "`n") + "`n"),
        [System.Text.UTF8Encoding]::new($false))
    return $lines
}

function Test-ChecksumManifest {
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string[]] $Lines
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    $named = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $Lines) {
        if ($line -notmatch '^([0-9a-f]{64})  ([^/\\]+)$') {
            $problems.Add("invalid checksum line '$line'")
            continue
        }
        $expected = $Matches[1]
        $name = $Matches[2]
        if (-not $named.Add($name)) { $problems.Add("duplicate checksum for $name"); continue }
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $problems.Add("$name is listed but missing")
            continue
        }
        $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
        if ($actual -cne $expected) { $problems.Add("$name has checksum $actual, expected $expected") }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -ne 'SHA256SUMS.txt') {
        if (-not $named.Contains($file.Name)) { $problems.Add("$($file.Name) has no checksum") }
    }
    return $problems.ToArray()
}

function Invoke-DotNet {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

function Read-ArchiveEntries {
    param([Parameter(Mandatory)] [string] $Path)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = [ordered]@{}
        foreach ($entry in $archive.Entries) { $entries[$entry.FullName.Replace('\', '/')] = $entry.Length }
        return $entries
    }
    finally { $archive.Dispose() }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Get-ProcessFailureDetail {
    param([Parameter(Mandatory)] [System.Diagnostics.Process] $Process, [Parameter(Mandatory)] [string[]] $Logs)
    $tail = foreach ($path in $Logs) {
        if (Test-Path -LiteralPath $path) {
            "--- $([System.IO.Path]::GetFileName($path)) ---"
            Get-Content -LiteralPath $path -Tail 30
        }
    }
    return "process $($Process.Id) exited with code $($Process.ExitCode)`n$($tail -join "`n")"
}

function Wait-HttpResponse {
    param(
        [Parameter(Mandatory)] [string] $Uri,
        [Parameter(Mandatory)] [System.Diagnostics.Process] $Process,
        [hashtable] $Headers = @{},
        [int] $TimeoutSeconds = 45,
        [string[]] $Logs = @()
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) { throw (Get-ProcessFailureDetail $Process $Logs) }
        try {
            $response = Invoke-WebRequest -Uri $Uri -Headers $Headers -TimeoutSec 2 -SkipHttpErrorCheck
            if ([int]$response.StatusCode -eq 200) { return $response }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "timed out waiting for HTTP 200 from $Uri"
}

function Stop-ReleaseProcess {
    param([System.Diagnostics.Process] $Process)
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            $Process.Kill($true)
            [void]$Process.WaitForExit(10000)
        }
    }
    finally { $Process.Dispose() }
}

function Invoke-ReleaseSmokeTest {
    param(
        [Parameter(Mandatory)] [string] $DistributionDirectory,
        [Parameter(Mandatory)] [string] $Version
    )

    $smokeRoot = [System.IO.Directory]::CreateTempSubdirectory('wcs-release-smoke-').FullName
    $sampleProcess = $null
    $hostProcess = $null
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $hostDirectory = Join-Path $smokeRoot 'host'
        $dbToolDirectory = Join-Path $smokeRoot 'dbtool'
        $sampleDirectory = Join-Path $smokeRoot 'sample-wms'
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $DistributionDirectory "wcs-host-$Version.zip"), $hostDirectory)
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $DistributionDirectory "wcs-dbtool-$Version.zip"), $dbToolDirectory)
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $DistributionDirectory "wcs-sample-wms-$Version.zip"), $sampleDirectory)

        $dbOutput = & (Join-Path $dbToolDirectory 'Wcs.DbTool.exe') 2>&1
        if ($LASTEXITCODE -ne 2 -or ($dbOutput -join "`n") -notmatch 'Wcs\.DbTool - backup') {
            throw "the packaged Wcs.DbTool did not print its usage as expected (exit $LASTEXITCODE)"
        }

        $wmsPort = Get-FreeTcpPort
        $hostPort = Get-FreeTcpPort
        $wmsUrl = "http://127.0.0.1:$wmsPort"
        $hostUrl = "http://127.0.0.1:$hostPort"
        $wmsOut = Join-Path $smokeRoot 'sample-wms.stdout.log'
        $wmsErr = Join-Path $smokeRoot 'sample-wms.stderr.log'
        $hostOut = Join-Path $smokeRoot 'host.stdout.log'
        $hostErr = Join-Path $smokeRoot 'host.stderr.log'
        $apiKey = 'release-smoke-' + [guid]::NewGuid().ToString('N')
        $wmsKey = 'release-smoke-wms-' + [guid]::NewGuid().ToString('N')

        $sampleProcess = Start-Process -FilePath (Join-Path $sampleDirectory 'Wcs.SampleWms.exe') `
            -WorkingDirectory $sampleDirectory -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $wmsOut -RedirectStandardError $wmsErr `
            -Environment @{ Urls = $wmsUrl; SampleWms__ApiKey = $wmsKey; Logging__LogLevel__Default = 'Warning' }
        [void](Wait-HttpResponse "$wmsUrl/" $sampleProcess -Logs $wmsOut, $wmsErr)

        $dataDirectory = Join-Path $smokeRoot 'data'
        $hostProcess = Start-Process -FilePath (Join-Path $hostDirectory 'Wcs.Host.exe') `
            -WorkingDirectory $hostDirectory -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput $hostOut -RedirectStandardError $hostErr `
            -Environment @{
                DOTNET_ENVIRONMENT = 'Production'
                Wcs__ApiKey = $apiKey
                Wcs__DatabaseDirectory = $dataDirectory
                Wcs__StartEngine = 'true'
                Wcs__AutoRun = 'false'
                Wms__ApiAddress = $wmsUrl
                Wms__ApiKey = $wmsKey
                Kestrel__Endpoints__Http__Url = $hostUrl
                'Logging__LogLevel__System.Net.Http.HttpClient' = 'Warning'
            }
        $headers = @{ 'X-Api-Key' = $apiKey }
        [void](Wait-HttpResponse "$hostUrl/api/v1/status" $hostProcess -Headers $headers -Logs $hostOut, $hostErr)
        $editor = Wait-HttpResponse "$hostUrl/editor/drawio" $hostProcess -Logs $hostOut, $hostErr
        if ($editor.Content -notmatch '<html|<!DOCTYPE') { throw 'the packaged editor returned no HTML document' }
        $framework = Wait-HttpResponse "$hostUrl/_framework/blazor.web.js" $hostProcess -Logs $hostOut, $hostErr
        if ($framework.RawContentLength -le 0) { throw 'the packaged editor returned an empty Blazor framework script' }

        & pwsh -NoProfile -File (Join-Path $sampleDirectory 'plant/load-sample-plant.ps1') -HostUrl $hostUrl -ApiKey $apiKey
        if ($LASTEXITCODE -ne 0) { throw "the packaged sample plant loader failed with exit code $LASTEXITCODE" }
        $graph = Invoke-RestMethod -Uri "$hostUrl/api/v1/plant/graph" -Headers $headers -TimeoutSec 10
        if (@($graph.items).Count -eq 0 -or @($graph.junctions).Count -eq 0 -or @($graph.doorMappings).Count -eq 0) {
            throw 'the packaged sample plant loader produced an incomplete plant'
        }
        Write-Host "Smoke test passed: host, editor, DbTool and sample WMS from the archives are usable."
    }
    finally {
        Stop-ReleaseProcess $hostProcess
        Stop-ReleaseProcess $sampleProcess
        if (Test-Path -LiteralPath $smokeRoot) { [System.IO.Directory]::Delete($smokeRoot, $true) }
    }
}

function Invoke-SelfTest {
    $failures = [System.Collections.Generic.List[string]]::new()

    function Assert-Equal([string] $Name, $Expected, $Actual) {
        if ($Expected -eq $Actual) { Write-Host "  ok    $Name"; return }
        $failures.Add("${Name}: expected '$Expected', got '$Actual'")
        Write-Host "  FAIL  $Name" -ForegroundColor Red
    }

    $goodEntries = @{
        'LICENSE' = 10; 'NOTICE' = 10; 'THIRD-PARTY-NOTICES.md' = 10; 'README.md' = 10
        'CHANGELOG.md' = 10; 'SAFETY.md' = 10; 'SECURITY.md' = 10; 'licenses/a.txt' = 10
        'Wcs.Host.exe' = 10; 'Wcs.Host.dll' = 10; 'appsettings.json' = 10
        'wwwroot/_framework/blazor.web.js' = 10; 'Wcs.WebEditor.dll' = 10; 'Wcs.Connection.Test.dll' = 10
    }
    Assert-Equal 'archive: valid host layout passes' 0 @(Test-ArchiveLayout 'host' $goodEntries @(
        'Wcs.Host.exe', 'Wcs.Host.dll', 'appsettings.json', 'wwwroot/_framework/blazor.web.js',
        'Wcs.WebEditor.dll', 'Wcs.Connection.Test.dll')).Count

    $missing = $goodEntries.Clone(); $missing.Remove('LICENSE')
    Assert-Equal 'archive: missing legal file fails' 1 @(Test-ArchiveLayout 'host' $missing @(
        'Wcs.Host.exe', 'Wcs.Host.dll', 'appsettings.json', 'wwwroot/_framework/blazor.web.js',
        'Wcs.WebEditor.dll', 'Wcs.Connection.Test.dll')).Count

    $forbidden = $goodEntries.Clone(); $forbidden['Data/wcs.db'] = 10
    Assert-Equal 'archive: database fails' 1 @(Test-ArchiveLayout 'host' $forbidden @(
        'Wcs.Host.exe', 'Wcs.Host.dll', 'appsettings.json', 'wwwroot/_framework/blazor.web.js',
        'Wcs.WebEditor.dll', 'Wcs.Connection.Test.dll')).Count

    $checksumDirectory = [System.IO.Directory]::CreateTempSubdirectory('wcs-release-selftest-').FullName
    try {
        [System.IO.File]::WriteAllText((Join-Path $checksumDirectory 'b.zip'), 'second')
        [System.IO.File]::WriteAllText((Join-Path $checksumDirectory 'a.zip'), 'first')
        $manifest = Write-ChecksumManifest $checksumDirectory
        Assert-Equal 'checksums: manifest is valid' 0 @(Test-ChecksumManifest $checksumDirectory $manifest).Count
        Assert-Equal 'checksums: entries are sorted' 'a.zip' (($manifest[0] -split '  ', 2)[1])
        [System.IO.File]::WriteAllText((Join-Path $checksumDirectory 'a.zip'), 'tampered')
        Assert-Equal 'checksums: changed artifact fails' 1 @(Test-ChecksumManifest $checksumDirectory $manifest).Count
    }
    finally { [System.IO.Directory]::Delete($checksumDirectory, $true) }

    if ($failures.Count) {
        $failures | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        Write-Host "Self-test: $($failures.Count) failure(s)." -ForegroundColor Red
        return 1
    }
    Write-Host 'Self-test: all release-artifact assertions passed.' -ForegroundColor Green
    return 0
}

if ($SelfTest) { exit (Invoke-SelfTest) }

$rootFull = [System.IO.Path]::GetFullPath($Root)
if (-not (Test-Path -LiteralPath (Join-Path $rootFull 'Directory.Build.props'))) {
    throw "'$rootFull' is not the root of the WCS repository"
}

[xml]$properties = Get-Content -LiteralPath (Join-Path $rootFull 'Directory.Build.props') -Raw
$version = $properties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -notmatch '^\d+\.\d+\.\d+(-preview\.\d+)?$') {
    throw "Directory.Build.props: '$version' is not X.Y.Z or X.Y.Z-preview.N"
}

$generatedRoot = $null
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $generatedRoot = [System.IO.Directory]::CreateTempSubdirectory('wcs-release-artifacts-').FullName
    $distributionDirectory = Join-Path $generatedRoot 'dist'
}
else { $distributionDirectory = [System.IO.Path]::GetFullPath($OutputDirectory) }

if (Test-Path -LiteralPath $distributionDirectory) {
    if (Get-ChildItem -LiteralPath $distributionDirectory -Force | Select-Object -First 1) {
        throw "output directory '$distributionDirectory' is not empty"
    }
}
else { [void](New-Item -ItemType Directory -Path $distributionDirectory) }

$stageRoot = [System.IO.Directory]::CreateTempSubdirectory('wcs-release-stage-').FullName
try {
    Push-Location -LiteralPath $rootFull
    try {
        if (-not $NoRestore) { Invoke-DotNet @('restore', 'Wcs.slnx', '--locked-mode') }

        $apps = [ordered]@{
            "wcs-host-$version"       = 'WCS/Wcs.Host/src/Wcs.Host.csproj'
            "wcs-dbtool-$version"     = 'WCS/tools/Wcs.DbTool/Wcs.DbTool.csproj'
            "wcs-sample-wms-$version" = 'samples/Wcs.SampleWms/Wcs.SampleWms.csproj'
        }
        foreach ($name in $apps.Keys) {
            $stage = Join-Path $stageRoot $name
            Invoke-DotNet @('publish', $apps[$name], '-c', 'Release', '--no-restore', '-o', $stage)
            Copy-Item -LiteralPath $script:CommonReleaseFiles -Destination $stage
            Copy-Item -LiteralPath 'licenses' -Destination (Join-Path $stage 'licenses') -Recurse
            if ($name -like 'wcs-sample-wms-*') {
                $plant = Join-Path $stage 'plant'
                [void](New-Item -ItemType Directory -Path $plant)
                Copy-Item -LiteralPath 'samples/plant/sample-plant.json', 'samples/plant/load-sample-plant.ps1' -Destination $plant
            }
            Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $distributionDirectory "$name.zip") -CompressionLevel Optimal
        }
        Copy-Item -LiteralPath 'sbom/wcs.cdx.json' -Destination (Join-Path $distributionDirectory "wcs-$version.cdx.json")
    }
    finally { Pop-Location }

    $expected = @{
        "wcs-host-$version" = @('Wcs.Host.exe', 'Wcs.Host.dll', 'appsettings.json', 'wwwroot/_framework/blazor.web.js', 'Wcs.WebEditor.dll', 'Wcs.Connection.Test.dll')
        "wcs-dbtool-$version" = @('Wcs.DbTool.exe', 'Wcs.DbTool.dll')
        "wcs-sample-wms-$version" = @('Wcs.SampleWms.exe', 'Wcs.SampleWms.dll', 'appsettings.json', 'plant/sample-plant.json', 'plant/load-sample-plant.ps1')
    }
    $archiveProblems = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $expected.Keys) {
        $zipPath = Join-Path $distributionDirectory "$name.zip"
        $entries = Read-ArchiveEntries $zipPath
        foreach ($problem in Test-ArchiveLayout $name $entries $expected[$name]) { $archiveProblems.Add($problem) }
    }
    if ($archiveProblems.Count) { throw ($archiveProblems -join "`n") }

    $manifest = @(Write-ChecksumManifest $distributionDirectory)
    $checksumProblems = @(Test-ChecksumManifest $distributionDirectory $manifest)
    if ($checksumProblems.Count) { throw ($checksumProblems -join "`n") }

    if ($SmokeTest) { Invoke-ReleaseSmokeTest $distributionDirectory $version }

    Write-Host "Release artifacts validated in $distributionDirectory"
    Get-Content -LiteralPath (Join-Path $distributionDirectory 'SHA256SUMS.txt')
}
finally {
    if (Test-Path -LiteralPath $stageRoot) { [System.IO.Directory]::Delete($stageRoot, $true) }
}
