#Requires -Version 7
<#
.SYNOPSIS
    Verifica che ogni riferimento al codice citato nella documentazione punti a una riga esistente
    e, dove indicato, che quella riga contenga ancora il testo atteso.

.DESCRIPTION
    Cerca nei file .md sotto il percorso indicato i link Markdown nella forma
    [etichetta](percorso/file.estensione:riga) e controlla che il file esista e che
    abbia almeno quel numero di righe (esito "rotto" se non risolve).

    L'etichetta può portare in coda, facoltativamente, un frammento del testo atteso su
    quella riga, racchiuso fra guillemet: [NomeFile.cs:123 «testo atteso»](percorso:123).
    Quando il frammento c'è, lo strumento controlla che compaia nella riga citata; se non
    c'è, il riferimento passa come oggi (nessun controllo di contenuto). Il frammento non
    può contenere la parentesi quadra chiusa ']', che chiuderebbe l'etichetta in anticipo.

    Un riferimento con frammento ha quindi tre esiti possibili: risolve ed è pertinente
    (il frammento è sulla riga citata); risolve ma non è pertinente (il frammento c'è
    altrove nel file, o non c'è affatto — riportato a parte, con la riga trovata quando
    esiste); non risolve affatto (file o riga assenti, come oggi). Un piccolo scarto fra
    la riga citata e quella dove il frammento è stato ritrovato è segnalato come "da
    riallineare" invece che come "spostato", per distinguere una citazione da aggiornare
    di poco da una che ha perso la riga originale.

.PARAMETER Percorso
    Cartella da scandire, relativa alla radice del repo. Default: WCS.

.PARAMETER Radice
    Radice del repository. Default: la cartella due livelli sopra questo script.
#>
[CmdletBinding()]
param(
    [string]$Percorso = 'WCS',
    [string]$Radice = (Resolve-Path (Join-Path $PSScriptRoot '..' '..'))
)

$ErrorActionPreference = 'Stop'

$cartella = if ([System.IO.Path]::IsPathRooted($Percorso)) { $Percorso }
            else { Join-Path $Radice $Percorso }

if (-not (Test-Path $cartella)) {
    Write-Host "Percorso non trovato: $cartella"
    exit 1
}

# Etichetta catturata come gruppo 1 (prima solo scartata) per poterne estrarre il frammento.
$modello = '\[([^\]]*)\]\(([^()\s:]+\.\w+):(\d+)\)'
# Il frammento facoltativo dentro l'etichetta: [Nome.cs:12 «testo atteso»](Nome.cs:12).
$modelloFrammento = '«([^»]+)»'
# Scarto, in righe, entro cui una citazione col frammento spostato è "da riallineare"
# invece che genericamente "spostata": tollera i piccoli scorrimenti da editing.
$tolleranzaRighe = 5

$documenti = Get-ChildItem -Path $cartella -Filter '*.md' -Recurse -File
$righeFile = @{}
$rotti = @()
$nonPertinenti = @()
$totale = 0
$conFrammento = 0

foreach ($documento in $documenti) {
    $percorsoDocumento = [System.IO.Path]::GetRelativePath($Radice, $documento.FullName)
    $numeroRiga = 0
    foreach ($riga in (Get-Content -LiteralPath $documento.FullName)) {
        $numeroRiga++
        foreach ($trovato in [regex]::Matches($riga, $modello)) {
            $totale++
            $etichetta = $trovato.Groups[1].Value
            $percorsoCitato = $trovato.Groups[2].Value
            $rigaCitata = [int]$trovato.Groups[3].Value
            $percorsoAssoluto = Join-Path $Radice $percorsoCitato

            if (-not (Test-Path -LiteralPath $percorsoAssoluto -PathType Leaf)) {
                $rotti += "$($percorsoDocumento):$numeroRiga -> file inesistente: $percorsoCitato"
                continue
            }

            if (-not $righeFile.ContainsKey($percorsoAssoluto)) {
                # Si tiene l'intero file in cache, non solo il conteggio: serve anche per
                # cercare il frammento, e risparmia una seconda lettura quando serve.
                $righeFile[$percorsoAssoluto] = @(Get-Content -LiteralPath $percorsoAssoluto)
            }

            $righe = $righeFile[$percorsoAssoluto]
            $conteggio = $righe.Count

            if ($rigaCitata -lt 1 -or $rigaCitata -gt $conteggio) {
                $rotti += "$($percorsoDocumento):$numeroRiga -> $percorsoCitato ha $conteggio righe, citata la $rigaCitata"
                continue
            }

            $matchFrammento = [regex]::Match($etichetta, $modelloFrammento)
            if (-not $matchFrammento.Success) {
                continue
            }

            $conFrammento++
            $frammento = $matchFrammento.Groups[1].Value

            if ($righe[$rigaCitata - 1].Contains($frammento)) {
                continue
            }

            # Si cerca il frammento partendo dalla riga citata e allargando il raggio
            # simmetricamente, cosi' se compare piu' volte nel file si trova sempre
            # l'occorrenza piu' vicina a quella citata, non la prima del file.
            $rigaTrovata = -1
            $raggioMassimo = [Math]::Max($rigaCitata - 1, $conteggio - $rigaCitata)
            for ($scarto = 1; $scarto -le $raggioMassimo; $scarto++) {
                $indiceSopra = $rigaCitata - 1 - $scarto
                if ($indiceSopra -ge 0 -and $righe[$indiceSopra].Contains($frammento)) {
                    $rigaTrovata = $indiceSopra + 1
                    break
                }
                $indiceSotto = $rigaCitata - 1 + $scarto
                if ($indiceSotto -lt $conteggio -and $righe[$indiceSotto].Contains($frammento)) {
                    $rigaTrovata = $indiceSotto + 1
                    break
                }
            }

            if ($rigaTrovata -gt 0) {
                $scarto = [Math]::Abs($rigaTrovata - $rigaCitata)
                $tipo = if ($scarto -le $tolleranzaRighe) { 'da riallineare' } else { 'contenuto spostato' }
                $nonPertinenti += "$($percorsoDocumento):$numeroRiga -> $($percorsoCitato):$rigaCitata $tipo (scarto $scarto righe): frammento «$frammento» atteso alla $rigaCitata, trovato alla $rigaTrovata"
            } else {
                $nonPertinenti += "$($percorsoDocumento):$numeroRiga -> $($percorsoCitato):$rigaCitata non pertinente: frammento «$frammento» atteso alla riga $rigaCitata, non trovato in nessuna delle $conteggio righe del file"
            }
        }
    }
}

Write-Host "Documenti esaminati: $($documenti.Count)"
Write-Host "Riferimenti controllati: $totale"
if ($conFrammento -gt 0) {
    Write-Host "Riferimenti con frammento di contenuto: $conFrammento"
}

if ($rotti.Count -gt 0) {
    Write-Host "Riferimenti rotti: $($rotti.Count)"
    $rotti | ForEach-Object { Write-Host "  $_" }
}

if ($nonPertinenti.Count -gt 0) {
    Write-Host "Riferimenti non pertinenti: $($nonPertinenti.Count)"
    $nonPertinenti | ForEach-Object { Write-Host "  $_" }
}

if ($rotti.Count -gt 0 -or $nonPertinenti.Count -gt 0) {
    exit 1
}

Write-Host 'Tutti i riferimenti risolvono.'
exit 0
