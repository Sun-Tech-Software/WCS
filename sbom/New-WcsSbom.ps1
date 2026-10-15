#Requires -Version 7
<#
.SYNOPSIS
    Writes the CycloneDX SBOM of the public copy of the WCS (sbom/wcs.cdx.json).

.DESCRIPTION
    Uses nothing beyond the .NET SDK and PowerShell (no SBOM tool is downloaded):
    - the NuGet packages come from `dotnet list Wcs.slnx package --include-transitive --format json`
      of the public copy, run after a restore; their licence, authors and copyright are read from
      the .nuspec files of the local NuGet package folder that restore has just filled;
    - the third-party code, fonts and images copied into the repository (the draw.io bundle and
      what it embeds, the font, the deep copy in Common/Helper) come from the hand inventory
      embedded-components.json, kept in step with THIRD-PARTY-NOTICES.md of the overlay.

    A package used only by test projects gets scope "excluded" (it is not part of what runs); the
    others "required". Called by export-public.ps1 (section 9d); can be run alone on any copy:
        pwsh -File New-WcsSbom.ps1 -Root <public copy> -Version <commit>

    Restore writes obj/ folders: they are removed again unless -KeepObj.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Root,
    [string]$Inventory = (Join-Path $PSScriptRoot 'embedded-components.json'),
    [string]$Out = (Join-Path $Root 'sbom/wcs.cdx.json'),
    [string]$Version = '',
    [switch]$NoRestore,
    [switch]$KeepObj
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rootFull = (Resolve-Path $Root).Path.TrimEnd('\')
$solution = Join-Path $rootFull 'Wcs.slnx'
if (-not (Test-Path $solution)) { throw "Wcs.slnx not found in $rootFull" }
$objBefore = @(Get-ChildItem -LiteralPath $rootFull -Recurse -Directory -Filter obj -ErrorAction SilentlyContinue | ForEach-Object FullName)

if (-not $NoRestore) {
    & dotnet restore $solution --verbosity quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $solution" }
}
$json = (& dotnet list $solution package --include-transitive --format json 2>$null) -join "`n"
if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $solution" }
$listing = $json | ConvertFrom-Json

$globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }

# id|version -> what the projects say about it
$packages = [ordered]@{}
foreach ($project in $listing.projects) {
    $projRel = [System.IO.Path]::GetRelativePath($rootFull, $project.path).Replace('\', '/')
    # A test project is one that references the test SDK (Wcs.Connection.Test is the loopback
    # driver, not a test project, whatever its name says)
    $isTest = [System.IO.File]::ReadAllText($project.path) -match 'Microsoft\.NET\.Test\.Sdk|<IsTestProject>\s*true'
    if (-not ($project.PSObject.Properties.Name -contains 'frameworks')) { continue }
    foreach ($fw in $project.frameworks) {
        $sets = @()
        if ($fw.PSObject.Properties.Name -contains 'topLevelPackages') { $sets += , @($fw.topLevelPackages | ForEach-Object { @{ p = $_; direct = $true } }) }
        if ($fw.PSObject.Properties.Name -contains 'transitivePackages') { $sets += , @($fw.transitivePackages | ForEach-Object { @{ p = $_; direct = $false } }) }
        foreach ($set in $sets) {
            foreach ($entry in $set) {
                $p = $entry.p
                $key = "$($p.id)|$($p.resolvedVersion)"
                if (-not $packages.Contains($key)) {
                    $packages[$key] = [ordered]@{ id = $p.id; version = $p.resolvedVersion; projects = [System.Collections.Generic.SortedSet[string]]::new(); direct = $false; runtime = $false }
                }
                [void]$packages[$key].projects.Add($projRel)
                if ($entry.direct) { $packages[$key].direct = $true }
                if (-not $isTest) { $packages[$key].runtime = $true }
            }
        }
    }
}

function Read-Nuspec([string]$id, [string]$version) {
    $dir = Join-Path $globalPackages (Join-Path $id.ToLowerInvariant() $version.ToLowerInvariant())
    $spec = Get-ChildItem -LiteralPath $dir -Filter *.nuspec -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $spec) { return $null }
    [xml]$x = Get-Content -LiteralPath $spec.FullName -Raw
    $md = $x.package.metadata
    $result = [ordered]@{ license = $null; licenseFile = $null; licenseUrl = $null; authors = $null; copyright = $null; projectUrl = $null }
    $names = @($md.ChildNodes | ForEach-Object LocalName)
    if ($names -contains 'license') {
        $lic = $md.license
        $text = if ($lic -is [string]) { $lic } else { $lic.'#text' }
        $type = if ($lic -is [string]) { 'expression' } else { $lic.type }
        if ($type -eq 'expression') { $result.license = $text } else { $result.licenseFile = $text }
    }
    if ($names -contains 'licenseUrl') { $result.licenseUrl = $md.licenseUrl }
    if ($names -contains 'authors') { $result.authors = $md.authors }
    if ($names -contains 'copyright') { $result.copyright = $md.copyright }
    if ($names -contains 'projectUrl') { $result.projectUrl = $md.projectUrl }
    return $result
}

