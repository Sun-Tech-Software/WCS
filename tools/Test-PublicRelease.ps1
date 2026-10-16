#Requires -Version 7
<#
.SYNOPSIS
    Checks that the repository is ready to be released, without releasing anything.

.DESCRIPTION
    Runs, on the working tree of a clone, every check that a release of the WCS depends on, and prints
    a summary table at the end. It exits with code 1 when any mandatory check fails, 0 otherwise.

    Checks, in order:
      1. the .NET SDK in use is the one pinned by global.json (or a later patch of its feature band);
      2. the working tree is clean at the start (unless -AllowDirtyWorkingTree);
      3. dotnet restore Wcs.slnx --locked-mode;
      4. a clean dotnet build Wcs.slnx -c Release with warnings treated as errors;
      5. every test project of Wcs.slnx except the browser tests, one by one;
      6. with -IncludeBrowserTests, the Playwright suite as its README says (host built in Debug,
         WCS_HOST_EXE); the Playwright browsers must already be installed;
      7. the Markdown links (tools/Wcs.LinkCheck);
      8. the machine-readable contracts and their examples (tools/Wcs.ContractCheck check);
      9. the SBOM against the restored packages (sbom/Test-WcsSbom.ps1);
     10. no known vulnerable package, direct or transitive (unless -SkipVulnerabilityCheck, which
         prints a warning and is never the default);
     11. no anomalous control or replacement characters in the current documentation
         (tools/Test-DocumentEncoding.ps1 when present, otherwise the equivalent check built in here);
     12. one version everywhere: Directory.Build.props, sbom/wcs.cdx.json, CHANGELOG.md and the
         current documentation;
     13. the community files are present and tracked;
     14. every external GitHub Action is pinned to an immutable commit and the Gitleaks archive is
         verified against the trusted digest stored in the release workflow;
     15. no leftover placeholders in the public documents (see Find-Placeholder below);
     16. no secrets: gitleaks over the git history when gitleaks is found, and a built-in pattern
         check over the tracked files in any case;
     17. no personal or absolute local paths in tracked files;
     18. with -ReleaseTag, the tag matches the version and CHANGELOG.md has its dated section;
     19. the repository URL (-RepositoryUrl, or RepositoryUrl of Directory.Build.props) is the one
         used by Directory.Build.props, README.md and the issue templates;
     20. with -IncludeReleaseDryRun (always with -ReleaseTag), the three release archives are built,
         validated, checksummed and smoke-tested from their extracted contents;
     21. the working tree is as it was at the start: no tool dirtied it.

    The script never deletes or edits a tracked file, never resets, commits, tags, pushes or creates a
    remote release, and never edits CHANGELOG.md. It writes build outputs (bin/, obj/, which .gitignore
    covers), reports and, for the artifact dry-run, dotnet publish outputs in a new folder under the
    system temporary folder (or -ReleaseArtifactsDirectory), which it names at the end and leaves in place.

    -SelfTest runs the built-in tests of the checking functions on in-memory fixtures and does
    nothing else (no build, no file of the repository read).

.EXAMPLE
    pwsh -File tools/Test-PublicRelease.ps1

.EXAMPLE
    pwsh -File tools/Test-PublicRelease.ps1 -ReleaseTag v0.1.0-preview.1 -IncludeBrowserTests -IncludeReleaseDryRun

.EXAMPLE
    pwsh -File tools/Test-PublicRelease.ps1 -SelfTest
#>
[CmdletBinding()]
param(
    # The root of the clone. Defaults to the parent of the folder of this script.
    [string]$Root = (Split-Path -Parent $PSScriptRoot),
    # Accept local changes at the start. The final check still fails if the tools change anything.
    [switch]$AllowDirtyWorkingTree,
    # Also run the Playwright suite (slow; needs the Playwright browsers installed once).
    [switch]$IncludeBrowserTests,
    # Build, inspect and smoke-test the three distributable archives. Always enabled with -ReleaseTag.
    [switch]$IncludeReleaseDryRun,
    # Where the validated archives are written. Defaults to this run's temporary report folder.
    [string]$ReleaseArtifactsDirectory,
    # Skip the vulnerable-package check (it needs nuget.org). Prints a warning; never for a release.
    [switch]$SkipVulnerabilityCheck,
    # The tag about to be created, vX.Y.Z or vX.Y.Z-preview.N. Nothing is created.
    [string]$ReleaseTag,
    # The URL of the public repository, for example https://github.com/Sun-Tech-Software/WCS.
    [string]$RepositoryUrl,
    # The gitleaks executable. Otherwise $env:GITLEAKS_PATH, then gitleaks on the PATH.
    [string]$GitleaksPath,
    # Run the self-tests of the checking functions and exit.
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# =================================================================================================
# Checking functions. They work on text and values, not on files, so that -SelfTest can feed them
# fixtures. Each returns a list of problems (empty when all is well).
# =================================================================================================

$script:VersionPattern = '^\d+\.\d+\.\d+(-preview\.\d+)?$'

function Test-SdkVersion {
    # rollForward latestPatch: same major.minor and feature band (the hundreds of the patch), patch
    # equal or later. allowPrerelease false: no prerelease SDK.
    param([string]$Required, [string]$Actual, [string]$RollForward = 'latestPatch', [bool]$AllowPrerelease = $false)
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($Actual -notmatch '^(\d+)\.(\d+)\.(\d+)(-.+)?$') { $problems.Add("cannot read the SDK version '$Actual'"); return , $problems }
    $a = [int[]]@($Matches[1], $Matches[2], $Matches[3]); $aPre = $Matches[4]
    if ($Required -notmatch '^(\d+)\.(\d+)\.(\d+)$') { $problems.Add("global.json: cannot read the SDK version '$Required'"); return , $problems }
    $r = [int[]]@($Matches[1], $Matches[2], $Matches[3])
    if ($aPre -and -not $AllowPrerelease) { $problems.Add("SDK $Actual is a prerelease and global.json does not allow it") }
    switch ($RollForward) {
        'disable' { if ($Actual -ne $Required) { $problems.Add("SDK $Actual, global.json requires exactly $Required") } }
        'latestPatch' {
            if ($a[0] -ne $r[0] -or $a[1] -ne $r[1] -or [math]::Floor($a[2] / 100) -ne [math]::Floor($r[2] / 100) -or $a[2] -lt $r[2]) {
                $problems.Add("SDK $Actual is not $Required or a later patch of the same feature band")
            }
        }
        default {
            if ($a[0] -ne $r[0] -or $a[1] -lt $r[1] -or ($a[1] -eq $r[1] -and $a[2] -lt $r[2])) { $problems.Add("SDK $Actual is older than $Required") }
        }
    }
    return , $problems
}

function Get-ChangelogSection {
    # The section '## [Version] ...' of CHANGELOG.md: its heading, whether it is dated, its date and
    # its body without the heading (up to the next '## [' or the link definitions at the bottom).
    param([string[]]$Lines, [string]$Version)
    $start = -1
    for ($i = 0; $i -lt $Lines.Count; $i++) { if ($Lines[$i].StartsWith("## [$Version]")) { $start = $i; break } }
    if ($start -lt 0) { return $null }
    $end = $Lines.Count
    for ($i = $start + 1; $i -lt $Lines.Count; $i++) { if ($Lines[$i] -match '^## \[' -or $Lines[$i] -match '^\[[^\]]+\]: ') { $end = $i; break } }
    $date = $null
    if ($Lines[$start] -match '^## \[[^\]]+\] - (\d{4}-\d{2}-\d{2})\s*$') { $date = $Matches[1] }
    $body = if ($end - 1 -ge $start + 1) { ($Lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
    [pscustomobject]@{ Heading = $Lines[$start]; Dated = [bool]$date; Date = $date; Body = $body; Index = $start }
}

function Test-ChangelogVersion {
    # Without a tag: the first version section after [Unreleased] is the version of the repository,
    # and the link definition of the version is there. With a tag: the section is also dated (a real
    # date, not later than tomorrow) and not empty, as the release workflow requires.
    param([string[]]$Lines, [string]$Version, [string]$RepositoryUrl, [switch]$ForRelease, [datetime]$Today = (Get-Date).Date)
    $problems = [System.Collections.Generic.List[string]]::new()
    $headings = @($Lines | Where-Object { $_ -match '^## \[' })
    if (-not $headings -or $headings[0] -notmatch '^## \[Unreleased\]') { $problems.Add("CHANGELOG.md: the first section is not '## [Unreleased]'") }
    $firstVersion = $headings | Where-Object { $_ -notmatch '^## \[Unreleased\]' } | Select-Object -First 1
    $section = Get-ChangelogSection -Lines $Lines -Version $Version
    if (-not $section) { $problems.Add("CHANGELOG.md has no section '## [$Version]'"); return , $problems }
    if ($firstVersion -and -not $firstVersion.StartsWith("## [$Version]")) { $problems.Add("CHANGELOG.md: the latest version section is '$firstVersion', not $Version") }
    if ($RepositoryUrl) {
        $link = "[$Version]: $RepositoryUrl/releases/tag/v$Version"
        if (-not ($Lines -contains $link)) { $problems.Add("CHANGELOG.md has no link definition '$link'") }
    }
    if ($ForRelease) {
        if (-not $section.Dated) { $problems.Add("the CHANGELOG.md section is not dated: '$($section.Heading)' (write the date of the tag)") }
        else {
            $d = [datetime]::MinValue
            if (-not [datetime]::TryParseExact($section.Date, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture, 'None', [ref]$d)) { $problems.Add("CHANGELOG.md: '$($section.Date)' is not a date") }
            elseif ($d -gt $Today.AddDays(1)) { $problems.Add("CHANGELOG.md: the section is dated $($section.Date), in the future") }
        }
        if (-not $section.Body) { $problems.Add("the CHANGELOG.md section of $Version is empty") }
        # The section becomes the text of the GitHub release: the note of the undated state must go
        foreach ($m in [regex]::Matches($section.Body, '(?im)^.*(not yet released|date is written here).*$')) {
            $problems.Add("the CHANGELOG.md section of $Version still says '$($m.Value.Trim())': remove it when dating the section")
        }
    }
    return , $problems
}

function Test-ReleaseTagName {
    param([string]$Tag, [string]$Version)
    $problems = [System.Collections.Generic.List[string]]::new()
    if ($Tag -notmatch '^v\d+\.\d+\.\d+(-preview\.\d+)?$') { $problems.Add("tag '$Tag' is not vX.Y.Z or vX.Y.Z-preview.N") }
    if ($Tag -cne "v$Version") { $problems.Add("tag '$Tag' does not match the version of Directory.Build.props (v$Version)") }
    return , $problems
}

function Find-VersionMismatch {
    # In a current document: every X.Y.Z-preview.N must be the version of the repository, and every
    # assembly version shown in an output ("version":"X.Y.Z.W", "version X.Y.Z.W") must be the one
    # the build gives that version (X.Y.Z.0). CHANGELOG.md is not passed here: it lists past versions.
    param([string]$Path, [string]$Text, [string]$Version)
    $problems = [System.Collections.Generic.List[string]]::new()
    $assembly = ($Version -replace '-.*$', '') + '.0'
    $lines = $Text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in [regex]::Matches($lines[$i], '(?<![\d.])\d+\.\d+\.\d+-preview\.\d+(?!\d)')) {
            if ($m.Value -ne $Version) { $problems.Add("${Path}:$($i + 1): version $($m.Value), the repository is $Version") }
        }
        foreach ($m in [regex]::Matches($lines[$i], '(?i)"?version"?\s*[:=]?\s*"?(\d+\.\d+\.\d+\.\d+)"?')) {
            if ($m.Groups[1].Value -ne $assembly) { $problems.Add("${Path}:$($i + 1): assembly version $($m.Groups[1].Value), the build of $Version gives $assembly") }
        }
    }
    return , $problems
}

function Find-AnomalousCharacter {
    # Control characters other than tab, line feed and carriage return; C1 controls and the
    # replacement character (the marks of a wrong decoding); a byte order mark after the start;
    # bidirectional overrides and zero-width characters (they hide text); and the common
    # double-encoding sequences of UTF-8 read as Windows-1252 (for example "Ã¨" for "è").
    param([string]$Path, [string]$Text)
    $problems = [System.Collections.Generic.List[string]]::new()
    $body = if ($Text.Length -gt 0 -and $Text[0] -eq [char]0xFEFF) { $Text.Substring(1) } else { $Text }
    $pattern = '[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F\uFFFD\uFEFF\u200B-\u200F\u202A-\u202E\u2066-\u2069]|\u00C3[\u0080-\u00BF\u2018-\u201E\u2020-\u2022\u2026\u2030\u0152\u0153\u0160\u0161\u0178\u017D\u017E]|\u00E2\u20AC'
    $lines = $body -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($m in [regex]::Matches($lines[$i], $pattern)) {
            $codes = ($m.Value.ToCharArray() | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ' '
            $problems.Add("${Path}:$($i + 1): $codes")
        }
    }
    return , $problems
}

function Find-Placeholder {
    # In a public document (Markdown, issue forms):
    #   - TODO, FIXME, TBD, XXX (upper case, whole words), TO BE WRITTEN, CHANGEME, REPLACE ME: never,
    #     not even in code;
    #   - an angle-bracketed "your" placeholder (your-key, your key, your_key between < and >): allowed only inside code (fenced blocks or `inline`),
    #     where they are the documented way to write a value of your own; in prose they are a
    #     sentence left unfinished.
    # In any other tracked text file (settings, scripts, code, workflows) such a placeholder is refused,
    # except in files that are examples by name (*.example.*, *.sample.*) or test fixtures.
    param([string]$Path, [string]$Text)
    $problems = [System.Collections.Generic.List[string]]::new()
    $isMarkdown = $Path -match '\.(md|markdown)$'
    $isPublicDoc = $isMarkdown -or $Path -like '.github/*'
    $isExample = $Path -match '\.(example|sample)\.[^/]+$' -or $Path -match '(^|/)(tests?|[^/]+\.Test|Fixtures)/' -or $Path -like 'docs/contracts/examples/*'
    $lines = $Text -split "`n"
    $inFence = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($isPublicDoc) {
            foreach ($m in [regex]::Matches($line, '\b(TODO|FIXME|TBD|XXX|CHANGEME)\b|TO BE WRITTEN|REPLACE[ _]ME')) {
                $problems.Add("${Path}:$($i + 1): marker '$($m.Value)'")
            }
        }
        if ($isMarkdown -and $line -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
        $scan = $line
        if ($isMarkdown) {
            if ($inFence) { continue }
            $scan = [regex]::Replace($line, '(`+)[^`]*?\1', '')   # inline code spans
        } elseif (-not $isPublicDoc -and $isExample) { continue }
        foreach ($m in [regex]::Matches($scan, '(?i)<your[-_ ][^>]{0,60}>')) {
            $problems.Add("${Path}:$($i + 1): placeholder '$($m.Value)' outside an example")
        }
    }
    return , $problems
}

function Find-LocalPath {
    # Paths of a developer's machine: a Windows user profile folder (except Public, Default and
    # written placeholders), a source checkout in a Repos folder at a drive root, and Unix home folders.
    param([string]$Path, [string]$Text)
    $problems = [System.Collections.Generic.List[string]]::new()
    $sep = '[\\/]{1,2}'
    $patterns = @(
        "(?i)\b[a-z]:$sep(?:Users|Documents and Settings)$sep(?!(?:Public|Default|All Users)\b)(?![<%`$.{])[^\s\\/""'<>|*?]+",
        "(?i)\b[a-z]:$sep(?:Repos|source${sep}repos)$sep[^\s""'<>|*?]+",
        '(?<![\w.~:])/home/(?![<$.{])[a-z_][a-z0-9_-]*/',
        '(?<![\w.~:])/Users/(?!(?:Shared|<|\$|\.|\{))[A-Za-z][A-Za-z0-9._-]*/'
    )
    $lines = $Text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($p in $patterns) {
            foreach ($m in [regex]::Matches($lines[$i], $p)) { $problems.Add("${Path}:$($i + 1): $($m.Value)") }
        }
    }
    return , $problems
}

