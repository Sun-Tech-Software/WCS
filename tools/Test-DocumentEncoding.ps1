<#
.SYNOPSIS
    Checks that the Markdown and JSON files of the repository are clean UTF-8 text.

.DESCRIPTION
    Scans every *.md and *.json file under -Path (by default the repository root, the parent of this
    script's folder) and reports, one line per finding, "<file>:<line>:<column>: <problem>":

      - the file is not valid UTF-8 (for example Windows-1252 text);
      - a C0 control character other than tab, CR and LF (U+0000-U+001F), DEL (U+007F), or a C1
        control character (U+0080-U+009F). A C1 character in a document is almost always a
        corrupted escape: a path like "\2026..." written through an escape-processing writer
        becomes U+0082 followed by "6...";
      - U+FFFD, the replacement character left by a lossy conversion;
      - U+FEFF (byte order mark) anywhere but at the very start of the file;
      - mojibake: UTF-8 read as Windows-1252 and written again (U+00C3 U+00A8 for U+00E8 "e grave",
        U+00E2 U+20AC U+2122 for U+2019 "right single quote", U+00C2 U+00B0 for U+00B0 "degree");
      - in JSON, an escaped control character (\u0000-\u001F except \t \n \r written as \u0009,
        \u000A, \u000D; and \u007F-\u009F), the same corruption in escaped form.

    Valid international characters (accents, CJK, typographic quotes, emoji) are not reported.

    Folders bin, obj, node_modules, .git, .vs and TestResults are skipped, and so are third-party
    bundles listed in -ExcludePath (by default the draw.io bundle of the web editor).

    Exit codes: 0 no finding; 1 at least one finding (or, with -SelfTest, a failed self-test);
    2 the scan could not run (the path does not exist).

.PARAMETER Path
    The folder to scan. Default: the repository root.

.PARAMETER ExcludePath
    Paths relative to -Path, with forward slashes, whose files are skipped.

.PARAMETER SelfTest
    Writes known-good and known-bad fixtures to a temporary folder, scans them and checks that every
    defect is found and nothing valid is reported. Exit code 0 when the self-test passes.

.EXAMPLE
    pwsh -NoProfile -File tools/Test-DocumentEncoding.ps1
.EXAMPLE
    pwsh -NoProfile -File tools/Test-DocumentEncoding.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    [string] $Path = (Split-Path -Parent $PSScriptRoot),
    [string[]] $ExcludePath = @('WCS/Wcs.WebEditor/src/wwwroot/drawio'),
    [switch] $SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$skippedFolders = @('bin', 'obj', 'node_modules', '.git', '.vs', 'TestResults')

# Windows-1252 renderings of the bytes 0x80-0xBF (the continuation bytes of UTF-8), the second
# character of a mojibake pair.
$cp1252High = '\u00A0-\u00BF\u20AC\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\u017D\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\u017E\u0178'
$mojibake = [regex]::new("[\u00C2\u00C3][$cp1252High]|\u00E2\u20AC[$cp1252High\u0080-\u009F]?")
$controls = [regex]::new('[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]')
$jsonEscapedControl = [regex]::new('(?<!\\)(?:\\\\)*\\u00(?:[01][0-9A-Fa-f]|7[Ff]|[89][0-9A-Fa-f])')
$jsonAllowedEscapes = @('\u0009', '\u000a', '\u000d')

function Get-CodePointName([int] $codePoint) {
    'U+{0:X4}' -f $codePoint
}

function Test-File([System.IO.FileInfo] $file, [string] $relative) {
    $findings = [System.Collections.Generic.List[string]]::new()
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $strict = [System.Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $strict.GetString($bytes)
    } catch {
        $findings.Add("${relative}: not valid UTF-8 (perhaps Windows-1252 or another legacy code page)")
        return $findings
    }
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) { $text = $text.Substring(1) }
    $isJson = $file.Extension -ieq '.json'
    $lines = $text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i].TrimEnd("`r")
        $number = $i + 1
        foreach ($m in $controls.Matches($line)) {
            $findings.Add("${relative}:${number}:$($m.Index + 1): control character $(Get-CodePointName ([int][char]$m.Value))")
        }
        $at = $line.IndexOf([char]0xFFFD)
        while ($at -ge 0) {
            $findings.Add("${relative}:${number}:$($at + 1): replacement character U+FFFD")
            $at = $line.IndexOf([char]0xFFFD, $at + 1)
        }
        $at = $line.IndexOf([char]0xFEFF)
        while ($at -ge 0) {
            $findings.Add("${relative}:${number}:$($at + 1): byte order mark U+FEFF inside the text")
            $at = $line.IndexOf([char]0xFEFF, $at + 1)
        }
        foreach ($m in $mojibake.Matches($line)) {
            $findings.Add("${relative}:${number}:$($m.Index + 1): mojibake '$($m.Value)' (UTF-8 read as Windows-1252)")
        }
        if ($isJson) {
            foreach ($m in $jsonEscapedControl.Matches($line)) {
                $escape = $m.Value.Substring($m.Value.Length - 6)
                if ($jsonAllowedEscapes -contains $escape.ToLowerInvariant()) { continue }
                $findings.Add("${relative}:${number}:$($m.Index + $m.Length - 5): escaped control character $escape")
            }
        }
    }
    return $findings
}

