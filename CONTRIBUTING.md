# Contributing to the Sun Tech WCS

Thank you for considering a contribution. This file says how contributions are handled, how to build
and test the code, and what is expected of a change.

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## How contributions are handled

- The repository is an **initial public preview**, maintained by Sun Tech Srl.
- Issues and pull requests are welcome. They are reviewed, accepted and merged **at Sun Tech's
  discretion**: a pull request may be declined, changed before merging, or left open, and there is no
  guaranteed response time ([SUPPORT.md](SUPPORT.md)).
- For a large change (a new feature, a change of a contract, a change of how the engine decides),
  open an issue first and describe it: the engine runs real plants, and a change that looks small can
  change behaviour on the field.
- **Security problems are never reported in a public issue or pull request**: use GitHub's private
  vulnerability reporting, as described in [SECURITY.md](SECURITY.md).

## Licence of contributions

The WCS is released under the [Apache License, Version 2.0](LICENSE). Unless you state otherwise, a
contribution you submit is licensed under the same terms (Apache 2.0, section 5), and by opening a pull
request you confirm that you have the right to submit it.

## Build

Requirements: the .NET 10 SDK of the version in [global.json](global.json) or a later patch of the
same feature band (`rollForward: latestPatch`); CI installs exactly that version. Windows is the
reference platform: `Common/Log.Windows` writes to the Windows event log and the host can run as a
Windows service. Linux has not been verified.

```powershell
dotnet build Wcs.slnx
```

Building a single project is often faster and tells more precisely what broke:

```powershell
dotnet build WCS/Wcs.Host/src/Wcs.Host.csproj
```