function Find-SecretLike {
    # A safety net beside gitleaks, for the shapes that are never legitimate in this repository:
    # private keys, cloud and GitHub tokens, connection strings with a password or account key.
    param([string]$Path, [string]$Text)
    $problems = [System.Collections.Generic.List[string]]::new()
    $patterns = [ordered]@{
        'private key'             = '-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED |PGP )?PRIVATE KEY(?: BLOCK)?-----'
        'AWS access key'          = '\b(?:AKIA|ASIA)[0-9A-Z]{16}\b'
        'GitHub token'            = '\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{60,})\b'
        'Slack token'             = '\bxox[abprs]-[A-Za-z0-9-]{10,}'
        'Azure account key'       = '(?i)\bAccountKey=[A-Za-z0-9+/]{40,}={0,2}'
        'NuGet API key'           = '\boy2[a-z0-9]{43}\b'
        'password in a connection string' = '(?i)\b(?:password|pwd)\s*=\s*(?![<$%{\[(.]|\*{3}|\.\.\.)[^;''"\s]{4,}'
    }
    $lines = $Text -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($name in $patterns.Keys) {
            if ([regex]::IsMatch($lines[$i], $patterns[$name])) { $problems.Add("${Path}:$($i + 1): looks like a $name") }
        }
    }
    return , $problems
}

function Get-VulnerablePackage {
    # The JSON of `dotnet list package --vulnerable --include-transitive --format json`, read as the
    # CI job reads it. Returns the vulnerable entries and the errors reported by the command.
    param([string]$Json)
    $report = $Json | ConvertFrom-Json
    $found = [System.Collections.Generic.List[string]]::new()
    foreach ($project in @($report.projects)) {
        if (-not ($project.PSObject.Properties.Name -contains 'frameworks')) { continue }
        foreach ($framework in $project.frameworks) {
            foreach ($set in 'topLevelPackages', 'transitivePackages') {
                if (-not ($framework.PSObject.Properties.Name -contains $set)) { continue }
                foreach ($package in $framework.$set) {
                    if (-not ($package.PSObject.Properties.Name -contains 'vulnerabilities')) { continue }
                    foreach ($v in @($package.vulnerabilities)) {
                        if ($v) { $found.Add("$($package.id) $($package.resolvedVersion) [$($v.severity)] $($v.advisoryurl) in $([System.IO.Path]::GetFileName($project.path)) ($($framework.framework))") }
                    }
                }
            }
        }
    }
    $errors = @(if ($report.PSObject.Properties.Name -contains 'problems') { $report.problems | Where-Object level -eq 'error' | ForEach-Object text })
    [pscustomobject]@{ Found = @($found | Sort-Object -Unique); Errors = $errors; Projects = @($report.projects).Count }
}

function Find-RepositoryUrlProblem {
    # $Files: path -> text of README.md, CHANGELOG.md, Directory.Build.props, the community files,
    # the current docs and .github/**. The URL must be RepositoryUrl and PackageProjectUrl of
    # Directory.Build.props, appear in README.md, and be the base of every link of the issue forms;
    # and no file may link to another repository of the same owner (or of the owner named by
    # Directory.Build.props), which is what a renamed repository leaves behind.
    param([System.Collections.IDictionary]$Files, [string]$Url)
    $problems = [System.Collections.Generic.List[string]]::new()
    $Url = $Url.TrimEnd('/') -replace '\.git$', ''
    if ($Url -notmatch '^https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+)$') { $problems.Add("'$Url' is not https://github.com/<owner>/<name>"); return , $problems }
    $owners = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$owners.Add($Matches[1])
    $props = $Files['Directory.Build.props']
    if ($props) {
        foreach ($name in 'RepositoryUrl', 'PackageProjectUrl') {
            $m = [regex]::Match($props, "<$name>([^<]*)</$name>")
            if (-not $m.Success) { $problems.Add("Directory.Build.props has no $name") }
            else {
                if ($m.Groups[1].Value.TrimEnd('/') -cne $Url) { $problems.Add("Directory.Build.props: $name is '$($m.Groups[1].Value)', not '$Url'") }
                if ($m.Groups[1].Value -match '^https://github\.com/([A-Za-z0-9-]+)/') { [void]$owners.Add($Matches[1]) }
            }
        }
    } else { $problems.Add('Directory.Build.props not given') }
    if (-not $Files['README.md'] -or -not $Files['README.md'].Contains($Url)) { $problems.Add("README.md does not mention $Url") }
    $config = $Files['.github/ISSUE_TEMPLATE/config.yml']
    if ($config) {
        foreach ($m in [regex]::Matches($config, '(?m)^\s*url:\s*(\S+)')) {
            if (-not $m.Groups[1].Value.StartsWith("$Url/")) { $problems.Add(".github/ISSUE_TEMPLATE/config.yml: contact link $($m.Groups[1].Value) is not under $Url") }
        }
    } else { $problems.Add('.github/ISSUE_TEMPLATE/config.yml not given') }
    foreach ($path in $Files.Keys) {
        foreach ($m in [regex]::Matches($Files[$path], 'https://github\.com/([A-Za-z0-9-]+)/([A-Za-z0-9._-]+?)(?:\.git)?(?=[/)\s"''<>#?`,;:]|\.(?:\s|$)|$)')) {
            if (-not $owners.Contains($m.Groups[1].Value)) { continue }
            $found = "https://github.com/$($m.Groups[1].Value)/$($m.Groups[2].Value)"
            if ($found -cne $Url) { $problems.Add("${path}: links to $found, the repository is $Url") }
        }
    }
    return , @($problems | Sort-Object -Unique)
}

function Test-GitleaksRequired {
    param([string] $ReleaseTag)
    return -not [string]::IsNullOrWhiteSpace($ReleaseTag)
}

function Resolve-EffectiveReleaseTag {
    param(
        [string]$ExplicitReleaseTag,
        [string]$GitHubActions,
        [string]$GitHubRefType,
        [string]$GitHubRefName
    )
    if (-not [string]::IsNullOrWhiteSpace($ExplicitReleaseTag)) { return $ExplicitReleaseTag }
    if ($GitHubActions -ceq 'true' -and $GitHubRefType -ceq 'tag' -and
        -not [string]::IsNullOrWhiteSpace($GitHubRefName)) {
        return $GitHubRefName
    }
    return $null
}

function Get-StreamSha256Hex {
    param([System.IO.Stream]$Stream)
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant()
}

function Get-YamlLiteralStepScript {
    param(
        [string]$Yaml,
        [string]$StepName
    )
    $lines = @($Yaml -split "\r?\n")
    $stepIndexes = [System.Collections.Generic.List[int]]::new()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $match = [regex]::Match($lines[$i], '^\s*-\s*name\s*:\s*(?:''([^'']+)''|"([^\"]+)"|([^#]+?))\s*(?:#.*)?$')
        if (-not $match.Success) { continue }
        $name = @($match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value) |
            Where-Object { $_ } | Select-Object -First 1
        if ($name.Trim() -ceq $StepName) { $stepIndexes.Add($i) }
    }
    if ($stepIndexes.Count -ne 1) {
        return [pscustomobject]@{ FoundCount = $stepIndexes.Count; Script = $null; RunCount = 0; ShellCount = 0; ShellIsPwsh = $false; UnexpectedDirectKeyCount = 0; IsFirstStep = $false }
    }

    $stepIndex = $stepIndexes[0]
    $stepIndent = $lines[$stepIndex].Length - $lines[$stepIndex].TrimStart().Length
    $stepsIndex = -1
    for ($i = $stepIndex - 1; $i -ge 0; $i--) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
        if ($indent -eq ($stepIndent - 2) -and $lines[$i] -match '^\s*steps\s*:\s*$') {
            $stepsIndex = $i
            break
        }
        if ($indent -lt ($stepIndent - 2)) { break }
    }
    $isFirstStep = $stepsIndex -ge 0
    if ($isFirstStep) {
        for ($i = $stepsIndex + 1; $i -lt $stepIndex; $i++) {
            if ([string]::IsNullOrWhiteSpace($lines[$i]) -or $lines[$i] -match '^\s*#') { continue }
            $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
            if ($indent -eq $stepIndent -and $lines[$i] -match '^\s*-') {
                $isFirstStep = $false
                break
            }
        }
    }
    $stepEnd = $lines.Count
    for ($i = $stepIndex + 1; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
        if ($indent -le $stepIndent) { $stepEnd = $i; break }
    }

    $runIndex = -1
    $runIndent = -1
    $runCount = 0
    $shellCount = 0
    $shellIsPwsh = $false
    $unexpectedDirectKeyCount = 0
    for ($i = $stepIndex + 1; $i -lt $stepEnd; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i])) { continue }
        $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
        if ($indent -ne ($stepIndent + 2)) { continue }
        if ($lines[$i] -match '^\s*#') { continue }
        $isKnownDirectKey = $lines[$i] -match '^\s*(?:shell|''shell''|"shell"|run|''run''|"run")\s*:'
        if (-not $isKnownDirectKey) { $unexpectedDirectKeyCount++ }
        if ($lines[$i] -match '^\s*(?:shell|''shell''|"shell")\s*:') {
            $shellCount++
            $shell = [regex]::Match($lines[$i], '^\s*shell\s*:\s*(.*?)\s*$')
            if ($shell.Success -and $shell.Groups[1].Value -ceq 'pwsh -NoProfile -Command ". ''{0}''"') {
                $shellIsPwsh = $true
            }
        }
        if ($lines[$i] -match '^\s*(?:run|''run''|"run")\s*:') {
            $runCount++
            if ($lines[$i] -match '^\s*run\s*:\s*\|[-+]?\s*(?:#.*)?$') {
                $runIndex = $i
                $runIndent = $indent
            }
        }
    }
    if ($runIndex -lt 0) {
        return [pscustomobject]@{ FoundCount = 1; Script = $null; RunCount = $runCount; ShellCount = $shellCount; ShellIsPwsh = $shellIsPwsh; UnexpectedDirectKeyCount = $unexpectedDirectKeyCount; IsFirstStep = $isFirstStep }
    }

    $bodyLines = [System.Collections.Generic.List[string]]::new()
    $contentIndent = -1
    for ($i = $runIndex + 1; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ([string]::IsNullOrWhiteSpace($line)) {
            $bodyLines.Add('')
            continue
        }
        $indent = $line.Length - $line.TrimStart().Length
        if ($indent -le $runIndent) { break }
        if ($contentIndent -lt 0) { $contentIndent = $indent }
        if ($indent -lt $contentIndent) { return [pscustomobject]@{ FoundCount = 1; Script = $null; RunCount = $runCount; ShellCount = $shellCount; ShellIsPwsh = $shellIsPwsh; UnexpectedDirectKeyCount = $unexpectedDirectKeyCount; IsFirstStep = $isFirstStep } }
        $bodyLines.Add($line.Substring($contentIndent))
    }
    if ($contentIndent -lt 0) {
        return [pscustomobject]@{ FoundCount = 1; Script = $null; RunCount = $runCount; ShellCount = $shellCount; ShellIsPwsh = $shellIsPwsh; UnexpectedDirectKeyCount = $unexpectedDirectKeyCount; IsFirstStep = $isFirstStep }
    }
    return [pscustomobject]@{ FoundCount = 1; Script = ($bodyLines -join "`n"); RunCount = $runCount; ShellCount = $shellCount; ShellIsPwsh = $shellIsPwsh; UnexpectedDirectKeyCount = $unexpectedDirectKeyCount; IsFirstStep = $isFirstStep }
}

