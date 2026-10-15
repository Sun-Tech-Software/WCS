#Requires -Version 7
# Verifica il comportamento di verifica-riferimenti-doc.ps1 su un caso buono e su tre casi rotti.

$ErrorActionPreference = 'Stop'
$radice = Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')
$script = Join-Path $radice 'WCS' 'tools' 'verifica-riferimenti-doc.ps1'

$temporanea = Join-Path ([System.IO.Path]::GetTempPath()) "verifica-doc-$(New-Guid)"
$cartellaDoc = Join-Path $temporanea 'doc'
New-Item -ItemType Directory -Path $cartellaDoc -Force | Out-Null

$fallimenti = @()

function Assert-Uguale($atteso, $ottenuto, $descrizione) {
    if ($atteso -ne $ottenuto) {
        $script:fallimenti += "$descrizione : atteso '$atteso', ottenuto '$ottenuto'"
    }
}

# Caso 1: riferimento valido -> esito 0
@'
Il navigatore parte da [Navigator.cs:478](WCS/Wcs.Engine/src/Navigator.cs:478).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'buono.md') -Encoding utf8

& $script -Percorso $temporanea -Radice $radice | Out-Null
Assert-Uguale 0 $LASTEXITCODE 'Riferimento valido'

# Caso 2: riga oltre la fine del file -> esito 1
@'
Riga inesistente in [Navigator.cs:999999](WCS/Wcs.Engine/src/Navigator.cs:999999).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'riga-alta.md') -Encoding utf8

& $script -Percorso $temporanea -Radice $radice | Out-Null
Assert-Uguale 1 $LASTEXITCODE 'Riga oltre la fine del file'

Remove-Item (Join-Path $cartellaDoc 'riga-alta.md')

# Caso 3: file inesistente -> esito 1
@'
File assente in [Fantasma.cs:1](WCS/Wcs.Engine/src/Fantasma.cs:1).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'file-assente.md') -Encoding utf8

& $script -Percorso $temporanea -Radice $radice | Out-Null
Assert-Uguale 1 $LASTEXITCODE 'File inesistente'

Remove-Item (Join-Path $cartellaDoc 'file-assente.md')

# Caso 4: riga zero -> esito 1
@'
Riga zero in [Navigator.cs:0](WCS/Wcs.Engine/src/Navigator.cs:0).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'riga-zero.md') -Encoding utf8

& $script -Percorso $temporanea -Radice $radice | Out-Null
Assert-Uguale 1 $LASTEXITCODE 'Riga zero'

Remove-Item (Join-Path $cartellaDoc 'riga-zero.md')

# Caso 5: riferimento rotto verso un file non .cs (es. .csproj) -> deve essere rilevato (rilievo 1)
@'
Riferimento rotto in [Fantasma.csproj:1](WCS/Wcs.Engine/src/Fantasma.csproj:1).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'estensione-non-cs.md') -Encoding utf8

& $script -Percorso $temporanea -Radice $radice | Out-Null
Assert-Uguale 1 $LASTEXITCODE 'Riferimento rotto verso file non .cs deve essere rilevato'

Remove-Item (Join-Path $cartellaDoc 'estensione-non-cs.md')

# Caso 6: la diagnosi riporta il percorso relativo alla radice, non il solo nome del file (rilievo 2)
$cartellaA = Join-Path $cartellaDoc 'moduloA'
$cartellaB = Join-Path $cartellaDoc 'moduloB'
New-Item -ItemType Directory -Path $cartellaA -Force | Out-Null
New-Item -ItemType Directory -Path $cartellaB -Force | Out-Null

@'
Riferimento rotto in [Fantasma.cs:1](WCS/Wcs.Engine/src/Fantasma.cs:1).
'@ | Set-Content -Path (Join-Path $cartellaA 'architettura.md') -Encoding utf8

@'
Riferimento rotto in [Fantasma.cs:1](WCS/Wcs.Engine/src/Fantasma.cs:1).
'@ | Set-Content -Path (Join-Path $cartellaB 'architettura.md') -Encoding utf8

$output6 = & $script -Percorso $temporanea -Radice $radice 6>&1 | Out-String
Assert-Uguale 1 $LASTEXITCODE 'Due documenti omonimi con riferimenti rotti'

if ($output6 -notmatch 'moduloA[\\/]architettura\.md') {
    $script:fallimenti += "Diagnosi con percorso relativo : atteso un rigo con 'moduloA/architettura.md', ottenuto: $output6"
}
if ($output6 -notmatch 'moduloB[\\/]architettura\.md') {
    $script:fallimenti += "Diagnosi con percorso relativo : atteso un rigo con 'moduloB/architettura.md', ottenuto: $output6"
}

Remove-Item -Recurse -Force $cartellaA
Remove-Item -Recurse -Force $cartellaB