Packages come only from nuget.org (see `nuget.config`). Every project has a committed
`packages.lock.json`; CI restores with `--locked-mode`, so a change of a package reference must
come with its updated lock file (see [Dependencies](#dependencies)).

The version of the WCS is written once, in [Directory.Build.props](Directory.Build.props); do not set
`Version` in a project file. Versions and releases follow
[docs/versioning-and-compatibility.md](docs/versioning-and-compatibility.md) and
[docs/release-process.md](docs/release-process.md).

## Test

| Suite | What it covers |
|---|---|
| `WCS/Wcs.Engine/tests/Wcs.Engine.Test` | the engine: graph, missions, routes, persistence on in-memory SQLite |
| `WCS/Wcs.Host/tests/Wcs.Host.Test` | routes, hub, security, the host in memory, and the guides' reference pages against the code |
| `WCS/Wcs.WebEditor/tests/Wcs.WebEditor.Test` | the web editor components, with bUnit |
| `WCS/Wcs.WebEditor/tests/Wcs.WebEditor.Browser.Test` | the web editor in a real browser (Playwright) against a real host |
| `WCS/Wcs.Wms.Client/tests`, `WCS/Wcs.Wms.Contracts/tests`, `WCS/Wcs.Api.Client/tests` | the WMS boundary and the typed client |
| `samples/Wcs.SampleWms.Test` | the sample WMS, called through the WCS's own client, and the monitoring scripts of `tools/monitoring` run against a real host |
| `tools/Wcs.LinkCheck/tests`, `WCS/tools/Wcs.DbTool/tests` | the link checker and the backup tool |

```powershell
dotnet test WCS/Wcs.Engine/tests/Wcs.Engine.Test/Wcs.Engine.Test.csproj
```

The engine tests need nothing from the environment. The browser tests do not skip themselves when
Playwright browsers or a built host are missing: they fail and say what to install; their
[README](WCS/Wcs.WebEditor/tests/Wcs.WebEditor.Browser.Test/README.md) explains the one-time setup.
So `dotnet test` on the whole solution needs that setup first.

A change is ready when the projects it touches build without errors and the suites that cover them
are green. Concurrency-sensitive changes deserve more than one run.

If you change a route of the host API, a command code or a route of the WMS contract, update the
reference pages in `docs/reference/`: a test in the host suite compares them with the code. Read
[what counts as a breaking change](docs/versioning-and-compatibility.md) first, and record the change
in the `Unreleased` section of [CHANGELOG.md](CHANGELOG.md).

### Continuous integration

The workflows in [.github/workflows/](.github/workflows/ci.yml) run on Windows for every pull request:
a locked restore, the build and `dotnet pack` (the packages are kept as artefacts, not published),
one job per test suite, the browser tests (`browser.yml`), the vulnerable-package check, the
Markdown link check, the SBOM check, the machine-readable contracts check (`tools/Wcs.ContractCheck`),
and, at the end of every job, a check that the working tree is still clean (`git status --porcelain`
empty): a build or test that rewrites a tracked file or leaves an unignored file behind fails. Each
job's commands can be run locally as they are written there.

### All the checks at once

[`tools/Test-PublicRelease.ps1`](tools/Test-PublicRelease.ps1) runs, on your clone, what a release
depends on, and ends with a summary table; it exits with code 1 when a mandatory check fails:

```powershell
pwsh -File tools/Test-PublicRelease.ps1                      # everything but the browser tests
pwsh -File tools/Test-PublicRelease.ps1 -IncludeBrowserTests # also the Playwright suite (README of the suite)
pwsh -File tools/Test-PublicRelease.ps1 -IncludeReleaseDryRun # also build and run the release archives
pwsh -File tools/Test-PublicRelease.ps1 -SelfTest            # only the tests of the script itself
```

It checks the .NET SDK against `global.json`; a clean working tree at the start (or
`-AllowDirtyWorkingTree`); the locked restore, a clean warning-free Release build and every test suite; the links,
the contracts and their examples, the SBOM; vulnerable packages (`-SkipVulnerabilityCheck` exists for
offline runs and prints a warning: never for a release); control and replacement characters in the
current documentation; one version in `Directory.Build.props`, `sbom/wcs.cdx.json`, `CHANGELOG.md`
and the docs; the community files; immutable GitHub Action pins and the trusted Gitleaks archive
digest; placeholders left in the documents; secrets (Gitleaks, with the repository's
`.gitleaks.toml`, from `-GitleaksPath`, `GITLEAKS_PATH` or the `PATH`; built-in patterns in any case);
personal or absolute local paths; and, at the end, that nothing it ran changed the working tree.
`-ReleaseTag vX.Y.Z` also checks the tag against the version and the dated `CHANGELOG.md` section,
makes Gitleaks mandatory and runs the complete archive dry-run. `-RepositoryUrl` checks the URL used
by `Directory.Build.props`, the README and the issue templates. It creates no tag, edits no tracked
file and creates no remote release. The `Release` workflow uses it to validate, build and smoke-test
the archives through the same `tools/New-WcsReleaseArtifacts.ps1` used by the local dry-run.

## Dependencies

- **Updates.** Dependabot ([.github/dependabot.yml](.github/dependabot.yml)) opens grouped pull requests
  once a week: the test tooling, the `Microsoft.*` and `System.*` packages, the other NuGet packages,
  and the GitHub Actions. They are reviewed and merged like any other pull request. Workflow actions
  stay pinned to their full commit SHA, with the human-readable release tag in a trailing comment.
  When Gitleaks is upgraded, obtain the Windows x64 archive digest from its signed-off release,
  update both `release.yml` and `Get-ApprovedGitleaksInstallScript` in
  `tools/Test-PublicRelease.ps1`, then run the self-test and the full release check. The Gitleaks
  installation remains the first step of the `release` job, on `windows-latest`, with the exact
  `-NoProfile` shell command enforced by the checker. Also update the trusted executable digest used
  by the tagged-release check; this second verification detects replacement of the extracted binary
  before it is run.
- **Lock files.** After changing a `PackageReference`, restore once (`dotnet restore Wcs.slnx`) and
  commit the lock files that changed with it. To regenerate all of them:
  `dotnet restore Wcs.slnx --force-evaluate`.
- **SBOM.** A change of packages also changes the SBOM: regenerate it with
  `pwsh -File sbom/New-WcsSbom.ps1 -Root . -Version <Version of Directory.Build.props>` and commit
  `sbom/wcs.cdx.json`; `sbom/Test-WcsSbom.ps1` (the CI check) says whether it is up to date.
  Third-party code copied into the repository is listed by hand in `sbom/embedded-components.json`
  and in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- **Consistent versions.** The packages of one family (`Microsoft.AspNetCore.*`,
  `Microsoft.Extensions.*`, `Microsoft.EntityFrameworkCore.*`) move together, to the same patch, in
  every project that uses them, and with the suites green. A difference that exists today is aligned
  in a pull request of its own, not as a side effect of another change.
- **Vulnerabilities.** CI fails when `dotnet list package --vulnerable --include-transitive` finds
  anything, direct or transitive.
- **Versions in the project files.** Package versions are written in each project file; the
  repository does not use central package management (`Directory.Packages.props`). The project files
  are shared with Sun Tech's internal repository, from which this one is exported, and moving every
  version out of them would make each project file differ between the two.

## Language and style

- **New code is in English**: identifiers, comments, log lines you add, test names. This applies to
  every project under `WCS/`.
- The codebase is still partly in Italian, and it is honest to say so up front. Identifiers are in
  English almost everywhere; many comments in the engine, the host and the driver layer, many host
  messages, and the historical notes under `docs/history/` are in Italian. Translating existing
  comments is welcome as a pull request of its own, not mixed with a functional change.
- Markdown links are checked by `tools/Wcs.LinkCheck` (`dotnet run --project tools/Wcs.LinkCheck/src -- .`): links to code use `path#L12`. The historical notes under `docs/history/` are neither checked nor edited.
- Documents are UTF-8: no control characters, replacement characters or broken encodings. `pwsh -File tools/Test-DocumentEncoding.ps1 -Path .` lists any it finds.
- Follow the style of the surrounding code. Nullable reference types are enabled; do not add warnings.
- Comments explain *why*, not *what*: the reason for a rule, the field case it answers, the trap it
  avoids.
- Keep the WCS/WMS boundary: the engine and the host talk to a WMS only through `Wcs.Wms.Contracts`,
  and to machines only through `Wcs.Connection.Interface`.

## Commits

- One logical change per commit, with a message that says what changes and why. Write messages in
  English, with a short first line.
- Do not commit generated files, local databases (`*.db`), `appsettings.Secrets.json`, keys,
  connection strings, customer data or plant names.

## Pull requests

- Work on a branch of your fork and open the pull request against `main`.
- One topic per pull request. Say what changes, why, and how you verified it (which suites, which
  manual steps; demo, test bench or real plant).
- A behaviour change comes with a test that fails without the change.
- Keep unrelated reformatting and renames out of the pull request: they hide the real diff.
- A change that touches how devices are commanded, how routes are chosen, or what is reported to the
  WMS must say how it was tried, and what it does to a plant already running: expect questions about
  field behaviour, and read [SAFETY.md](SAFETY.md).
- The [pull request template](.github/pull_request_template.md) asks for the motivation, the tests
  run, the effect on contracts, OpenAPI, documentation and SBOM, breaking changes, plant safety and
  confidentiality: fill it in, or say why a part does not apply.

## Reporting issues

Open a GitHub issue with one of the forms ([bug report](.github/ISSUE_TEMPLATE/bug_report.yml),
[feature request](.github/ISSUE_TEMPLATE/feature_request.yml)). A bug report asks for:

- the version or commit, the operating system and architecture, the .NET version;
- the component, where it happened (demo, test bench or production) and the field driver used;
- what you did, what you expected, and what happened instead;
- the relevant host log lines, sanitised, and, if the problem is about routing, a minimal plant (the
  drawing exported from the web editor) that reproduces it;
- whether a real machine moved, or failed to stop, unexpectedly.

Do not include customer data, plant names, personal data, API keys or connection strings. Blank
issues are disabled; vulnerabilities go through private vulnerability reporting
([SECURITY.md](SECURITY.md)), never in an issue.