function Get-ApprovedGitleaksInstallScript {
    return (@'
$version = '8.29.1'
$asset = "gitleaks_${version}_windows_x64.zip"
$gitleaksArchiveSha256 = 'e4b7d556f0cddbe23d10d8fac2ab0f29f68f019091c6599ffbeaa8a4fb71ac78'
$base = "https://github.com/gitleaks/gitleaks/releases/download/v$version"
$tool = Join-Path $env:RUNNER_TEMP 'gitleaks'
New-Item -ItemType Directory -Force -Path $tool | Out-Null
Invoke-WebRequest -Uri "$base/$asset" -OutFile (Join-Path $tool $asset)
$actual = (Get-FileHash -Algorithm SHA256 (Join-Path $tool $asset)).Hash.ToLowerInvariant()
if ($actual -cne $gitleaksArchiveSha256) { throw "Wrong checksum for $asset" }
Expand-Archive -LiteralPath (Join-Path $tool $asset) -DestinationPath $tool
"GITLEAKS_PATH=$(Join-Path $tool 'gitleaks.exe')" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
'@ -replace "\r\n?", "`n").TrimEnd()
}

function Get-YamlReleaseJob {
    param([string]$Yaml)
    $lines = @($Yaml -split "\r?\n")
    $jobsIndexes = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^jobs\s*:\s*$') { $jobsIndexes += $i }
    }
    if ($jobsIndexes.Count -ne 1) {
        return [pscustomobject]@{ FoundCount = 0; Text = $null; RunsOnCount = 0; RunsOnIsWindowsLatest = $false; UnexpectedDirectKeyCount = 0 }
    }
    $jobsIndex = $jobsIndexes[0]
    $releaseIndexes = @()
    for ($i = $jobsIndex + 1; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i]) -or $lines[$i] -match '^\s*#') { continue }
        $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
        if ($indent -eq 0) { break }
        if ($indent -eq 2 -and $lines[$i] -match '^\s{2}release\s*:\s*$') { $releaseIndexes += $i }
    }
    if ($releaseIndexes.Count -ne 1) {
        return [pscustomobject]@{ FoundCount = $releaseIndexes.Count; Text = $null; RunsOnCount = 0; RunsOnIsWindowsLatest = $false; UnexpectedDirectKeyCount = 0 }
    }

    $jobIndex = $releaseIndexes[0]
    $jobEnd = $lines.Count
    for ($i = $jobIndex + 1; $i -lt $lines.Count; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i]) -or $lines[$i] -match '^\s*#') { continue }
        $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
        if ($indent -le 2) { $jobEnd = $i; break }
    }
    $runsOnCount = 0
    $runsOnIsWindowsLatest = $false
    $unexpectedDirectKeyCount = 0
    for ($i = $jobIndex + 1; $i -lt $jobEnd; $i++) {
        if ([string]::IsNullOrWhiteSpace($lines[$i]) -or $lines[$i] -match '^\s*#') { continue }
        $indent = $lines[$i].Length - $lines[$i].TrimStart().Length
        if ($indent -ne 4) { continue }
        if ($lines[$i] -match '^\s{4}runs-on\s*:') {
            $runsOnCount++
            if ($lines[$i] -match '^\s{4}runs-on\s*:\s*windows-latest\s*$') { $runsOnIsWindowsLatest = $true }
            continue
        }
        if ($lines[$i] -match '^\s{4}(?:name|timeout-minutes|steps)\s*:') { continue }
        $unexpectedDirectKeyCount++
    }
    return [pscustomobject]@{
        FoundCount = 1
        Text = ($lines[$jobIndex..($jobEnd - 1)] -join "`n")
        RunsOnCount = $runsOnCount
        RunsOnIsWindowsLatest = $runsOnIsWindowsLatest
        UnexpectedDirectKeyCount = $unexpectedDirectKeyCount
    }
}

function Find-WorkflowSupplyChainProblem {
    param([System.Collections.IDictionary]$Files)
    $problems = [System.Collections.Generic.List[string]]::new()
    $knownActionComments = @{
        'actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1' = 'v7.0.1'
        'actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68' = 'v6.0.0'
        'actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9' = 'v7.0.2'
    }
    $usesPattern = '(?m)^\s*(?:-\s*)?(?:uses|''uses''|"uses")\s*:\s*(?:''([^'']+)''|"([^"]+)"|([^\s#]+))(?:\s*#\s*(.*?))?\s*$'
    foreach ($path in $Files.Keys) {
        if ($path -notmatch '^\.github/workflows/[^/]+\.ya?ml$') { continue }
        if ($Files[$path] -match '(?m)(?:\{|,)\s*(?:uses|''uses''|"uses")\s*:') {
            $problems.Add("${path}: flow-style uses declarations are not permitted; use one checked block-style line")
        }
        foreach ($match in [regex]::Matches($Files[$path], $usesPattern)) {
            $action = @($match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value) |
                Where-Object { $_ } | Select-Object -First 1
            if ($action.StartsWith('./')) { continue }
            if ($action -notmatch '^([^@]+)@([0-9a-fA-F]{40})$') {
                $problems.Add("${path}: action '$action' is not pinned to an immutable 40-character commit SHA")
                continue
            }
            $comment = $match.Groups[4].Value.Trim()
            if ($comment -notmatch '^v\d+(?:\.\d+)*(?:[-.][0-9A-Za-z]+)*$') {
                $problems.Add("${path}: action '$action' needs one release version comment such as '# v7.0.1'")
                continue
            }
            if ($knownActionComments.ContainsKey($action) -and $comment -cne $knownActionComments[$action]) {
                $problems.Add("${path}: action '$action' must use version comment '# $($knownActionComments[$action])', not '# $comment'")
            }
        }
    }

    $releasePath = '.github/workflows/release.yml'
    $release = $Files[$releasePath]
    if (-not $release) {
        $problems.Add("$releasePath is missing")
        return , $problems
    }
    $releaseJob = Get-YamlReleaseJob $release
    if ($releaseJob.FoundCount -ne 1) {
        $problems.Add("${releasePath}: exactly one plain 'release' job under the top-level jobs mapping is required")
    }
    elseif ($releaseJob.RunsOnCount -ne 1 -or -not $releaseJob.RunsOnIsWindowsLatest) {
        $problems.Add("${releasePath}: the release job must declare exactly one unquoted 'runs-on: windows-latest'; expressions and self-hosted runners are not trusted")
    }
    if ($releaseJob.UnexpectedDirectKeyCount -gt 0) {
        $problems.Add("${releasePath}: the release job may contain only name, runs-on, timeout-minutes and steps; quoted, escaped or extra execution context is not permitted")
    }
    $releaseCheckName = [regex]::Matches($releaseJob.Text, '(?m)^\s{6}- name:\s*Release checks, package and smoke test\s*$')
    $releaseCheckCommand = './tools/Test-PublicRelease.ps1 -ReleaseTag $env:GITHUB_REF_NAME -RepositoryUrl "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY" -ReleaseArtifactsDirectory release/dist'
    $releaseCheckShell = 'pwsh -NoProfile -Command ". ''{0}''"'
    $releaseCheckBlock = [regex]::Matches(
        $releaseJob.Text,
        '(?m)^\s{6}- name:\s*Release checks, package and smoke test\s*\r?\n' +
        '\s{8}shell:\s*' + [regex]::Escape($releaseCheckShell) + '\s*\r?\n' +
        '\s{8}run:\s*' + [regex]::Escape($releaseCheckCommand) + '\s*(?=\r?\n\s{6}-|\z)')
    if ($releaseCheckName.Count -ne 1 -or $releaseCheckBlock.Count -ne 1) {
        $problems.Add("${releasePath}: the Release checks step must contain only the approved -NoProfile shell and Test-PublicRelease.ps1 command with -ReleaseTag `$env:GITHUB_REF_NAME")
    }
    $expectedVersion = '8.29.1'
    $expectedDigest = 'e4b7d556f0cddbe23d10d8fac2ab0f29f68f019091c6599ffbeaa8a4fb71ac78'
    $installStep = Get-YamlLiteralStepScript $releaseJob.Text 'Install pinned gitleaks'
    if ($installStep.FoundCount -ne 1 -or $installStep.RunCount -ne 1 -or [string]::IsNullOrWhiteSpace($installStep.Script)) {
        $problems.Add("${releasePath}: exactly one 'Install pinned gitleaks' step with a literal run block is required")
    }
    else {
        if ($installStep.ShellCount -ne 1 -or -not $installStep.ShellIsPwsh) {
            $problems.Add("${releasePath}: the Gitleaks install step must declare exactly 'shell: pwsh -NoProfile -Command `". ''{0}''`"' so profiles and shell wrappers cannot alter checksum verification")
        }
        if (-not $installStep.IsFirstStep) {
            $problems.Add("${releasePath}: the Gitleaks install step must be the first step so earlier steps cannot replace pwsh or poison its execution context")
        }
        if ($installStep.UnexpectedDirectKeyCount -gt 0) {
            $problems.Add("${releasePath}: the Gitleaks install step may contain only name, shell and run; extra execution context is not permitted")
        }
        $installScript = ($installStep.Script -replace "\r\n?", "`n").TrimEnd()
        if ($installScript -cne (Get-ApprovedGitleaksInstallScript)) {
            $problems.Add("${releasePath}: the Gitleaks step must match the approved install script exactly; extra, missing or changed instructions are not permitted")
        }
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseInput(
            $installScript,
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            $problems.Add("${releasePath}: the 'Install pinned gitleaks' PowerShell block is not syntactically valid")
        }
        else {
            # These checks must run unconditionally. Nested statements can be hidden behind a false
            # branch, so only direct statements of the script's main block qualify.
            $topLevelStatements = @($ast.EndBlock.Statements)
            $trapStatements = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.TrapStatementAst]
            }, $true))
            if ($trapStatements.Count -gt 0) {
                $problems.Add("${releasePath}: trap statements are not permitted in the Gitleaks install step because they can neutralize checksum failures")
            }
            $functionDefinitions = @($ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst]
            }, $true))
            if ($functionDefinitions.Count -gt 0) {
                $problems.Add("${releasePath}: local function definitions are not permitted in the Gitleaks install step because they can override checksum commands")
            }
            $assignments = @($topLevelStatements | Where-Object {
                $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $_.Left -is [System.Management.Automation.Language.VariableExpressionAst]
            })
            $versionAssignments = @($assignments | Where-Object {
                ($_.Left.VariablePath.UserPath -replace '^.*:', '') -ieq 'version'
            })
            $digestAssignments = @($assignments | Where-Object {
                ($_.Left.VariablePath.UserPath -replace '^.*:', '') -ieq 'gitleaksArchiveSha256'
            })
            $actualAssignments = @($assignments | Where-Object {
                ($_.Left.VariablePath.UserPath -replace '^.*:', '') -ieq 'actual'
            })

            if ($versionAssignments.Count -ne 1 -or $versionAssignments[0].Right.Extent.Text -cne "'$expectedVersion'") {
                $problems.Add("${releasePath}: Gitleaks version must be assigned exactly once to '$expectedVersion' in executable code of the install step")
            }
            if ($digestAssignments.Count -ne 1) {
                $problems.Add("${releasePath}: gitleaksArchiveSha256 must be assigned exactly once")
            }
            else {
                $digestValue = [regex]::Match($digestAssignments[0].Right.Extent.Text, "^'([0-9a-f]{64})'$")
                if (-not $digestValue.Success) {
                    $problems.Add("${releasePath}: gitleaksArchiveSha256 is missing or is not a lower-case SHA-256 literal in executable code of the install step")
                }
                elseif ($digestValue.Groups[1].Value -cne $expectedDigest) {
                    $problems.Add("${releasePath}: gitleaksArchiveSha256 is not the trusted digest for Gitleaks $expectedVersion Windows x64 in the install step")
                }
            }
            if ($actualAssignments.Count -ne 1) {
                $problems.Add("${releasePath}: the computed Gitleaks hash variable actual must be assigned exactly once")
            }
            $mutationCommands = @($ast.FindAll({
                param($node)
                if ($node -isnot [System.Management.Automation.Language.CommandAst]) { return $false }
                $name = $node.GetCommandName()
                return $name -and $name.ToLowerInvariant() -in @(
                    'set-variable', 'new-variable', 'remove-variable', 'clear-variable', 'sv', 'nv', 'rv', 'clv',
                    'set-alias', 'new-alias', 'remove-alias', 'sal', 'nal', 'ral')
            }, $true))
            if ($mutationCommands.Count -gt 0) {
                $problems.Add("${releasePath}: gitleaksArchiveSha256 and actual must be assigned exactly once; variable mutation and command override commands are not permitted in the install step")
            }

            $hashAssignment = if ($actualAssignments.Count -eq 1 -and
                $actualAssignments[0].Right.Extent.Text -match '^\(Get-FileHash\s+-Algorithm\s+SHA256\s+\(Join-Path\s+\$tool\s+\$asset\)\)\.Hash\.ToLowerInvariant\(\)$') {
                $actualAssignments[0]
            }
            $compareStatements = @($topLevelStatements | Where-Object {
                if ($_ -isnot [System.Management.Automation.Language.IfStatementAst] -or $_.Clauses.Count -ne 1) { return $false }
                $condition = $_.Clauses[0].Item1.Extent.Text
                $statements = @($_.Clauses[0].Item2.Statements)
                return $condition -match '^\$actual\s+-cne\s+\$gitleaksArchiveSha256$' -and
                    $statements.Count -gt 0 -and
                    $statements[0] -is [System.Management.Automation.Language.ThrowStatementAst]
            })
            $expandStatements = @($topLevelStatements | Where-Object {
                if ($_ -isnot [System.Management.Automation.Language.PipelineAst] -or $_.PipelineElements.Count -ne 1) { return $false }
                $command = $_.PipelineElements[0]
                return $command -is [System.Management.Automation.Language.CommandAst] -and
                    $command.GetCommandName() -ieq 'Expand-Archive' -and
                    $command.Extent.Text -match '^Expand-Archive\s+-LiteralPath\s+\(Join-Path\s+\$tool\s+\$asset\)\s+-DestinationPath\s+\$tool$'
            })
            if (-not $hashAssignment -or $compareStatements.Count -ne 1 -or $expandStatements.Count -ne 1) {
                $problems.Add("${releasePath}: Gitleaks must hash and compare the downloaded archive with gitleaksArchiveSha256 before extraction")
            }
            elseif ($hashAssignment.Extent.StartOffset -gt $compareStatements[0].Extent.StartOffset -or
                $compareStatements[0].Extent.StartOffset -gt $expandStatements[0].Extent.StartOffset) {
                $problems.Add("${releasePath}: Gitleaks must verify gitleaksArchiveSha256 before Expand-Archive")
            }
            else {
                $hashIndex = [array]::IndexOf($topLevelStatements, $hashAssignment)
                $compareIndex = [array]::IndexOf($topLevelStatements, $compareStatements[0])
                $expandIndex = [array]::IndexOf($topLevelStatements, $expandStatements[0])
                if ($compareIndex -ne ($hashIndex + 1) -or $expandIndex -ne ($compareIndex + 1)) {
                    $problems.Add("${releasePath}: hash, checksum comparison and Expand-Archive must run consecutively, with verification immediately before extraction")
                }
            }

            $checksumLiterals = @($ast.FindAll({
                param($node)
                ($node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                    $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) -and
                    $node.Value -match '(?i)checksums\.txt'
            }, $true))
            if ($checksumLiterals.Count -gt 0) {
                $problems.Add("${releasePath}: Gitleaks must not download its checksum from the same release as the binary")
            }
        }
    }
    return , @($problems | Sort-Object -Unique)
}

