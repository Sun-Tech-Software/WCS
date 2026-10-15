#Requires -Version 7
<#
.SYNOPSIS
    Checks that sbom/wcs.cdx.json still describes the repository.

.DESCRIPTION
    Regenerates the SBOM with New-WcsSbom.ps1 (after a restore) into -Out and compares it with the
    committed sbom/wcs.cdx.json: the components (NuGet packages with their licences and the
    projects that use them, and the embedded components of embedded-components.json) and the
    version of the WCS must be the same. The serial number and the timestamp, which change at every
    generation, are not compared. Exits with an error, listing what differs, when they are not.

    After a package change, regenerate the committed file and commit it with the change:
        pwsh -File sbom/New-WcsSbom.ps1 -Root . -Version <the Version of Directory.Build.props>
#>
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    [string]$Out = (Join-Path ([System.IO.Path]::GetTempPath()) 'wcs.cdx.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$rootFull = (Resolve-Path $Root).Path
$committedPath = Join-Path $rootFull 'sbom/wcs.cdx.json'
if (-not (Test-Path -LiteralPath $committedPath)) { throw "sbom/wcs.cdx.json not found in $rootFull" }

[xml]$props = Get-Content -LiteralPath (Join-Path $rootFull 'Directory.Build.props') -Raw
$version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText

& (Join-Path $PSScriptRoot 'New-WcsSbom.ps1') -Root $rootFull -Out $Out -Version $version

$committed = Get-Content -LiteralPath $committedPath -Raw | ConvertFrom-Json -AsHashtable
$fresh = Get-Content -LiteralPath $Out -Raw | ConvertFrom-Json -AsHashtable

$differences = [System.Collections.Generic.List[string]]::new()
if ($committed.metadata.component.version -ne $fresh.metadata.component.version) {
    $differences.Add("version: committed '$($committed.metadata.component.version)', regenerated '$($fresh.metadata.component.version)'")
}
# Keys are compared sorted: the generator writes some objects from plain hashtables, whose key order
# changes from one PowerShell process to the next (string hashing is randomised in .NET)
function ConvertTo-Canonical($value) {
    if ($value -is [System.Collections.IDictionary]) {
        $sorted = [ordered]@{}
        foreach ($key in ($value.Keys | Sort-Object { [string]$_ } -CaseSensitive)) { $sorted[$key] = ConvertTo-Canonical $value[$key] }
        return $sorted
    }
    if ($value -is [System.Collections.IList]) { return , @($value | ForEach-Object { ConvertTo-Canonical $_ }) }
    return $value
}
$byRef = { param($bom) $map = [ordered]@{}; foreach ($c in $bom.components) { $map[$c['bom-ref']] = (ConvertTo-Canonical $c | ConvertTo-Json -Depth 20 -Compress) }; $map }
$old = & $byRef $committed
$new = & $byRef $fresh
foreach ($ref in $new.Keys) {
    if (-not $old.Contains($ref)) { $differences.Add("added: $ref") }
    elseif ($old[$ref] -ne $new[$ref]) { $differences.Add("changed: $ref") }
}
foreach ($ref in $old.Keys) { if (-not $new.Contains($ref)) { $differences.Add("removed: $ref") } }

if ($differences.Count -gt 0) {
    $differences | ForEach-Object { Write-Host "  $_" }
    throw "sbom/wcs.cdx.json is out of date ($($differences.Count) differences): regenerate it with sbom/New-WcsSbom.ps1 and commit it"
}
Write-Host "sbom/wcs.cdx.json matches the repository: $($new.Count) components, version $version."
