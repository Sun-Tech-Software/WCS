# Release process

How a version of the WCS is prepared, tagged, built and published, what a release contains, how to
check what you downloaded, and how to move an installation to a new version. What the version number
promises is in [versioning and compatibility](versioning-and-compatibility.md). What the first public
release still needs (decisions, GitHub settings, plant tests) is in the
[publication checklist](publication-checklist.md).

## What a release is

A release is a tag `vX.Y.Z` or `vX.Y.Z-preview.N` on `main`, and a GitHub release built from it by the
`Release` workflow ([`.github/workflows/release.yml`](../.github/workflows/release.yml)):

| File | What it holds |
|---|---|
| `wcs-host-<version>.zip` | the published host (`Wcs.Host`), with the web editor and the loopback driver |
| `wcs-dbtool-<version>.zip` | `Wcs.DbTool`, the backup, verify and restore tool of the same version |
| `wcs-sample-wms-<version>.zip` | the sample WMS, for a demo or to test an adapter, never for a plant |
| `wcs-<version>.cdx.json` | the CycloneDX SBOM of the version |
| `SHA256SUMS.txt` | the SHA-256 of each file above |

Every zip carries `LICENSE`, `NOTICE`, `THIRD-PARTY-NOTICES.md`, `licenses/`, and the repository's
`README.md`, `CHANGELOG.md`, `SAFETY.md` and `SECURITY.md`. The sample WMS zip also carries
`plant/sample-plant.json` and `plant/load-sample-plant.ps1`, so the [quick start](quick-start.md) can
be followed with the release's files: start `Wcs.SampleWms.exe` and `Wcs.Host.exe` from their own
folders (the host reads its settings and `wwwroot` from the working directory), with the keys of step 4
as environment variables, and run `plant/load-sample-plant.ps1` from the sample WMS folder. The
workflow reads every zip back before attaching it: the files above must be there and not empty, and no
`appsettings.Secrets.json`, `appsettings.Development.json`, database or `Data/` folder may be. GitHub
adds the source code archives of the tag by itself; they are not in `SHA256SUMS.txt`.

The binaries are **framework-dependent**: they need the .NET 10 runtime on the machine (the ASP.NET
Core Runtime 10 for the host and the sample WMS; the .NET Runtime 10 is enough for `Wcs.DbTool`). They
are built on Windows x64: each folder holds a Windows launcher (`Wcs.Host.exe`) and the portable
assemblies (`dotnet Wcs.Host.dll`). Framework-dependent because runtime security fixes then reach the
WCS through the runtime's own updates, without a new WCS release for each of them, and because the
zips stay small.