# =================================================================================================
# Self-tests: fixtures in memory. Strings that would trip the scans of this very file (local paths,
# key shapes) are assembled from pieces.
# =================================================================================================
function Invoke-SelfTest {
    $failures = [System.Collections.Generic.List[string]]::new()
    function Assert([string]$Name, $Condition) {
        $script:selfTestCount++
        if (-not [bool]$Condition) { $failures.Add($Name); Write-Host "  FAIL  $Name" -ForegroundColor Red } else { Write-Host "  ok    $Name" }
    }
    $script:selfTestCount = 0

    # SDK
    Assert 'sdk: exact version passes' ((Test-SdkVersion '10.0.402' '10.0.402').Count -eq 0)
    Assert 'sdk: later patch of the band passes' ((Test-SdkVersion '10.0.402' '10.0.405').Count -eq 0)
    Assert 'sdk: earlier patch fails' ((Test-SdkVersion '10.0.402' '10.0.401').Count -eq 1)
    Assert 'sdk: other feature band fails' ((Test-SdkVersion '10.0.402' '10.0.500').Count -eq 1)
    Assert 'sdk: other major fails' ((Test-SdkVersion '10.0.402' '11.0.100').Count -eq 1)
    Assert 'sdk: prerelease fails' ((Test-SdkVersion '10.0.402' '10.0.403-rc.1').Count -ge 1)
    Assert 'sdk: unreadable version fails' ((Test-SdkVersion '10.0.402' 'garbage').Count -eq 1)

    # CHANGELOG
    $url = 'https://github.com/Example-Org/WCS'
    $cl = @('# Changelog', '', '## [Unreleased]', '', 'Nothing yet.', '', '## [0.1.0-preview.1] - not yet released', '', '### Added', '', '- Things.', '',
        '[Unreleased]: https://github.com/Example-Org/WCS/compare/v0.1.0-preview.1...HEAD', '[0.1.0-preview.1]: https://github.com/Example-Org/WCS/releases/tag/v0.1.0-preview.1')
    Assert 'changelog: undated section passes without a tag' ((Test-ChangelogVersion $cl '0.1.0-preview.1' $url).Count -eq 0)
    $p = Test-ChangelogVersion $cl '0.1.0-preview.1' $url -ForRelease
    Assert 'changelog: undated section fails for a release' ($p.Count -eq 1 -and $p[0] -like '*not dated*')
    $dated = $cl.Clone(); $dated[6] = '## [0.1.0-preview.1] - 2026-10-20'
    Assert 'changelog: dated section passes for a release' ((Test-ChangelogVersion $dated '0.1.0-preview.1' $url -ForRelease -Today ([datetime]'2026-10-20')).Count -eq 0)
    Assert 'changelog: date in the future fails' ((Test-ChangelogVersion $dated '0.1.0-preview.1' $url -ForRelease -Today ([datetime]'2026-10-01')).Count -eq 1)
    $stale = $dated.Clone(); $stale[7] = 'The date is written here when the version is tagged.'
    Assert 'changelog: the note of the undated state fails for a release' ((Test-ChangelogVersion $stale '0.1.0-preview.1' $url -ForRelease -Today ([datetime]'2026-10-20')) -match 'remove it')
    $bad = $cl.Clone(); $bad[6] = '## [0.1.0-preview.1] - 2026-13-45'
    Assert 'changelog: impossible date fails' ((Test-ChangelogVersion $bad '0.1.0-preview.1' $url -ForRelease).Count -ge 1)
    $empty = @('## [Unreleased]', '', '## [0.2.0] - 2026-10-20', '', '[0.2.0]: https://github.com/Example-Org/WCS/releases/tag/v0.2.0')
    Assert 'changelog: empty section fails for a release' ((Test-ChangelogVersion $empty '0.2.0' $url -ForRelease -Today ([datetime]'2026-10-20')) -match 'empty')
    Assert 'changelog: missing section fails' ((Test-ChangelogVersion $cl '0.2.0' $url).Count -eq 1)
    Assert 'changelog: missing link definition fails' ((Test-ChangelogVersion $cl[0..11] '0.1.0-preview.1' $url).Count -eq 1)
    $older = @('## [Unreleased]', '', '## [0.2.0] - 2026-11-01', '', 'x', '', '## [0.1.0] - 2026-10-01', '', 'y')
    Assert 'changelog: a version that is not the latest fails' ((Test-ChangelogVersion $older '0.1.0' $null) -match 'latest')
    $s = Get-ChangelogSection $dated '0.1.0-preview.1'
    Assert 'changelog: section body stops at the link definitions' ($s.Body -eq "### Added`n`n- Things.")

    # Tag
    Assert 'tag: matching tag passes' ((Test-ReleaseTagName 'v0.1.0-preview.1' '0.1.0-preview.1').Count -eq 0)
    Assert 'tag: other version fails' ((Test-ReleaseTagName 'v0.1.0-preview.2' '0.1.0-preview.1').Count -eq 1)
    Assert 'tag: missing v fails' ((Test-ReleaseTagName '0.1.0-preview.1' '0.1.0-preview.1').Count -eq 2)
    Assert 'tag: other suffix fails' ((Test-ReleaseTagName 'v0.1.0-rc.1' '0.1.0-rc.1').Count -eq 1)
    Assert 'tag: case matters' ((Test-ReleaseTagName 'V0.1.0' '0.1.0').Count -ge 1)

    # Versions in documents
    Assert 'version: same preview passes' ((Find-VersionMismatch 'a.md' 'the first version is `0.1.0-preview.1`' '0.1.0-preview.1').Count -eq 0)
    Assert 'version: other preview fails' ((Find-VersionMismatch 'a.md' 'git tag v0.1.0-preview.2' '0.1.0-preview.1').Count -eq 1)
    Assert 'version: status output with the right assembly version passes' ((Find-VersionMismatch 'a.md' '{"navStatus":0,"version":"0.1.0.0"}' '0.1.0-preview.1').Count -eq 0)
    Assert 'version: stale status output fails' ((Find-VersionMismatch 'a.md' '{"navStatus":0,"version":"0.0.9.0"}' '0.1.0-preview.1').Count -eq 1)
    Assert 'version: log line with the assembly version is read' ((Find-VersionMismatch 'a.md' 'Engine Run, version 0.2.0.0' '0.1.0-preview.1').Count -eq 1)
    Assert 'version: plain semantic versions are not judged' ((Find-VersionMismatch 'a.md' 'a patch (0.1.0 to 0.1.1), SDK 10.0.402' '0.1.0-preview.1').Count -eq 0)

    # Characters
    Assert 'chars: clean text passes' ((Find-AnomalousCharacter 'a.md' "Hello`r`nperché è così`tok`n").Count -eq 0)
    Assert 'chars: leading BOM passes' ((Find-AnomalousCharacter 'a.md' "$([char]0xFEFF)# Title").Count -eq 0)
    Assert 'chars: BOM in the middle fails' ((Find-AnomalousCharacter 'a.md' "a$([char]0xFEFF)b").Count -eq 1)
    Assert 'chars: control character fails' ((Find-AnomalousCharacter 'a.md' "a$([char]0x1B)[0mb").Count -eq 1)
    Assert 'chars: C1 control fails' ((Find-AnomalousCharacter 'a.md' "a$([char]0x92)b").Count -eq 1)
    Assert 'chars: replacement character fails' ((Find-AnomalousCharacter 'a.md' "caf$([char]0xFFFD)").Count -eq 1)
    Assert 'chars: double-encoded e grave fails' ((Find-AnomalousCharacter 'a.md' "perch$([char]0xC3)$([char]0xA8)").Count -eq 1)
    Assert 'chars: double-encoded quote fails' ((Find-AnomalousCharacter 'a.md' "$([char]0xE2)$([char]0x20AC)$([char]0x2122)s").Count -eq 1)
    Assert 'chars: bidirectional override fails' ((Find-AnomalousCharacter 'a.md' "a$([char]0x202E)b").Count -eq 1)
    Assert 'chars: zero-width space fails' ((Find-AnomalousCharacter 'a.md' "a$([char]0x200B)b").Count -eq 1)
    Assert 'chars: the line number is right' ((Find-AnomalousCharacter 'a.md' "one`ntwo`nth$([char]0x7)ree")[0] -like 'a.md:3:*')

    # Placeholders
    $todo = 'TO' + 'DO'
    Assert 'placeholder: marker in a document fails' ((Find-Placeholder 'docs/a.md' "Write this. $todo").Count -eq 1)
    Assert 'placeholder: marker inside code in a document still fails' ((Find-Placeholder 'docs/a.md' (('```text', $todo, '```') -join "`n")).Count -eq 1)
    Assert 'placeholder: lower-case todo in prose passes' ((Find-Placeholder 'docs/a.md' 'a to-do list, todo apps').Count -eq 0)
    Assert 'placeholder: marker in code files is not judged' ((Find-Placeholder 'src/a.cs' "// $todo later").Count -eq 0)
    Assert 'placeholder: marker in an issue form fails' ((Find-Placeholder '.github/ISSUE_TEMPLATE/x.yml' "label: $todo").Count -eq 1)
    $yourKey = '<your' + ' key>'; $yourDashKey = '<your' + '-key>'
    Assert 'placeholder: your-key in inline code passes' ((Find-Placeholder 'docs/a.md' "Send ``X-Api-Key: $yourKey``.").Count -eq 0)
    Assert 'placeholder: your-key in a fenced block passes' ((Find-Placeholder 'docs/a.md' (('```powershell', "`$h = @{ 'X-Api-Key' = '$yourKey' }", '```') -join "`n")).Count -eq 0)
    Assert 'placeholder: your-host in prose fails' ((Find-Placeholder 'docs/a.md' ('Open <your' + '-host>/editor.')).Count -eq 1)
    Assert 'placeholder: prose after a fence is scanned again' ((Find-Placeholder 'docs/a.md' ((('```text', 'x', '```', 'Then <your') -join "`n") + '_value> here.')).Count -eq 1)
    Assert 'placeholder: your-key in settings fails' ((Find-Placeholder 'src/appsettings.json' """ApiKey"": ""$yourDashKey""").Count -eq 1)
    Assert 'placeholder: your-key in an example file passes' ((Find-Placeholder 'src/appsettings.example.json' """ApiKey"": ""$yourDashKey""").Count -eq 0)
    Assert 'placeholder: your-key in a test fixture passes' ((Find-Placeholder 'x/tests/Some.Test/data.json' """ApiKey"": ""$yourDashKey""").Count -eq 0)

    # Local paths
    $users = 'C:' + '\Users\'
    Assert 'path: a user profile fails' ((Find-LocalPath 'a.md' "see ${users}jdoe\AppData\x").Count -eq 1)
    Assert 'path: a user profile with forward slashes fails' ((Find-LocalPath 'a.md' ("c:/" + "Users/jdoe/x")).Count -eq 1)
    Assert 'path: a checkout under Repos fails' ((Find-LocalPath 'a.md' ('D:' + '\Repos\Company\WCS')).Count -eq 1)
    Assert 'path: the public profile passes' ((Find-LocalPath 'a.md' "${users}Public\Documents").Count -eq 0)
    Assert 'path: a placeholder profile passes' ((Find-LocalPath 'a.md' "${users}<you>\x and ${users}%USERNAME%\x and ${users}...\x").Count -eq 0)
    Assert 'path: an installation folder passes' ((Find-LocalPath 'a.md' 'C:\Wcs\Wcs.Host.exe and C:\...\WCS\Data').Count -eq 0)
    Assert 'path: a Unix home fails' ((Find-LocalPath 'a.md' ('/ho' + 'me/jdoe/src')).Count -eq 1)
    Assert 'path: a macOS home fails' ((Find-LocalPath 'a.md' ('/Us' + 'ers/jdoe/src')).Count -eq 1)
    Assert 'path: a URL path is not a home' ((Find-LocalPath 'a.md' 'https://example.org/home/page/ and https://x.org/Users/list/').Count -eq 0)

    # Secrets
    Assert 'secret: private key header fails' ((Find-SecretLike 'a' ('-----BEGIN ' + 'RSA PRIVATE KEY-----')).Count -eq 1)
    Assert 'secret: AWS key fails' ((Find-SecretLike 'a' ('key AK' + 'IAABCDEFGHIJKLMNOP')).Count -eq 1)
    Assert 'secret: GitHub token fails' ((Find-SecretLike 'a' ('gh' + 'p_' + ('a1' * 18))).Count -eq 1)
    Assert 'secret: connection string with a password fails' ((Find-SecretLike 'a' ('Server=x;User Id=sa;Pass' + 'word=Sup3rS3cret;')).Count -eq 1)
    Assert 'secret: placeholder password passes' ((Find-SecretLike 'a' 'Password=<password>; Password=...; Password=$env:X; pwd=***').Count -eq 0)
    Assert 'secret: demo keys pass' ((Find-SecretLike 'a' 'X-Api-Key: my-wcs-key; sample-wms-key').Count -eq 0)
    Assert 'gitleaks: missing executable is mandatory for a tagged release' (Test-GitleaksRequired 'v0.1.0')
    Assert 'gitleaks: missing executable is optional before tagging' (-not (Test-GitleaksRequired ''))
    $abc = [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes('abc'))
    try { $abcDigest = Get-StreamSha256Hex $abc } finally { $abc.Dispose() }
    Assert 'gitleaks: executable digest uses SHA-256 bytes, not command resolution' `
        ($abcDigest -ceq 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
    Assert 'gitleaks: substituted executable digest does not match the trusted binary' `
        ($abcDigest -cne '3cc09feda0ed0f9fcb2c10f71a6186c956725d077068cb6c4a3d594c6461c2ed')
    Assert 'release tag: an explicit value wins' `
        ((Resolve-EffectiveReleaseTag 'v1.2.3' 'true' 'tag' 'v9.9.9') -ceq 'v1.2.3')
    Assert 'release tag: GitHub tag context cannot silently disable release enforcement' `
        ((Resolve-EffectiveReleaseTag '' 'true' 'tag' 'v1.2.3') -ceq 'v1.2.3')
    Assert 'release tag: a GitHub branch is not treated as a release' `
        ([string]::IsNullOrEmpty((Resolve-EffectiveReleaseTag '' 'true' 'branch' 'main')))

    # Workflow supply chain
    $checkoutSha = '3d3c42e5aac5ba805825da76410c181273ba90b1'
    $uploadSha = 'cf430e030ddbb5b0abf93d22962f4752f3646cd9'
    $gitleaksDigest = 'e4b7d556f0cddbe23d10d8fac2ab0f29f68f019091c6599ffbeaa8a4fb71ac78'
    $approvedGitleaksScript = ((Get-ApprovedGitleaksInstallScript) -split "`n" | ForEach-Object { "          $_" }) -join "`n"
    $secureWorkflows = [ordered]@{
        '.github/workflows/ci.yml' = "steps:`n  - uses: actions/checkout@$checkoutSha # v7.0.1`n  - uses: ./local-action"
        '.github/workflows/release.yml' = "jobs:`n  release:`n    name: Release`n    runs-on: windows-latest`n    timeout-minutes: 60`n    steps:`n      - name: Install pinned gitleaks`n        shell: pwsh -NoProfile -Command `". '{0}'`"`n        run: |`n$approvedGitleaksScript`n      - name: Release checks, package and smoke test`n        shell: pwsh -NoProfile -Command `". '{0}'`"`n        run: ./tools/Test-PublicRelease.ps1 -ReleaseTag `$env:GITHUB_REF_NAME -RepositoryUrl `"`$env:GITHUB_SERVER_URL/`$env:GITHUB_REPOSITORY`" -ReleaseArtifactsDirectory release/dist`n      - uses: actions/upload-artifact@$uploadSha # v7.0.2"
    }
    Assert 'workflow supply chain: immutable action SHAs and embedded digest pass' `
        ((Find-WorkflowSupplyChainProblem $secureWorkflows).Count -eq 0)
    $mutableAction = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $mutableAction[$k] = $secureWorkflows[$k] }
    $mutableAction['.github/workflows/ci.yml'] = 'steps:' + "`n  - uses: actions/checkout@v4"
    Assert 'workflow supply chain: mutable action tag fails' `
        ((Find-WorkflowSupplyChainProblem $mutableAction) -match 'immutable 40-character commit SHA')
    $mutableReusableWorkflow = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $mutableReusableWorkflow[$k] = $secureWorkflows[$k] }
    $mutableReusableWorkflow['.github/workflows/ci.yml'] = "jobs:`n  delegated:`n    uses: example/actions/.github/workflows/build.yml@main"
    Assert 'workflow supply chain: mutable reusable workflow fails' `
        ((Find-WorkflowSupplyChainProblem $mutableReusableWorkflow) -match 'immutable 40-character commit SHA')
    $spacedUses = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $spacedUses[$k] = $secureWorkflows[$k] }
    $spacedUses['.github/workflows/ci.yml'] = 'steps:' + "`n  - uses : actions/checkout@v4"
    Assert 'workflow supply chain: whitespace before uses colon cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $spacedUses) -match 'immutable 40-character commit SHA')
    $quotedUses = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $quotedUses[$k] = $secureWorkflows[$k] }
    $quotedUses['.github/workflows/ci.yml'] = 'jobs:' + "`n  delegated:`n    'uses': 'example/actions/.github/workflows/build.yml@main'"
    Assert 'workflow supply chain: quoted uses key cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $quotedUses) -match 'immutable 40-character commit SHA')
    $flowAction = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $flowAction[$k] = $secureWorkflows[$k] }
    $flowAction['.github/workflows/ci.yml'] = 'steps:' + "`n  - { uses: actions/checkout@v4 }"
    Assert 'workflow supply chain: flow-style action cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $flowAction) -match 'flow-style uses')
    $flowReusable = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $flowReusable[$k] = $secureWorkflows[$k] }
    $flowReusable['.github/workflows/ci.yml'] = "jobs:`n  delegated: { uses: 'example/actions/.github/workflows/build.yml@main' }"
    Assert 'workflow supply chain: flow-style reusable workflow cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $flowReusable) -match 'flow-style uses')
    $missingActionComment = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $missingActionComment[$k] = $secureWorkflows[$k] }
    $missingActionComment['.github/workflows/ci.yml'] = "steps:`n  - uses: actions/checkout@$checkoutSha"
    Assert 'workflow supply chain: action pin without version comment fails' `
        ((Find-WorkflowSupplyChainProblem $missingActionComment) -match 'version comment')
    $wrongActionComment = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $wrongActionComment[$k] = $secureWorkflows[$k] }
    $wrongActionComment['.github/workflows/ci.yml'] = "steps:`n  - uses: actions/checkout@$checkoutSha # v4.3.0"
    Assert 'workflow supply chain: wrong known action version comment fails' `
        ((Find-WorkflowSupplyChainProblem $wrongActionComment) -match 'must use version comment')
    $missingDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $missingDigest[$k] = $secureWorkflows[$k] }
    $missingDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "      `$gitleaksArchiveSha256 = '$gitleaksDigest'`n", '')
    Assert 'workflow supply chain: missing embedded gitleaks digest fails' `
        ((Find-WorkflowSupplyChainProblem $missingDigest) -match 'gitleaksArchiveSha256')
    $unusedDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $unusedDigest[$k] = $secureWorkflows[$k] }
    $unusedDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()`n", '').Replace(
        '          if ($actual -cne $gitleaksArchiveSha256) { throw "Wrong checksum for $asset" }' + "`n", '').Replace(
        "          Expand-Archive -LiteralPath (Join-Path `$tool `$asset) -DestinationPath `$tool`n", '')
    Assert 'workflow supply chain: an unused gitleaks digest fails' `
        ((Find-WorkflowSupplyChainProblem $unusedDigest) -match 'hash and compare')
    $reassignedDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $reassignedDigest[$k] = $secureWorkflows[$k] }
    $reassignedDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "`$actual =", "`$gitleaksArchiveSha256 = '$((('c' * 64) -join ''))'`n`$actual =")
    Assert 'workflow supply chain: gitleaks digest reassignment fails' `
        ((Find-WorkflowSupplyChainProblem $reassignedDigest) -match 'assigned exactly once')
    $reassignedActual = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $reassignedActual[$k] = $secureWorkflows[$k] }
    $reassignedActual['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        'if ($actual', "`$actual = '$gitleaksDigest'`nif (`$actual")
    Assert 'workflow supply chain: computed hash reassignment fails' `
        ((Find-WorkflowSupplyChainProblem $reassignedActual) -match 'assigned exactly once')
    $bracedDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $bracedDigest[$k] = $secureWorkflows[$k] }
    $bracedDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "`$actual =", "`${gitleaksArchiveSha256} = '$((('c' * 64) -join ''))'`n`$actual =")
    Assert 'workflow supply chain: braced digest reassignment cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $bracedDigest) -match 'assigned exactly once')
    $scopedDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $scopedDigest[$k] = $secureWorkflows[$k] }
    $scopedDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "`$actual =", "`$script:gitleaksArchiveSha256 = '$((('c' * 64) -join ''))'`n`$actual =")
    Assert 'workflow supply chain: scoped digest reassignment cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $scopedDigest) -match 'assigned exactly once')
    $setVariableDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $setVariableDigest[$k] = $secureWorkflows[$k] }
    $setVariableDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "`$actual =", "Set-Variable -Scope Script -Name gitleaksArchiveSha256 -Value '$((('c' * 64) -join ''))'`n`$actual =")
    Assert 'workflow supply chain: Set-Variable digest reassignment cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $setVariableDigest) -match 'assigned exactly once')
    $aliasActual = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $aliasActual[$k] = $secureWorkflows[$k] }
    $aliasActual['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        'if ($actual', "sv actual '$gitleaksDigest'`nif (`$actual")
    Assert 'workflow supply chain: sv actual reassignment cannot bypass the check' `
        ((Find-WorkflowSupplyChainProblem $aliasActual) -match 'assigned exactly once')
    $decoyDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $decoyDigest[$k] = $secureWorkflows[$k] }
    $decoyDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "          `$gitleaksArchiveSha256 = '$gitleaksDigest'", "          `$gitleaksArchiveSha256 = '$((('c' * 64) -join ''))'") +
        "`n      - name: Decoy digest`n        run: |`n          `$gitleaksArchiveSha256 = '$gitleaksDigest'"
    Assert 'workflow supply chain: trusted digest in a decoy step cannot validate the install step' `
        ((Find-WorkflowSupplyChainProblem $decoyDigest) -match 'trusted digest')
    $decoyVerification = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $decoyVerification[$k] = $secureWorkflows[$k] }
    $decoyVerification['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()", "          `$actual = 'unchecked'").Replace(
        '          if ($actual -cne $gitleaksArchiveSha256) { throw "Wrong checksum for $asset" }' + "`n", '').Replace(
        "          Expand-Archive -LiteralPath (Join-Path `$tool `$asset) -DestinationPath `$tool`n", '') +
        "`n      - name: Decoy verification`n        run: |`n          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()`n          if (`$actual -cne `$gitleaksArchiveSha256) { throw 'wrong' }`n          Expand-Archive -LiteralPath (Join-Path `$tool `$asset) -DestinationPath `$tool"
    Assert 'workflow supply chain: hash verification in a decoy step cannot validate the install step' `
        ((Find-WorkflowSupplyChainProblem $decoyVerification) -match 'hash and compare')
    $commentDecoy = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $commentDecoy[$k] = $secureWorkflows[$k] }
    $commentDecoy['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "          `$version =", "          <#`n          `$gitleaksArchiveSha256 = '$gitleaksDigest'`n          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()`n          if (`$actual -cne `$gitleaksArchiveSha256) { throw 'wrong' }`n          Expand-Archive -LiteralPath (Join-Path `$tool `$asset) -DestinationPath `$tool`n          #>`n          `$version =").Replace(
        "          `$gitleaksArchiveSha256 = '$gitleaksDigest'", "          `$gitleaksArchiveSha256 = '$((('c' * 64) -join ''))'").Replace(
        "          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()", "          `$actual = 'unchecked'")
    Assert 'workflow supply chain: trusted lines in a PowerShell comment cannot validate the install step' `
        ((Find-WorkflowSupplyChainProblem $commentDecoy) -match 'trusted digest|hash and compare')
    $nestedCompare = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $nestedCompare[$k] = $secureWorkflows[$k] }
    $nestedCompare['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        '      if ($actual -cne $gitleaksArchiveSha256) { throw "Wrong checksum for $asset" }',
        '      if ($false) { if ($actual -cne $gitleaksArchiveSha256) { throw "Wrong checksum for $asset" } }')
    Assert 'workflow supply chain: a comparison in an unreachable nested branch cannot validate the install step' `
        ((Find-WorkflowSupplyChainProblem $nestedCompare) -match 'hash and compare')
    $trappedThrow = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $trappedThrow[$k] = $secureWorkflows[$k] }
    $trappedThrow['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "      `$version =", "      trap { continue }`n      `$version =")
    Assert 'workflow supply chain: a trap cannot neutralize a checksum mismatch throw' `
        ((Find-WorkflowSupplyChainProblem $trappedThrow) -match 'trap')
    $shadowedHash = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $shadowedHash[$k] = $secureWorkflows[$k] }
    $shadowedHash['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "      `$version =", "      function Get-FileHash { 'fake' }`n      `$version =")
    Assert 'workflow supply chain: local functions cannot shadow checksum commands' `
        ((Find-WorkflowSupplyChainProblem $shadowedHash) -match 'override')
    $tamperedArchive = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $tamperedArchive[$k] = $secureWorkflows[$k] }
    $tamperedArchive['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "          Expand-Archive", "          Set-Content -LiteralPath (Join-Path `$tool `$asset) -Value 'tampered'`n          Expand-Archive")
    Assert 'workflow supply chain: archive cannot be changed between checksum and extraction' `
        ((Find-WorkflowSupplyChainProblem $tamperedArchive) -match 'immediately before')
    $providerMutation = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $providerMutation[$k] = $secureWorkflows[$k] }
    $providerMutation['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "      `$actual =", "      Set-Item -Path Variable:gitleaksArchiveSha256 -Value '$((('c' * 64) -join ''))'`n      `$actual =")
    Assert 'workflow supply chain: provider-based variable mutation cannot bypass the trusted digest' `
        ((Find-WorkflowSupplyChainProblem $providerMutation) -match 'approved install script')
    $shellWrapper = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $shellWrapper[$k] = $secureWorkflows[$k] }
    $shellWrapper['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "        run: |", "        shell: pwsh -NoProfile -File tools/malicious-shell.ps1 {0}`n        run: |")
    Assert 'workflow supply chain: a custom shell cannot wrap the approved install script' `
        ((Find-WorkflowSupplyChainProblem $shellWrapper) -match "shell: pwsh")
    $profileLoadingShell = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $profileLoadingShell[$k] = $secureWorkflows[$k] }
    $profileLoadingShell['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        '    shell: pwsh -NoProfile -Command ". ''{0}''"', '    shell: pwsh')
    Assert 'workflow supply chain: the install shell cannot load a modified PowerShell profile' `
        ((Find-WorkflowSupplyChainProblem $profileLoadingShell) -match 'NoProfile')
    $pathPoisoningStep = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $pathPoisoningStep[$k] = $secureWorkflows[$k] }
    $pathPoisoningStep['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "      - name: Install pinned gitleaks", "      - name: Poison PATH`n        run: Add-Content `$env:GITHUB_PATH 'fake-shell'`n      - name: Install pinned gitleaks")
    Assert 'workflow supply chain: no earlier step can replace the PowerShell interpreter through PATH' `
        ((Find-WorkflowSupplyChainProblem $pathPoisoningStep) -match 'first step')
    $selfHostedRunner = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $selfHostedRunner[$k] = $secureWorkflows[$k] }
    $selfHostedRunner['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        '    runs-on: windows-latest', '    runs-on: [self-hosted, Windows]')
    Assert 'workflow supply chain: release cannot run on an untrusted self-hosted runner' `
        ((Find-WorkflowSupplyChainProblem $selfHostedRunner) -match 'windows-latest')
    $decoyRunnerJob = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $decoyRunnerJob[$k] = $secureWorkflows[$k] }
    $decoyRunnerJob['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "jobs:`n", "jobs:`n  decoy:`n    runs-on: windows-latest`n    steps:`n      - run: echo decoy`n").Replace(
        '    runs-on: windows-latest', '    "\u0072uns-on": [self-hosted, Windows]')
    Assert 'workflow supply chain: a decoy job and escaped runs-on key cannot bypass runner validation' `
        ((Find-WorkflowSupplyChainProblem $decoyRunnerJob) -match 'release job.*windows-latest|quoted, escaped')
    $weakenedReleaseCheck = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $weakenedReleaseCheck[$k] = $secureWorkflows[$k] }
    $weakenedReleaseCheck['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        '-ReleaseTag $env:GITHUB_REF_NAME', '-IncludeReleaseDryRun')
    Assert 'workflow supply chain: release invocation cannot omit the release tag enforcement' `
        ((Find-WorkflowSupplyChainProblem $weakenedReleaseCheck) -match 'ReleaseTag')
    $wrappedReleaseCheck = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $wrappedReleaseCheck[$k] = $secureWorkflows[$k] }
    $wrappedReleaseCheck['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        '        shell: pwsh -NoProfile -Command ". ''{0}''"' + "`n        run: ./tools/Test-PublicRelease.ps1",
        '        shell: pwsh -NoProfile -File tools/release-shell.ps1 {0}' + "`n        run: ./tools/Test-PublicRelease.ps1")
    Assert 'workflow supply chain: release check shell cannot wrap or alter tag enforcement' `
        ((Find-WorkflowSupplyChainProblem $wrappedReleaseCheck) -match 'Release checks.*shell')
    $quotedShellWrapper = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $quotedShellWrapper[$k] = $secureWorkflows[$k] }
    $quotedShellWrapper['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "        run: |", "        'shell': 'pwsh -File tools/malicious-shell.ps1 {0}'`n        run: |")
    Assert 'workflow supply chain: a quoted duplicate shell key cannot wrap the install script' `
        ((Find-WorkflowSupplyChainProblem $quotedShellWrapper) -match "shell: pwsh")
    $duplicateRun = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $duplicateRun[$k] = $secureWorkflows[$k] }
    $duplicateRun['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "    run: |", "    'run': 'tools/malicious.ps1'`n    run: |")
    Assert 'workflow supply chain: a quoted duplicate run key cannot replace the install script' `
        ((Find-WorkflowSupplyChainProblem $duplicateRun) -match "exactly one.*literal run block")
    $stepEnvironment = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $stepEnvironment[$k] = $secureWorkflows[$k] }
    $stepEnvironment['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "        shell: pwsh", "        env:`n          PSModulePath: tools/malicious-modules`n        shell: pwsh")
    Assert 'workflow supply chain: extra step context cannot alter the approved install script' `
        ((Find-WorkflowSupplyChainProblem $stepEnvironment) -match 'only name, shell and run')
    $lateDigestCheck = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $lateDigestCheck[$k] = $secureWorkflows[$k] }
    $orderedVerification = "          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()`n          if (`$actual -cne `$gitleaksArchiveSha256) { throw `"Wrong checksum for `$asset`" }`n          Expand-Archive -LiteralPath (Join-Path `$tool `$asset) -DestinationPath `$tool"
    $lateVerification = "          Expand-Archive -LiteralPath (Join-Path `$tool `$asset) -DestinationPath `$tool`n          `$actual = (Get-FileHash -Algorithm SHA256 (Join-Path `$tool `$asset)).Hash.ToLowerInvariant()`n          if (`$actual -cne `$gitleaksArchiveSha256) { throw `"Wrong checksum for `$asset`" }"
    $lateDigestCheck['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace($orderedVerification, $lateVerification)
    Assert 'workflow supply chain: gitleaks digest checked after extraction fails' `
        ((Find-WorkflowSupplyChainProblem $lateDigestCheck) -match 'before Expand-Archive')
    $downloadedDigest = [ordered]@{}; foreach ($k in $secureWorkflows.Keys) { $downloadedDigest[$k] = $secureWorkflows[$k] }
    $downloadedDigest['.github/workflows/release.yml'] = $secureWorkflows['.github/workflows/release.yml'].Replace(
        "      `$actual =", "      Invoke-WebRequest https://example.invalid/gitleaks_checksums.txt`n      `$actual =")
    Assert 'workflow supply chain: checksum downloaded with the binary fails' `
        ((Find-WorkflowSupplyChainProblem $downloadedDigest) -match 'must not download its checksum')

    # Vulnerable packages
    $clean = '{"version":1,"projects":[{"path":"a/A.csproj","frameworks":[{"framework":"net10.0","topLevelPackages":[{"id":"X","resolvedVersion":"1.0.0"}]}]},{"path":"b/B.csproj"}]}'
    $r = Get-VulnerablePackage $clean
    Assert 'vulnerable: clean report passes' ($r.Found.Count -eq 0 -and $r.Errors.Count -eq 0 -and $r.Projects -eq 2)
    $vuln = '{"version":1,"projects":[{"path":"a/A.csproj","frameworks":[{"framework":"net10.0","transitivePackages":[{"id":"Y","resolvedVersion":"2.0.0","vulnerabilities":[{"severity":"High","advisoryurl":"https://github.com/advisories/GHSA-x"}]}]}]}]}'
    $r = Get-VulnerablePackage $vuln
    Assert 'vulnerable: transitive vulnerability is found' ($r.Found.Count -eq 1 -and $r.Found[0] -like 'Y 2.0.0 `[High`]*A.csproj (net10.0)')
    $err = '{"version":1,"problems":[{"level":"error","text":"Unable to load the service index"}],"projects":[]}'
    Assert 'vulnerable: errors of the command are reported' ((Get-VulnerablePackage $err).Errors.Count -eq 1)

    # Repository URL
    $good = [ordered]@{
        'Directory.Build.props' = '<PackageProjectUrl>https://github.com/Example-Org/WCS</PackageProjectUrl><RepositoryUrl>https://github.com/Example-Org/WCS</RepositoryUrl>'
        'README.md' = 'git clone https://github.com/Example-Org/WCS.git and https://github.com/jgraph/drawio'
        '.github/ISSUE_TEMPLATE/config.yml' = "contact_links:`n  - name: x`n    url: https://github.com/Example-Org/WCS/security/advisories/new`n"
        'CHANGELOG.md' = '[0.1.0]: https://github.com/Example-Org/WCS/releases/tag/v0.1.0'
    }
    Assert 'url: consistent files pass' ((Find-RepositoryUrlProblem $good 'https://github.com/Example-Org/WCS').Count -eq 0)
    Assert 'url: trailing slash and .git are tolerated' ((Find-RepositoryUrlProblem $good 'https://github.com/Example-Org/WCS.git').Count -eq 0)
    $p = Find-RepositoryUrlProblem $good 'https://github.com/Example-Org/WCS-Core'
    Assert 'url: a renamed repository is caught everywhere' ($p.Count -ge 4)
    $renamed = [ordered]@{}; foreach ($k in $good.Keys) { $renamed[$k] = $good[$k] }; $renamed['CHANGELOG.md'] = '[0.1.0]: https://github.com/Example-Org/Wcs/releases/tag/v0.1.0'
    Assert 'url: a link with different case is caught' ((Find-RepositoryUrlProblem $renamed 'https://github.com/Example-Org/WCS').Count -eq 1)
    $period = [ordered]@{}; foreach ($k in $good.Keys) { $period[$k] = $good[$k] }; $period['x.yml'] = '# assumes https://github.com/Example-Org/WCS. Update it if renamed; see https://github.com/Example-Org/WCS, too.'
    Assert 'url: a sentence ending after the URL is not part of it' ((Find-RepositoryUrlProblem $period 'https://github.com/Example-Org/WCS').Count -eq 0)
    Assert 'url: not a GitHub repository URL fails' ((Find-RepositoryUrlProblem $good 'https://example.org/wcs').Count -eq 1)

    Write-Host ''
    if ($failures.Count -gt 0) { Write-Host "Self-test: $($failures.Count) of $script:selfTestCount assertions FAILED" -ForegroundColor Red; return 1 }
    Write-Host "Self-test: all $script:selfTestCount assertions passed" -ForegroundColor Green
    return 0
}

