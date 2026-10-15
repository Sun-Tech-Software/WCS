<#
.SYNOPSIS
Esporta dispositivi e giunzioni dell'impianto dall'host WCS in esecuzione, per la fixture dei test
della ricerca dei percorsi.

.DESCRIPTION
Legge /plant/items, /plant/items/config e /plant/junctions e scrive un JSON con i nomi corti
(ASI), il tipo, il livello di gruppo e gli archi con la priorita'. Gli identificatori non servono
ai test, che ricreano i dispositivi in memoria.
#>
param(
    [string]$HostAddress = 'http://localhost:5580',
    [string]$ApiKey = $env:Wcs__ApiKey,
    [string]$Out = "$PSScriptRoot\..\Wcs.Engine\tests\Wcs.Engine.Test\Routing\Fixtures\exported-plant.json"
)

$ErrorActionPreference = 'Stop'
$headers = @{ 'X-Api-Key' = $ApiKey }
$items = Invoke-RestMethod "$HostAddress/api/v1/plant/items" -Headers $headers
$configs = Invoke-RestMethod "$HostAddress/api/v1/plant/items/config" -Headers $headers
$junctions = Invoke-RestMethod "$HostAddress/api/v1/plant/junctions" -Headers $headers

$levelById = @{}
foreach ($c in $configs) { $levelById[[string]$c.itemId] = [int]$c.groupRoutePathId }
$nameById = @{}
foreach ($i in $items) {
    if ($nameById.ContainsValue($i.asi)) { throw "ASI duplicato: $($i.asi)" }
    $nameById[[string]$i.itemId] = $i.asi
}

$fixture = [ordered]@{
    source = "$HostAddress il $(Get-Date -Format 'yyyy-MM-dd')"
    items = @($items | Sort-Object asi | ForEach-Object {
        # Un dispositivo senza configurazione non ha livello: vale 0, come il motore, invece di
        # finire nel JSON come null e diventare un livello mancante nella fixture.
        [ordered]@{ name = $_.asi; deviceType = [int]$_.deviceType; level = [int]($levelById[[string]$_.itemId] ?? 0) }
    })
    # L'ordinamento va fatto sugli oggetti dell'API, prima della proiezione: Sort-Object non
    # risolve le chiavi di un dizionario ordinato come proprieta', e non ordinerebbe nulla.
    junctions = @($junctions
        | Sort-Object { $nameById[[string]$_.sourceItemId] }, { $nameById[[string]$_.destinationItemId] }
        | ForEach-Object {
            [ordered]@{ from = $nameById[[string]$_.sourceItemId]; to = $nameById[[string]$_.destinationItemId]; priority = [int]$_.priority }
        })
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fixture | ConvertTo-Json -Depth 4 | Set-Content -Path $Out -Encoding utf8
Write-Host "Scritti $($fixture.items.Count) dispositivi e $($fixture.junctions.Count) giunzioni in $Out"