$components = [System.Collections.Generic.List[object]]::new()
foreach ($pkg in $packages.Values) {
    $purl = "pkg:nuget/$($pkg.id)@$($pkg.version)"
    $c = [ordered]@{
        type      = 'library'
        'bom-ref' = $purl
        name      = $pkg.id
        version   = $pkg.version
        scope     = $(if ($pkg.runtime) { 'required' } else { 'excluded' })
        purl      = $purl
    }
    $spec = Read-Nuspec $pkg.id $pkg.version
    if ($spec) {
        if ($spec.authors) { $c.author = $spec.authors }
        if ($spec.license) { $c.licenses = @(@{ expression = $spec.license }) }
        elseif ($spec.licenseFile) { $c.licenses = @(@{ license = [ordered]@{ name = "file in package: $($spec.licenseFile)" } }) }
        elseif ($spec.licenseUrl) { $c.licenses = @(@{ license = [ordered]@{ name = 'see licence URL'; url = $spec.licenseUrl } }) }
        if ($spec.copyright) { $c.copyright = $spec.copyright }
        if ($spec.projectUrl) { $c.externalReferences = @(@{ type = 'website'; url = $spec.projectUrl }) }
    }
    $c.properties = @(
        [ordered]@{ name = 'wcs:dependency'; value = $(if ($pkg.direct) { 'direct' } else { 'transitive' }) }
        [ordered]@{ name = 'wcs:projects'; value = ($pkg.projects -join ', ') }
        [ordered]@{ name = 'wcs:distribution'; value = 'restored from nuget.org, not stored in the repository' }
    )
    $components.Add($c)
}

$inv = Get-Content -LiteralPath $Inventory -Raw | ConvertFrom-Json
foreach ($e in $inv.components) {
    $ref = 'embedded:' + (($e.name -replace '[^A-Za-z0-9.@+-]+', '-').Trim('-'))
    $c = [ordered]@{ type = $e.type; 'bom-ref' = $ref; name = $e.name }
    if ($e.version) { $c.version = $e.version }
    $c.scope = 'required'
    if ($e.license -eq 'NOASSERTION') { }
    elseif ($e.license -match ' (AND|OR|WITH) ' -or $e.license -like 'LicenseRef-*') { $c.licenses = @(@{ expression = $e.license }) }
    else { $c.licenses = @(@{ license = @{ id = $e.license } }) }
    if ($e.copyright) { $c.copyright = $e.copyright }
    if (($e.PSObject.Properties.Name -contains 'purl') -and $e.purl) { $c.purl = $e.purl }
    if ($e.url) { $c.externalReferences = @(@{ type = 'website'; url = $e.url }) }
    $props = [System.Collections.Generic.List[object]]::new()
    $props.Add([ordered]@{ name = 'wcs:location'; value = $e.location })
    $props.Add([ordered]@{ name = 'wcs:distribution'; value = 'copied into the repository' })
    if ($e.licenseText) { $props.Add([ordered]@{ name = 'wcs:licenseText'; value = $e.licenseText }) }
    if ($e.uncertain) { $props.Add([ordered]@{ name = 'wcs:uncertain'; value = $e.uncertain }) }
    $c.properties = $props
    $components.Add($c)
}

$sorted = @($components | Sort-Object { $_['bom-ref'] })
$bom = [ordered]@{
    bomFormat    = 'CycloneDX'
    specVersion  = '1.5'
    serialNumber = "urn:uuid:$([guid]::NewGuid())"
    version      = 1
    metadata     = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        tools     = [ordered]@{ components = @([ordered]@{ type = 'application'; name = 'New-WcsSbom.ps1'; description = 'dotnet list package plus the hand inventory of embedded components' }) }
        component = [ordered]@{
            type      = 'application'
            'bom-ref' = 'sun-tech-wcs'
            name      = 'Sun Tech WCS'
            version   = $(if ($Version) { $Version } else { 'unversioned' })
            supplier  = [ordered]@{ name = 'Sun Tech Srl'; url = @('https://suntechonline.it') }
            licenses  = @(@{ license = @{ id = 'Apache-2.0' } })
        }
    }
    components   = $sorted
    dependencies = @([ordered]@{ ref = 'sun-tech-wcs'; dependsOn = @($sorted | ForEach-Object { $_['bom-ref'] }) })
}
$outDir = Split-Path -Parent $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$text = ($bom | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"
[System.IO.File]::WriteAllText($Out, $text, [System.Text.UTF8Encoding]::new($false))

if (-not $NoRestore -and -not $KeepObj) {
    Get-ChildItem -LiteralPath $rootFull -Recurse -Directory -Filter obj -ErrorAction SilentlyContinue |
        Where-Object { $objBefore -notcontains $_.FullName } | Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object { if (Test-Path $_.FullName) { Remove-Item -Recurse -Force $_.FullName } }
}
$nuget = @($sorted | Where-Object { $_['bom-ref'] -like 'pkg:nuget/*' })
Write-Host ("SBOM: {0} components ({1} NuGet packages, {2} of them for tests only; {3} embedded) -> {4}" -f
    $sorted.Count, $nuget.Count, @($nuget | Where-Object { $_.scope -eq 'excluded' }).Count, ($sorted.Count - $nuget.Count), $Out)