if ($SelfTest) { exit (Invoke-SelfTest) }

$ReleaseTag = Resolve-EffectiveReleaseTag $ReleaseTag $env:GITHUB_ACTIONS $env:GITHUB_REF_TYPE $env:GITHUB_REF_NAME

# =================================================================================================
# The run on the repository
# =================================================================================================

$rootFull = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\', '/')
$results = [System.Collections.Generic.List[object]]::new()
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('wcs-release-check-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $tempDir | Out-Null

function Add-Result([string]$Check, [string]$Status, [string]$Detail = '', [bool]$Mandatory = $true) {
    $results.Add([pscustomobject]@{ Check = $Check; Status = $Status; Mandatory = $Mandatory; Detail = $Detail })
    $color = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } 'WARN' { 'Yellow' } default { 'DarkYellow' } }
    Write-Host ("[{0}] {1}{2}" -f $Status, $Check, $(if ($Detail) { ": $Detail" } else { '' })) -ForegroundColor $color
}
function Write-Step([string]$Text) { Write-Host ''; Write-Host "=== $Text ===" -ForegroundColor Cyan }
function Add-ProblemResult([string]$Check, $Problems, [string]$PassDetail = '', [int]$Show = 25) {
    $Problems = @($Problems)
    if ($Problems.Count -eq 0) { Add-Result $Check 'PASS' $PassDetail; return }
    $Problems | Select-Object -First $Show | ForEach-Object { Write-Host "    $_" }
    if ($Problems.Count -gt $Show) { Write-Host "    ... and $($Problems.Count - $Show) more" }
    Add-Result $Check 'FAIL' "$($Problems.Count) problem(s)"
}
function Invoke-Native([string]$Exe, [string[]]$Arguments) {
    Write-Host "> $Exe $($Arguments -join ' ')" -ForegroundColor DarkGray
    Push-Location -LiteralPath $rootFull
    try { & $Exe @Arguments | Out-Host; return $LASTEXITCODE } finally { Pop-Location }
}
function Get-Git([string[]]$Arguments) {
    # Callers wrap the result in @(): nothing is returned on failure, the lines otherwise
    $out = @(& git -C $rootFull @Arguments 2>$null)
    if ($LASTEXITCODE -ne 0) { return }
    return $out
}