function Get-DocumentFiles([string] $root, [string[]] $exclude) {
    $rootFull = [System.IO.Path]::GetFullPath($root)
    $excluded = @($exclude | ForEach-Object { $_.Trim('/').Replace('\', '/') + '/' })
    $pending = [System.Collections.Generic.Stack[string]]::new()
    $pending.Push($rootFull)
    while ($pending.Count -gt 0) {
        $dir = $pending.Pop()
        foreach ($sub in [System.IO.Directory]::EnumerateDirectories($dir)) {
            $name = [System.IO.Path]::GetFileName($sub)
            if ($skippedFolders -contains $name) { continue }
            $rel = [System.IO.Path]::GetRelativePath($rootFull, $sub).Replace('\', '/') + '/'
            if ($excluded | Where-Object { $rel.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) }) { continue }
            # Reparse points (junctions, symbolic links) are not followed
            if (([System.IO.File]::GetAttributes($sub) -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $pending.Push($sub)
        }
        foreach ($f in [System.IO.Directory]::EnumerateFiles($dir)) {
            $ext = [System.IO.Path]::GetExtension($f)
            if ($ext -ieq '.md' -or $ext -ieq '.json') {
                [pscustomobject]@{
                    File     = [System.IO.FileInfo]::new($f)
                    Relative = [System.IO.Path]::GetRelativePath($rootFull, $f).Replace('\', '/')
                }
            }
        }
    }
}

function Invoke-Scan([string] $root, [string[]] $exclude) {
    $all = [System.Collections.Generic.List[string]]::new()
    $count = 0
    foreach ($entry in (Get-DocumentFiles $root $exclude | Sort-Object Relative)) {
        $count++
        foreach ($finding in (Test-File $entry.File $entry.Relative)) { $all.Add($finding) }
    }
    [pscustomobject]@{ Files = $count; Findings = $all }
}

function Invoke-SelfTest {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("wcs-encoding-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    try {
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        $write = { param($name, $content) [System.IO.File]::WriteAllText((Join-Path $dir $name), $content, $utf8) }

        # The script stays ASCII (PowerShell reads curly quotes as string delimiters): {U+XXXX}
        # stands for the character.
        $x = { param($s) [regex]::Replace($s, '\{U\+([0-9A-F]{4,5})\}', { param($m) [char]::ConvertFromUtf32([Convert]::ToInt32($m.Groups[1].Value, 16)) }) }

        # Valid text in several languages: nothing must be reported
        & $write 'good.md' (& $x ("# Citt{U+00E0}, Gr{U+00F6}{U+00DF}e, fa{U+00E7}ade, {U+00D1}and{U+00FA}, S{U+00E3}o Paulo, N{U+00C3}O`r`n" +
            "Typographic {U+2018}quotes{U+2019} {U+201C}double{U+201D} {U+2014} dash {U+2026} ellipsis, 50 {U+00B0}C, {U+00A9} 2026, {U+20AC} 10, {U+2122}`r`n" +
            "{U+65E5}{U+672C}{U+8A9E} {U+D55C}{U+AD6D}{U+C5B4} {U+0395}{U+03BB}{U+03BB}{U+03B7}{U+03BD}{U+03B9}{U+03BA}{U+03AC} {U+0420}{U+0443}{U+0441} {U+05E2}{U+05D1} {U+0627}{U+0644} {U+1F680}`r`n" +
            'Path <root>\Scheduled\20261015-122143 and a' + "`t" + "tab-separated table cell`r`n"))
        & $write 'good.json' (& $x '{ "name": "Citt{U+00E0} {U+2014} {U+2018}ok{U+2019} {U+65E5}{U+672C}", "tab": "a\tb", "nl": "a\u000Ab", "path": "C:\\Data\\2026", "lit": "\\u0082 is text" }')
        # A BOM at the start is allowed
        [System.IO.File]::WriteAllText((Join-Path $dir 'bom.md'), "# With a byte order mark`r`n", [System.Text.UTF8Encoding]::new($true))

        # Defects
        & $write 'c1.md' ("Restore <root>\Scheduled$([char]0x82)61015-122143`r`n")
        & $write 'c0.md' ("A bell $([char]0x07) and an escape $([char]0x1B)`n")
        & $write 'fffd.md' ("Lost character $([char]0xFFFD) here`n")
        # "a", "e acute", "right single quote" and "degree" encoded in UTF-8 and read back as Windows-1252
        & $write 'mojibake.md' (& $x "Citt{U+00C3}{U+00A0} perch{U+00C3}{U+00A9} l{U+00E2}{U+20AC}{U+2122}ordine, 50 {U+00C2}{U+00B0}C`n")
        & $write 'midbom.md' ("Text$([char]0xFEFF)here`n")
        & $write 'escaped.json' ('{ "path": "C:\\Scheduled\u008261015", "nul": "\u0000" }')
        [System.IO.File]::WriteAllBytes((Join-Path $dir 'cp1252.md'), [byte[]](0x43, 0x69, 0x74, 0x74, 0xE0, 0x0A))
        # Excluded folders are not scanned
        New-Item -ItemType Directory -Path (Join-Path $dir 'bin'), (Join-Path $dir 'vendor/bundle') | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $dir 'bin/skip.md'), "bad $([char]0x82)", $utf8)
        [System.IO.File]::WriteAllText((Join-Path $dir 'vendor/bundle/skip.json'), "bad $([char]0xFFFD)", $utf8)

        $result = Invoke-Scan $dir @('vendor/bundle')
        $expected = [ordered]@{
            'c0.md'        = 2
            'c1.md'        = 1
            'cp1252.md'    = 1
            'escaped.json' = 2
            'fffd.md'      = 1
            'midbom.md'    = 1
            'mojibake.md'  = 4
        }
        $failures = [System.Collections.Generic.List[string]]::new()
        $byFile = $result.Findings | Group-Object { ($_ -split ':', 2)[0] }
        foreach ($name in 'good.md', 'good.json', 'bom.md', 'bin/skip.md', 'vendor/bundle/skip.json') {
            $hit = $byFile | Where-Object Name -eq $name
            if ($hit) { $failures.Add("$name should be clean, got: $($hit.Group -join ' | ')") }
        }
        foreach ($name in $expected.Keys) {
            $hit = $byFile | Where-Object Name -eq $name
            $got = if ($hit) { $hit.Count } else { 0 }
            if ($got -ne $expected[$name]) {
                $failures.Add("$name expected $($expected[$name]) finding(s), got ${got}: $(if ($hit) { $hit.Group -join ' | ' })")
            }
        }
        if ($result.Files -ne 10) { $failures.Add("expected 10 files scanned, got $($result.Files)") }

        if ($failures.Count -gt 0) {
            Write-Host "Self-test FAILED:"
            $failures | ForEach-Object { Write-Host "  $_" }
            return 1
        }
        Write-Host "Self-test passed: $($result.Findings.Count) defects found in 7 bad fixtures, 3 valid fixtures clean, excluded folders skipped."
        return 0
    } finally {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) { exit (Invoke-SelfTest) }

if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
    Write-Host "Test-DocumentEncoding: folder not found: $Path"
    exit 2
}

$result = Invoke-Scan $Path $ExcludePath
if ($result.Findings.Count -eq 0) {
    Write-Host "Document encoding: $($result.Files) Markdown and JSON files checked, no defect."
    exit 0
}
$result.Findings | ForEach-Object { Write-Host $_ }
Write-Host "Document encoding: $($result.Findings.Count) defect(s) in $(@($result.Findings | ForEach-Object { ($_ -split ':', 2)[0] } | Sort-Object -Unique).Count) of $($result.Files) files."
exit 1