**No NuGet packages are published.** The WCS is distributed as source: the libraries are
referenced from a clone or a submodule at the release tag
([README](../README.md#using-the-libraries-from-your-code)). CI does run `dotnet pack` on every change,
to keep the projects packable for when packages are published.

## Preparing a version

On a branch, through a pull request to `main` like any other change:

1. **Version.** Set `Version` in `Directory.Build.props` to the new version (it may already be there:
   the property always holds the version being prepared).
2. **Changelog.** In [CHANGELOG.md](../CHANGELOG.md), move what is under `Unreleased` into a new section
   `## [X.Y.Z] - YYYY-MM-DD`, dated the day of the tag; leave `Unreleased` in place, empty; update the
   comparison links at the bottom of the file. Breaking changes are marked as such, with what to do
   ([before 1.0](versioning-and-compatibility.md#before-10)). The section, without its heading, becomes
   the text of the GitHub release: remove the sentence that says the date is written when the version
   is tagged.
3. **Lock files and SBOM.** If packages changed since the last version, the lock files and
   `sbom/wcs.cdx.json` already came with those changes: CI fails otherwise
   ([CONTRIBUTING](../CONTRIBUTING.md#dependencies)).
4. **Green.** The `CI` and `Browser tests` workflows must be green on the commit that will be tagged.
   The release workflow repeats the build and the test suites, but not the browser tests.
5. **Check locally.** On a clean clone of the branch, run
   `pwsh -File tools/Test-PublicRelease.ps1 -ReleaseTag vX.Y.Z[-preview.N] -IncludeBrowserTests`
   (add `-GitleaksPath <gitleaks.exe>` if gitleaks is not on the `PATH`). With a release tag, Gitleaks
   and the complete artifact dry-run are mandatory: the three archives are created in a temporary
   directory, inspected, checksummed, extracted and smoke-tested. Without a tag, request that slower
   check explicitly with `-IncludeReleaseDryRun`. The script runs every check listed under
   [Tagging](#tagging) and ends with a summary table; it creates no tag and changes no tracked file
   ([CONTRIBUTING](../CONTRIBUTING.md#all-the-checks-at-once)).

## Tagging

From an up-to-date `main`, on the merged commit:

```powershell
git tag -a v0.1.0 -m "WCS 0.1.0"
git push origin v0.1.0
```

Pushing the tag starts the `Release` workflow. It stops, and creates nothing, when:

- the tag does not match `Version` in `Directory.Build.props`, or the version is not `X.Y.Z` or
  `X.Y.Z-preview.N`;
- `CHANGELOG.md` has no section for the version, the section is not dated, is empty, or still says it
  is not yet released;
- any mandatory check of `tools/Test-PublicRelease.ps1` fails: SDK, clean tree, locked restore, build,
  a test suite (the browser tests run in `browser.yml`), links, contracts and examples, SBOM,
  vulnerable packages, document encoding, one version everywhere, community files, workflow
  supply-chain pins, placeholders, secrets (Gitleaks is required here), local paths, the repository
  URL, the artifact dry-run, or a tree changed by the tools;
- an archive misses a required file or contains a file that must not be distributed.

Otherwise it publishes the three applications with `tools/New-WcsReleaseArtifacts.ps1`, zips them,
adds the SBOM, writes and verifies `SHA256SUMS.txt`, smoke-tests the programs from the extracted archives,
keeps everything as an artefact of the run, and creates a **draft** GitHub release with those files
and the changelog section as its text (marked as a pre-release when the version has a `-preview`
suffix). A maintainer reads the draft, downloads and checks a file or two as below, and publishes it.

Third-party actions in all workflows are pinned to full commit SHAs. Their release tags remain in
comments for review, and Dependabot proposes updates. The release workflow also pins Gitleaks 8.29.1
and keeps the expected SHA-256 of its Windows x64 archive in the repository; it does not trust a
checksum downloaded beside the binary. Its installation is the first step of the `release` job on
the GitHub-hosted `windows-latest` runner and uses an exact `pwsh -NoProfile` shell; the approved
script is checked before any archive is extracted. For tagged releases the checker also verifies the
SHA-256 of the selected executable immediately before running it, so a later path or file replacement
is rejected. `tools/Test-PublicRelease.ps1` enforces this execution boundary as well as the action and
digest pins. In GitHub Actions it also derives the release tag from the immutable tag context and the
workflow fixes the complete check invocation and its `-NoProfile` shell.

A tag is never moved or reused once its release is published. If something is wrong before the draft is
published, delete the draft and the tag, fix, and tag again; after publication, release a new version.

## Verifying a download

Put `SHA256SUMS.txt` and the files you downloaded in one folder. In PowerShell:

```powershell
Get-Content SHA256SUMS.txt | ForEach-Object {
    $hash, $name = $_ -split '\s+', 2
    if (Test-Path $name) {
        $actual = (Get-FileHash -Algorithm SHA256 $name).Hash.ToLowerInvariant()
        '{0}  {1}' -f $(if ($actual -eq $hash) { 'OK    ' } else { 'FAILED' }), $name
    }
}
```

On Linux: `sha256sum --check --ignore-missing SHA256SUMS.txt`.

The checksums prove that a file is the one attached to the release page; they come from the same page,
so they do not prove who built it. The binaries are not signed.

## Upgrading an installation

1. Read the [changelog](../CHANGELOG.md) of every version between yours and the new one, and the
   [compatibility rules](versioning-and-compatibility.md): a breaking change in the WMS contract or
   the driver contract means a new adapter or driver build, before the host is upgraded.
2. Check the downloaded files ([above](#verifying-a-download)).
3. Follow the upgrade procedure of
   [backup, restore and disaster recovery](integration/backup-restore-and-disaster-recovery.md#upgrade):
   stop the engine and the service, back up with the `Wcs.DbTool` of the version you are leaving and
   keep that version's folder, put the new version in place
   ([deployment](integration/deployment-and-operations.md#upgrade)), start, and check
   `GET /api/v1/status` (its `version` field) and `Wcs.DbTool verify`.
4. If it goes wrong, [roll back](integration/backup-restore-and-disaster-recovery.md#rollback) with the
   previous version's folder and its `Wcs.DbTool`.

For code that references the libraries, move the clone or the submodule to the new tag, rebuild, and
run your tests against a host of the new version.