Write-Host "Release checks of $rootFull"
Write-Host "Reports in $tempDir"

# --- 1. SDK ----------------------------------------------------------------------------------------
Write-Step 'SDK'
try {
    $global = Get-Content -LiteralPath (Join-Path $rootFull 'global.json') -Raw | ConvertFrom-Json
    $required = $global.sdk.version
    $rollForward = if ($global.sdk.PSObject.Properties.Name -contains 'rollForward') { $global.sdk.rollForward } else { 'latestPatch' }
    $allowPre = if ($global.sdk.PSObject.Properties.Name -contains 'allowPrerelease') { [bool]$global.sdk.allowPrerelease } else { $false }
    Push-Location -LiteralPath $rootFull
    try { $actual = (& dotnet --version 2>&1 | Select-Object -Last 1).ToString().Trim(); $sdkExit = $LASTEXITCODE } finally { Pop-Location }
    if ($sdkExit -ne 0) { Add-Result 'SDK (global.json)' 'FAIL' "dotnet --version failed: no SDK satisfies global.json ($required, $rollForward)" }
    else { Add-ProblemResult 'SDK (global.json)' (Test-SdkVersion $required $actual $rollForward $allowPre) "$actual for $required ($rollForward)" }
} catch { Add-Result 'SDK (global.json)' 'FAIL' $_.Exception.Message }