# Caso 7: un link Markdown verso un altro documento (con ancora) non e' un riferimento al codice
@'
Vedi [approfondimento](../../doc/debito-tecnico.md#ancora) per i dettagli.
'@ | Set-Content -Path (Join-Path $cartellaDoc 'link-doc.md') -Encoding utf8

$output7 = & $script -Percorso $temporanea -Radice $radice 6>&1 | Out-String
Assert-Uguale 0 $LASTEXITCODE 'Link Markdown con ancora non deve essere un riferimento al codice'

if ($output7 -notmatch 'Riferimenti controllati: 1') {
    $script:fallimenti += "Link con ancora scambiato per riferimento al codice : atteso 'Riferimenti controllati: 1', ottenuto: $output7"
}

Remove-Item (Join-Path $cartellaDoc 'link-doc.md')

# Caso 8: riferimento con frammento che corrisponde alla riga citata -> esito 0
# (Navigator.cs:478 e' "        if (mission != null)")
@'
Frammento pertinente in [Navigator.cs:478 «if (mission != null)»](WCS/Wcs.Engine/src/Navigator.cs:478).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'frammento-pertinente.md') -Encoding utf8

$output8 = & $script -Percorso $temporanea -Radice $radice 6>&1 | Out-String
Assert-Uguale 0 $LASTEXITCODE 'Frammento presente sulla riga citata'

if ($output8 -notmatch 'Tutti i riferimenti risolvono') {
    $script:fallimenti += "Frammento pertinente : atteso 'Tutti i riferimenti risolvono', ottenuto: $output8"
}

Remove-Item (Join-Path $cartellaDoc 'frammento-pertinente.md')

# Caso 9: riferimento con frammento che non compare in nessuna riga del file -> esito 1
@'
Frammento assente in [Navigator.cs:478 «testo che non esiste nel file XYZQWERTY»](WCS/Wcs.Engine/src/Navigator.cs:478).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'frammento-assente.md') -Encoding utf8

$output9 = & $script -Percorso $temporanea -Radice $radice 6>&1 | Out-String
Assert-Uguale 1 $LASTEXITCODE 'Frammento non trovato in nessuna riga del file'

if ($output9 -notmatch 'non trovato in nessuna delle') {
    $script:fallimenti += "Frammento assente : atteso un rigo con 'non trovato in nessuna delle', ottenuto: $output9"
}
if ($output9 -match 'file inesistente') {
    $script:fallimenti += "Frammento assente : non deve finire fra i riferimenti rotti, ottenuto: $output9"
}

Remove-Item (Join-Path $cartellaDoc 'frammento-assente.md')

# Caso 10: riferimento con frammento che compare a poche righe di distanza da quella citata
# -> esito 1, segnalato come "da riallineare" e non come rotto (la riga 480 e' vuota, il
# frammento sta invece alla 478, due righe piu' su, entro la tolleranza)
@'
Frammento spostato in [Navigator.cs:480 «if (mission != null)»](WCS/Wcs.Engine/src/Navigator.cs:480).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'frammento-vicino.md') -Encoding utf8

$output10 = & $script -Percorso $temporanea -Radice $radice 6>&1 | Out-String
Assert-Uguale 1 $LASTEXITCODE 'Frammento trovato a poche righe da quella citata'

if ($output10 -notmatch 'da riallineare') {
    $script:fallimenti += "Frammento vicino : atteso un rigo con 'da riallineare', ottenuto: $output10"
}
if ($output10 -notmatch 'trovato alla 478') {
    $script:fallimenti += "Frammento vicino : atteso che riporti la riga trovata (478), ottenuto: $output10"
}
if ($output10 -match 'file inesistente') {
    $script:fallimenti += "Frammento vicino : non deve finire fra i riferimenti rotti, ottenuto: $output10"
}

Remove-Item (Join-Path $cartellaDoc 'frammento-vicino.md')

# Caso 11: riferimento senza frammento (sintassi di sempre) -> continua a risolvere come
# prima, senza alcun controllo di contenuto, anche se la riga citata non e' pertinente a
# nulla in particolare
@'
Riferimento senza frammento in [Navigator.cs:478](WCS/Wcs.Engine/src/Navigator.cs:478).
'@ | Set-Content -Path (Join-Path $cartellaDoc 'senza-frammento.md') -Encoding utf8

$output11 = & $script -Percorso $temporanea -Radice $radice 6>&1 | Out-String
Assert-Uguale 0 $LASTEXITCODE 'Riferimento senza frammento resta compatibile con il comportamento di prima'

if ($output11 -notmatch 'Tutti i riferimenti risolvono') {
    $script:fallimenti += "Riferimento senza frammento : atteso 'Tutti i riferimenti risolvono', ottenuto: $output11"
}

Remove-Item (Join-Path $cartellaDoc 'senza-frammento.md')

Remove-Item -Recurse -Force $temporanea

if ($fallimenti.Count -gt 0) {
    $fallimenti | ForEach-Object { Write-Host "FALLITO: $_" }
    exit 1
}

Write-Host 'Tutti i casi passati.'
exit 0