# --- 2. Working tree at the start ----------------------------------------------------------------
Write-Step 'Working tree'
$isGit = $null -ne (Get-Git @('rev-parse', '--is-inside-work-tree'))
$statusAtStart = $null
if (-not $isGit) {
    Add-Result 'Git working tree' 'FAIL' 'not a git working tree: the release is built from a clone'
} else {
    $statusAtStart = @(Get-Git @('status', '--porcelain', '--untracked-files=all'))
    if ($statusAtStart.Count -eq 0) { Add-Result 'Clean working tree at start' 'PASS' "HEAD $((Get-Git @('rev-parse', '--short', 'HEAD')) -join '')" }
    elseif ($AllowDirtyWorkingTree) { $statusAtStart | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" }; Add-Result 'Clean working tree at start' 'WARN' "$($statusAtStart.Count) change(s) accepted by -AllowDirtyWorkingTree" $false }
    else { $statusAtStart | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" }; Add-Result 'Clean working tree at start' 'FAIL' "$($statusAtStart.Count) change(s); commit or stash them, or pass -AllowDirtyWorkingTree" }
}

# --- 3-5. Restore, build, tests ------------------------------------------------------------------
Write-Step 'Restore (locked mode)'
$restored = (Invoke-Native 'dotnet' @('restore', 'Wcs.slnx', '--locked-mode')) -eq 0
Add-Result 'Restore --locked-mode' $(if ($restored) { 'PASS' } else { 'FAIL' })

$built = $false
Write-Step 'Build (Release)'
if ($restored) {
    $built = (Invoke-Native 'dotnet' @('build', 'Wcs.slnx', '-c', 'Release', '--no-restore', '-t:Rebuild', '-warnaserror')) -eq 0
    Add-Result 'Build Release' $(if ($built) { 'PASS' } else { 'FAIL' })
} else { Add-Result 'Build Release' 'FAIL' 'not run: the restore failed' }

[xml]$slnx = Get-Content -LiteralPath (Join-Path $rootFull 'Wcs.slnx') -Raw
$allProjects = @($slnx.SelectNodes('//Project') | ForEach-Object { $_.Path.Replace('\', '/') })
# A test project is one that references the test SDK (Wcs.Connection.Test is the loopback driver,
# not a test project, whatever its name says)
$testProjects = @($allProjects | Where-Object {
    $file = Join-Path $rootFull $_
    (Test-Path -LiteralPath $file) -and (Select-String -LiteralPath $file -Pattern 'Microsoft\.NET\.Test\.Sdk|<IsTestProject>\s*true' -Quiet)
})
$browserProject = $testProjects | Where-Object { $_ -like '*Browser.Test.csproj' } | Select-Object -First 1
$testProjects = @($testProjects | Where-Object { $_ -ne $browserProject })

Write-Step "Tests ($($testProjects.Count) projects, browser tests apart)"
foreach ($project in $testProjects) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($project)
    if (-not $built) { Add-Result "Tests $name" 'FAIL' 'not run: the build failed'; continue }
    $exit = Invoke-Native 'dotnet' @('test', $project, '-c', 'Release', '--no-build')
    Add-Result "Tests $name" $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($exit -ne 0) { "exit $exit" } else { '' })
}

# --- 6. Browser tests ----------------------------------------------------------------------------
Write-Step 'Browser tests'
if (-not $browserProject) { Add-Result 'Browser tests' 'FAIL' 'no *Browser.Test project in Wcs.slnx' }
elseif (-not $IncludeBrowserTests) { Add-Result 'Browser tests' 'SKIP' 'not requested (-IncludeBrowserTests); CI runs them in browser.yml' $false }
elseif (-not $restored) { Add-Result 'Browser tests' 'FAIL' 'not run: the restore failed' }
else {
    # As the README of the suite says: the host is built first, in Debug, and named by WCS_HOST_EXE;
    # the tests run with --no-build. The Playwright browsers must be installed already
    # (bin/Debug/net10.0/playwright.ps1 install chromium): this script downloads nothing.
    $hostProject = $allProjects | Where-Object { $_ -like '*/Wcs.Host/src/Wcs.Host.csproj' } | Select-Object -First 1
    $ok = (Invoke-Native 'dotnet' @('build', $hostProject, '-c', 'Debug', '--no-restore')) -eq 0
    if ($ok) { $ok = (Invoke-Native 'dotnet' @('build', $browserProject, '-c', 'Debug', '--no-restore')) -eq 0 }
    if (-not $ok) { Add-Result 'Browser tests' 'FAIL' 'the Debug build of the host or of the suite failed' }
    else {
        $previous = $env:WCS_HOST_EXE
        $env:WCS_HOST_EXE = Join-Path $rootFull ((Split-Path -Parent $hostProject) + '/bin/Debug/net10.0/Wcs.Host.exe')
        try { $exit = Invoke-Native 'dotnet' @('test', $browserProject, '-c', 'Debug', '--no-build', '--logger', 'console;verbosity=normal') }
        finally { $env:WCS_HOST_EXE = $previous }
        Add-Result 'Browser tests' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' }) $(if ($exit -ne 0) { "exit $exit (are the Playwright browsers installed?)" } else { '' })
    }
}

# --- 7-9. Links, contracts, SBOM -----------------------------------------------------------------
Write-Step 'Markdown links'
if ($built) {
    $exit = Invoke-Native 'dotnet' @('run', '--project', 'tools/Wcs.LinkCheck/src', '-c', 'Release', '--no-build', '--', '.')
    Add-Result 'Links (Wcs.LinkCheck)' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' })
} else { Add-Result 'Links (Wcs.LinkCheck)' 'FAIL' 'not run: the build failed' }

Write-Step 'Machine-readable contracts'
if ($built) {
    $exit = Invoke-Native 'dotnet' @('run', '--project', 'tools/Wcs.ContractCheck/src', '-c', 'Release', '--no-build', '--', 'check')
    Add-Result 'Contracts and examples (Wcs.ContractCheck)' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' })
} else { Add-Result 'Contracts and examples (Wcs.ContractCheck)' 'FAIL' 'not run: the build failed' }

Write-Step 'SBOM'
if ($restored) {
    $exit = Invoke-Native 'pwsh' @('-NoProfile', '-File', 'sbom/Test-WcsSbom.ps1', '-Out', (Join-Path $tempDir 'wcs.cdx.json'))
    Add-Result 'SBOM (sbom/Test-WcsSbom.ps1)' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' })
} else { Add-Result 'SBOM (sbom/Test-WcsSbom.ps1)' 'FAIL' 'not run: the restore failed' }

# --- 10. Vulnerable packages ---------------------------------------------------------------------
Write-Step 'Vulnerable packages'
if ($SkipVulnerabilityCheck) {
    Write-Host ''
    Write-Host '################################################################################' -ForegroundColor Yellow
    Write-Host '#  WARNING: the vulnerable-package check was SKIPPED (-SkipVulnerabilityCheck). #' -ForegroundColor Yellow
    Write-Host '#  This run does not show that the release is free of known vulnerabilities.   #' -ForegroundColor Yellow
    Write-Host '#  Never prepare a release on a run with this switch.                          #' -ForegroundColor Yellow
    Write-Host '################################################################################' -ForegroundColor Yellow
    Add-Result 'Vulnerable packages' 'SKIP' 'SKIPPED BY -SkipVulnerabilityCheck: not valid for a release' $false
} elseif (-not $restored) { Add-Result 'Vulnerable packages' 'FAIL' 'not run: the restore failed' }
else {
    Push-Location -LiteralPath $rootFull
    try { $json = (& dotnet list Wcs.slnx package --vulnerable --include-transitive --format json 2>$null) -join "`n"; $listExit = $LASTEXITCODE } finally { Pop-Location }
    Set-Content -LiteralPath (Join-Path $tempDir 'vulnerable.json') -Value $json -Encoding utf8
    if ($listExit -ne 0) { Add-Result 'Vulnerable packages' 'FAIL' "dotnet list package failed (exit $listExit)" }
    else {
        try {
            $report = Get-VulnerablePackage $json
            $problems = @($report.Found) + @($report.Errors | ForEach-Object { "dotnet list package error: $_" })
            Add-ProblemResult 'Vulnerable packages' $problems "none in $($report.Projects) projects"
        } catch { Add-Result 'Vulnerable packages' 'FAIL' "cannot read the report: $($_.Exception.Message)" }
    }
}

# --- Files of the repository for the text checks -------------------------------------------------
$tracked = if ($isGit) { @((& git -C $rootFull ls-files -z) -split "`0" | Where-Object { $_ }) } else {
    @(Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force | Where-Object { $_.FullName -notmatch '[\\/](\.git|bin|obj|node_modules|TestResults)[\\/]' } |
        ForEach-Object { $_.FullName.Substring($rootFull.Length + 1).Replace('\', '/') })
}
$tracked = @($tracked | Where-Object { Test-Path -LiteralPath (Join-Path $rootFull $_) -PathType Leaf })
$textExtensions = '\.(md|markdown|txt|cs|razor|cshtml|json|ya?ml|ps1|psm1|psd1|props|targets|csproj|slnx|xml|config|js|css|html?|sql|sh|cmd|bat|toml|editorconfig|gitignore|gitattributes)$'
$texts = [ordered]@{}
foreach ($rel in $tracked) {
    if ($rel -notmatch $textExtensions -and $rel -notmatch '(^|/)(LICENSE|NOTICE)$') { continue }
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $rootFull $rel))
    if ([Array]::IndexOf($bytes, [byte]0, 0, [Math]::Min($bytes.Length, 8000)) -ge 0) { continue }
    $texts[$rel] = [System.Text.Encoding]::UTF8.GetString($bytes)
}
$isThirdPartyBundle = { param($rel) $rel -like 'WCS/Wcs.WebEditor/src/wwwroot/drawio/*' }
$currentDocs = @($texts.Keys | Where-Object { $_ -match '\.(md|markdown)$' -and $_ -notlike 'docs/history/*' -and -not (& $isThirdPartyBundle $_) })

# --- 11. Characters of the current documentation -------------------------------------------------
Write-Step 'Characters of the current documentation'
$encodingScript = Join-Path $rootFull 'tools/Test-DocumentEncoding.ps1'
if (Test-Path -LiteralPath $encodingScript) {
    $exit = Invoke-Native 'pwsh' @('-NoProfile', '-File', 'tools/Test-DocumentEncoding.ps1')
    Add-Result 'Document encoding (tools/Test-DocumentEncoding.ps1)' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' })
} else {
    $problems = [System.Collections.Generic.List[string]]::new()
    $strict = [System.Text.UTF8Encoding]::new($false, $true)
    foreach ($rel in $currentDocs) {
        try { [void]$strict.GetString([System.IO.File]::ReadAllBytes((Join-Path $rootFull $rel))) } catch { $problems.Add("${rel}: not valid UTF-8"); continue }
        foreach ($p in (Find-AnomalousCharacter $rel $texts[$rel])) { $problems.Add($p) }
    }
    Add-ProblemResult 'Document encoding (built-in check)' $problems "$($currentDocs.Count) documents"
}

# --- 12. Version ---------------------------------------------------------------------------------
Write-Step 'Version'
$version = $null
$repoUrlFromProps = $null
try {
    $propsText = Get-Content -LiteralPath (Join-Path $rootFull 'Directory.Build.props') -Raw
    [xml]$props = $propsText
    $version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $repoUrlFromProps = ([regex]::Match($propsText, '<RepositoryUrl>([^<]*)</RepositoryUrl>')).Groups[1].Value.TrimEnd('/')
} catch { }
$urlForLinks = if ($RepositoryUrl) { $RepositoryUrl.TrimEnd('/') -replace '\.git$', '' } else { $repoUrlFromProps }
$changelogPath = Join-Path $rootFull 'CHANGELOG.md'
$changelog = if (Test-Path -LiteralPath $changelogPath) { [System.IO.File]::ReadAllLines($changelogPath) } else { @() }
if (-not $version -or $version -notmatch $script:VersionPattern) { Add-Result 'Version' 'FAIL' "Directory.Build.props: '$version' is not X.Y.Z or X.Y.Z-preview.N" }
else {
    $problems = [System.Collections.Generic.List[string]]::new()
    try {
        $sbomVersion = (Get-Content -LiteralPath (Join-Path $rootFull 'sbom/wcs.cdx.json') -Raw | ConvertFrom-Json).metadata.component.version
        if ($sbomVersion -ne $version) { $problems.Add("sbom/wcs.cdx.json: version '$sbomVersion'") }
    } catch { $problems.Add("sbom/wcs.cdx.json: $($_.Exception.Message)") }
    foreach ($p in (Test-ChangelogVersion $changelog $version $urlForLinks)) { $problems.Add($p) }
    foreach ($rel in $currentDocs | Where-Object { $_ -ne 'CHANGELOG.md' }) { foreach ($p in (Find-VersionMismatch $rel $texts[$rel] $version)) { $problems.Add($p) } }
    Add-ProblemResult 'Version coherence' $problems "$version in Directory.Build.props, SBOM, CHANGELOG.md and docs"
}

# --- 13. Community files -------------------------------------------------------------------------
Write-Step 'Community files'
$required = 'LICENSE', 'NOTICE', 'README.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'SECURITY.md', 'SUPPORT.md', 'SAFETY.md',
    'CHANGELOG.md', 'THIRD-PARTY-NOTICES.md', '.github/ISSUE_TEMPLATE/bug_report.yml', '.github/ISSUE_TEMPLATE/feature_request.yml',
    '.github/ISSUE_TEMPLATE/config.yml', '.github/pull_request_template.md', '.github/workflows/ci.yml', '.github/workflows/browser.yml',
    '.github/workflows/release.yml', 'sbom/wcs.cdx.json'
$problems = [System.Collections.Generic.List[string]]::new()
$trackedSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$tracked, [StringComparer]::Ordinal)
foreach ($f in $required) {
    $full = Join-Path $rootFull $f
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { $problems.Add("$f is missing") }
    elseif ((Get-Item -LiteralPath $full).Length -eq 0) { $problems.Add("$f is empty") }
    elseif ($isGit -and -not $trackedSet.Contains($f)) { $problems.Add("$f is not tracked by git") }
}
if (-not @($tracked | Where-Object { $_ -like 'licenses/*' })) { $problems.Add('licenses/ has no tracked file') }
Add-ProblemResult 'Community files' $problems "$($required.Count) files and licenses/"

# --- 14. Workflow supply chain -------------------------------------------------------------------
Write-Step 'Workflow supply chain'
$workflowFiles = [ordered]@{}
foreach ($rel in $texts.Keys | Where-Object { $_ -match '^\.github/workflows/[^/]+\.ya?ml$' }) {
    $workflowFiles[$rel] = $texts[$rel]
}
Add-ProblemResult 'Workflow supply chain' (Find-WorkflowSupplyChainProblem $workflowFiles) `
    "$($workflowFiles.Count) workflows; immutable action SHAs and trusted Gitleaks digest"

# --- 15. Placeholders ----------------------------------------------------------------------------
Write-Step 'Placeholders'
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($rel in $texts.Keys) {
    if ($rel -like 'docs/history/*' -or (& $isThirdPartyBundle $rel)) { continue }
    foreach ($p in (Find-Placeholder $rel $texts[$rel])) { $problems.Add($p) }
}
Add-ProblemResult 'Placeholders' $problems "$($texts.Count) text files (docs/history and the draw.io bundle apart)"

# --- 16. Secrets ---------------------------------------------------------------------------------
Write-Step 'Secrets'
$gitleaksCommand = Get-Command gitleaks -ErrorAction SilentlyContinue | Select-Object -First 1
$gitleaks = if ($GitleaksPath) { $GitleaksPath } elseif ($env:GITLEAKS_PATH) { $env:GITLEAKS_PATH } elseif ($gitleaksCommand) { $gitleaksCommand.Source } else { $null }
$gitleaksIntegrityProblem = $null
if ($ReleaseTag -and $gitleaks -and (Test-Path -LiteralPath $gitleaks -PathType Leaf)) {
    $trustedGitleaksExecutableSha256 = '3cc09feda0ed0f9fcb2c10f71a6186c956725d077068cb6c4a3d594c6461c2ed'
    try {
        $stream = [System.IO.File]::OpenRead((Resolve-Path -LiteralPath $gitleaks).Path)
        try { $actualGitleaksExecutableSha256 = Get-StreamSha256Hex $stream } finally { $stream.Dispose() }
        if ($actualGitleaksExecutableSha256 -cne $trustedGitleaksExecutableSha256) {
            $gitleaksIntegrityProblem = "the selected executable does not match the trusted Gitleaks 8.29.1 Windows x64 digest ($actualGitleaksExecutableSha256)"
        }
    }
    catch {
        $gitleaksIntegrityProblem = "cannot hash the selected executable: $($_.Exception.Message)"
    }
}
if (-not $gitleaks -or -not (Test-Path -LiteralPath $gitleaks)) {
    $gitleaksRequired = Test-GitleaksRequired $ReleaseTag
    Add-Result 'Secrets (gitleaks)' $(if ($gitleaksRequired) { 'FAIL' } else { 'SKIP' }) `
        'gitleaks not found (-GitleaksPath, GITLEAKS_PATH or PATH): only the built-in patterns ran' $gitleaksRequired
} elseif ($gitleaksIntegrityProblem) {
    Add-Result 'Secrets (gitleaks)' 'FAIL' $gitleaksIntegrityProblem
} elseif (-not $isGit) { Add-Result 'Secrets (gitleaks)' 'FAIL' 'not a git working tree' }
else {
    # Git mode: the history that would be pushed. The repository's .gitleaks.toml (read by gitleaks
    # from the scanned folder) allows the documented demo keys and the draw.io bundle.
    $reportPath = Join-Path $tempDir 'gitleaks.json'
    $exit = Invoke-Native $gitleaks @('git', $rootFull, '--no-banner', '--redact', '--report-format', 'json', '--report-path', $reportPath)
    if ($exit -eq 0) { Add-Result 'Secrets (gitleaks)' 'PASS' 'no leak in the git history' }
    else {
        $leaks = @(try { Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json } catch { })
        $leaks | Select-Object -First 25 | ForEach-Object { Write-Host "    $($_.File):$($_.StartLine) $($_.RuleID) (commit $($_.Commit.Substring(0, 8)))" }
        Add-Result 'Secrets (gitleaks)' 'FAIL' "$($leaks.Count) finding(s), report $reportPath"
    }
}
$problems = [System.Collections.Generic.List[string]]::new()
# The draw.io bundle is third-party code copied verbatim: its XHR code passes a password parameter
foreach ($rel in $texts.Keys) { if (& $isThirdPartyBundle $rel) { continue }; foreach ($p in (Find-SecretLike $rel $texts[$rel])) { $problems.Add($p) } }
Add-ProblemResult 'Secrets (built-in patterns)' $problems "$($texts.Count) text files (the draw.io bundle apart)"

# --- 17. Local paths -----------------------------------------------------------------------------
Write-Step 'Local paths'
$problems = [System.Collections.Generic.List[string]]::new()
foreach ($rel in $texts.Keys) { foreach ($p in (Find-LocalPath $rel $texts[$rel])) { $problems.Add($p) } }
Add-ProblemResult 'Personal and local paths' $problems "$($texts.Count) text files"

# --- 18. Release tag -----------------------------------------------------------------------------
Write-Step 'Release tag'
if (-not $ReleaseTag) { Add-Result 'Release tag and dated CHANGELOG section' 'SKIP' 'no -ReleaseTag' $false }
elseif (-not $version) { Add-Result 'Release tag and dated CHANGELOG section' 'FAIL' 'no version in Directory.Build.props' }
else {
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($p in (Test-ReleaseTagName $ReleaseTag $version)) { $problems.Add($p) }
    foreach ($p in (Test-ChangelogVersion $changelog $version $urlForLinks -ForRelease)) { $problems.Add($p) }
    if ($isGit -and (Get-Git @('rev-parse', '-q', '--verify', "refs/tags/$ReleaseTag"))) {
        $tagged = (Get-Git @('rev-list', '-n', '1', $ReleaseTag)) -join ''
        $head = (Get-Git @('rev-parse', 'HEAD')) -join ''
        if ($tagged -ne $head) { $problems.Add("tag $ReleaseTag already exists on another commit ($($tagged.Substring(0, 8)))") }
    }
    Add-ProblemResult 'Release tag and dated CHANGELOG section' $problems "$ReleaseTag, section $((Get-ChangelogSection $changelog $version).Heading)"
}

# --- 19. Repository URL --------------------------------------------------------------------------
Write-Step 'Repository URL'
$url = if ($RepositoryUrl) { $RepositoryUrl } else { $repoUrlFromProps }
if (-not $url) { Add-Result 'Repository URL' 'FAIL' 'no -RepositoryUrl and no RepositoryUrl in Directory.Build.props' }
else {
    $files = [ordered]@{}
    foreach ($rel in $texts.Keys) {
        if ($rel -in 'Directory.Build.props', 'README.md' -or $rel -like '.github/*' -or ($rel -in $currentDocs)) { $files[$rel] = $texts[$rel] }
    }
    Add-ProblemResult 'Repository URL' (Find-RepositoryUrlProblem $files $url) "$url $(if (-not $RepositoryUrl) { '(from Directory.Build.props)' })"
}

# --- 20. Release artifact dry-run ----------------------------------------------------------------
Write-Step 'Release artifact dry-run'
if (-not $IncludeReleaseDryRun -and -not $ReleaseTag) {
    Add-Result 'Release archives and smoke test' 'SKIP' 'not requested (-IncludeReleaseDryRun); always runs with -ReleaseTag' $false
}
elseif (-not $built) {
    Add-Result 'Release archives and smoke test' 'FAIL' 'not run: the Release build failed'
}
else {
    $artifactScript = Join-Path $rootFull 'tools/New-WcsReleaseArtifacts.ps1'
    $artifactOutput = if ($ReleaseArtifactsDirectory) {
        [System.IO.Path]::GetFullPath($ReleaseArtifactsDirectory, $rootFull)
    } else { Join-Path $tempDir 'release-artifacts' }
    if (-not (Test-Path -LiteralPath $artifactScript)) {
        Add-Result 'Release archives and smoke test' 'FAIL' 'tools/New-WcsReleaseArtifacts.ps1 is missing'
    }
    else {
        $exit = Invoke-Native 'pwsh' @('-NoProfile', '-File', $artifactScript, '-Root', $rootFull,
            '-OutputDirectory', $artifactOutput, '-NoRestore', '-SmokeTest')
        Add-Result 'Release archives and smoke test' $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' }) `
            $(if ($exit -eq 0) { $artifactOutput } else { "exit $exit" })
    }
}

# --- 21. Working tree at the end -----------------------------------------------------------------
Write-Step 'Working tree after the checks'
if ($isGit) {
    $statusAtEnd = @(Get-Git @('status', '--porcelain', '--untracked-files=all'))
    $added = @($statusAtEnd | Where-Object { $statusAtStart -notcontains $_ })
    $gone = @($statusAtStart | Where-Object { $statusAtEnd -notcontains $_ })
    Add-ProblemResult 'Working tree not dirtied by the tools' (@($added | ForEach-Object { "changed: $_" }) + @($gone | ForEach-Object { "no longer: $_" })) 'git status unchanged'
} else { Add-Result 'Working tree not dirtied by the tools' 'FAIL' 'not a git working tree' }

# --- Summary -------------------------------------------------------------------------------------
Write-Host ''
Write-Host '=== Summary ===' -ForegroundColor Cyan
$results | Format-Table -AutoSize -Wrap @{ n = 'Status'; e = { $_.Status } }, @{ n = 'Check'; e = { $_.Check } }, @{ n = 'Mandatory'; e = { if ($_.Mandatory) { 'yes' } else { 'no' } } }, @{ n = 'Detail'; e = { $_.Detail } } | Out-Host
$failed = @($results | Where-Object { $_.Status -eq 'FAIL' -and $_.Mandatory })
Write-Host "Reports: $tempDir"
if ($failed.Count -gt 0) {
    Write-Host "NOT READY: $($failed.Count) mandatory check(s) failed." -ForegroundColor Red
    exit 1
}
Write-Host "Ready: every mandatory check passed$(if (@($results | Where-Object Status -in 'SKIP', 'WARN')) { ' (see the skipped or warned checks above)' })." -ForegroundColor Green
exit 0
