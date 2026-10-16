# Publication checklist

The one list of what must be true, decided or tested before the WCS is published as open source and
its first version is released, and who can do it. It complements the [release process](release-process.md)
(how any version is tagged and built) with what only the **first** publication needs.

Each item belongs to one group:

- **A. Checked automatically**: a script or workflow in the repository checks it; someone runs it and
  reads the result.
- **B. To be confirmed by Sun Tech**: a fact or a text only Sun Tech can confirm.
- **C. Needs GitHub access**: settings and runs on the GitHub organisation and repository.
- **D. Needs PLC or real plant tests**: cannot be proven with the loopback driver.
- **E. Needs a commercial decision**.

An item is done when its box is ticked and the evidence (a run, a link, a signed note) is recorded next
to it. Nothing here is done by writing it in this file.

## A. Checked automatically

- [ ] **Build, tests, locked restore, packages**: the `CI` workflow
      ([`ci.yml`](../.github/workflows/ci.yml)) is green on the commit to tag; locally,
      `tools/Test-PublicRelease.ps1` runs the same checks with a summary table
      ([CONTRIBUTING](../CONTRIBUTING.md)). On Windows 11 x64 on 2026-10-15, the clean rebuild had
      zero warnings and zero errors and all 3,996 automated tests passed.
- [ ] **Browser tests**: the `Browser tests` workflow is green on the same commit (the `Release`
      workflow does not run them). Locally on Windows 11 x64 on 2026-10-15: 284 of 284 green with
      Chromium.
- [ ] **Links**: `dotnet run --project tools/Wcs.LinkCheck/src -- .` reports no broken link in the
      current documentation.
- [ ] **Document encoding**: `pwsh -File tools/Test-DocumentEncoding.ps1` exits 0 (no control
      characters, replacement characters or mojibake in any Markdown or JSON file); its own check is
      `-SelfTest`.
- [ ] **Machine-readable contracts**: `tools/Wcs.ContractCheck` finds the OpenAPI documents and their
      examples in line with the code ([contracts](contracts/README.md)).
- [ ] **SBOM**: `sbom/wcs.cdx.json` matches the restored packages (`sbom/Test-WcsSbom.ps1`).
- [ ] **Vulnerable packages**: none, direct or transitive (CI job `vulnerable-packages`).
- [ ] **Tag and version**: the tag is `vX.Y.Z` and matches `Version` in
      `Directory.Build.props`; the `Release` workflow refuses anything else.
- [ ] **Changelog**: [CHANGELOG.md](../CHANGELOG.md) has the dated
      `## [X.Y.Z] - YYYY-MM-DD` section; the `Release` workflow refuses an undated or empty
      section.
- [ ] **Release assets and checksums**: the `Release` workflow builds the three zips, the SBOM and
      `SHA256SUMS.txt`, starts the applications from the extracted archives, and attaches them to a draft release.
      The same dry-run is available locally with
      `tools/Test-PublicRelease.ps1 -IncludeReleaseDryRun`
      ([what a release is](release-process.md#what-a-release-is)). On Windows 11 x64 on 2026-10-15,
      the local dry-run built, extracted and started the host, editor, DbTool and sample WMS archives.
- [ ] **Monitoring scripts**: `MonitoringScriptsTest` in `samples/Wcs.SampleWms.Test` is green (part of
      the CI job `sample-wms-end-to-end`; it needs PowerShell 7, present on the GitHub Windows
      runners).
- [ ] **Secrets and personal paths**: `tools/Test-PublicRelease.ps1` finds none. Built-in patterns
      always run; Gitleaks scans the whole Git history and is mandatory when `-ReleaseTag` is used.
- [ ] **Workflow supply chain**: every external GitHub Action is pinned to a full commit SHA and the
      Gitleaks Windows x64 archive is checked against the trusted SHA-256 digest stored in the
      repository. `tools/Test-PublicRelease.ps1` rejects mutable action references, missing or stale
      digests, downloading the checksum from the same release as the binary, or changing the
      approved first-step/hosted-runner/`-NoProfile` execution boundary. For a tagged release it
      hashes the selected Gitleaks executable again immediately before use.

## B. To be confirmed by Sun Tech

- [ ] **Final repository name and URL.** `Directory.Build.props` uses
      `https://github.com/Sun-Tech-Software/WCS`; the README, the issue templates and the package
      metadata must use the final one (`tools/Test-PublicRelease.ps1 -RepositoryUrl` checks them).
- [ ] **Security contacts.** [SECURITY.md](../SECURITY.md) sends reports to GitHub's private
      vulnerability reporting: who at Sun Tech receives them, the expected response time Sun Tech is
      willing to state (none is stated today), and a fallback contact if GitHub is unavailable.
- [ ] **Commercial contacts.** The README and `SUPPORT.md` point to <https://www.suntechonline.it>: the
      page, address or form a company should use for support, the proprietary driver and integration
      work.
- [ ] **Officially supported platforms.** [Supported platforms](integration/supported-platforms.md)
      lists what was tested (Windows 11 x64) and what is only expected to work; Sun Tech decides what
      it will call supported, Windows Server versions included, and on which evidence.
- [ ] **Hardware requirements.** None are published. [Performance and sizing](integration/performance-and-sizing.md#what-sun-tech-must-fill-in-before-an-official-matrix)
      lists the measurements needed before a minimum or recommended configuration can be stated.
- [ ] **Field claims.** The README's statement that the engine has run 50 plants, with up to 386
      machines at the same time, is presented as Sun Tech's declaration; confirm the wording and the
      figures, or remove them.
- [ ] **The proprietary driver.** Its name, how it is obtained, its platforms and its versions, as
      the guides refer to it ([connecting machines](integration/connecting-machines.md)).
- [ ] **Legal review of third-party licences.** The items listed at the end of
      [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md), "Items requiring legal review": libavoid
      under LGPL-2.1 inside draw.io's minified bundle (keep, ship separately or remove), the EPL-2.0
      obligations for ELK, dagre's missing LEGAL file, components whose licence the bundle does not
      state, reconstructed licence texts, the draw.io stencil restriction, the font's licence. Signed
      off by a lawyer, with the decision recorded.
- [ ] **Safety approval.** [SAFETY.md](../SAFETY.md) reviewed and approved by the person responsible
      for machine safety at Sun Tech.
- [ ] **Historical notes.** The internal notes published under `docs/history/` reviewed for customer
      names, plant data and personal data.

## C. Needs GitHub access

- [ ] **Repository created** under the final organisation and name, public, with the description and
      topics; the default branch `main`.
- [ ] **Private vulnerability reporting enabled** (Settings, Security), so that the procedure in
      `SECURITY.md` works; test it with a dummy report from another account.
- [ ] **GitHub Actions enabled** for the repository, with the default token read-only except where a
      workflow asks for more (`release.yml` asks `contents: write`).
- [ ] **First runs checked**: `CI` and `Browser tests` green on `main`; the runner image printed at the
      top of the logs recorded as the evidence for [supported platforms](integration/supported-platforms.md#evidence).
- [ ] **Branch protection on `main`**: pull requests required, the CI jobs and the browser tests as
      required checks, no force-push, no deletion; who may approve.
- [ ] **Dependabot** (`.github/dependabot.yml`) alerts and security updates enabled.
- [ ] **Tag pushed and the draft release reviewed**: the three zips, the SBOM and `SHA256SUMS.txt`
      present; the notes are the changelog section; marked pre-release.
- [ ] **Checksums verified on a download** from the draft, as in
      [verifying a download](release-process.md#verifying-a-download), before publishing it.
- [ ] **Anonymous clone**: from a machine and an account with no access to Sun Tech's systems,
      `git clone` the public URL and build it with the SDK of `global.json`; no submodule, private feed
      or credential may be needed.
- [ ] **Quick start from the release**: on a clean Windows machine with only the .NET 10 runtimes,
      follow the [quick start](quick-start.md) with the release's zips (host and sample WMS) instead of
      a build from source, and record the result.
- [ ] **Issue templates and labels** in place; the issue tracker and discussions set as `SUPPORT.md`
      describes.

## D. Needs PLC or real plant tests

The loopback driver proves that the WCS's logic runs; it proves nothing about machines or safety
([SAFETY.md](../SAFETY.md)).

- [ ] **Browser tests on the operators' browsers**: the web editor on the browsers and screens the
      plant's operators will use (only Chromium is tested automatically).
- [ ] **Restore test**: a backup of a running installation restored on another machine, following
      [backup, restore and disaster recovery](integration/backup-restore-and-disaster-recovery.md), and
      the restored host checked before it moves anything.
- [ ] **Test-bench test**: with Sun Tech's driver (or a third-party one) and real PLCs, every movement
      type, every completion code the engine reacts to, connection loss and reconnection
      ([connecting machines](integration/connecting-machines.md)); the
      [production readiness checklist](integration/production-readiness-checklist.md) walked through.
- [ ] **Capacity test** on representative hardware, as in
      [performance and sizing](integration/performance-and-sizing.md#measuring-a-real-plant), including
      the field message rate and the PLC log growth, which the loopback driver cannot produce.
- [ ] **Monitoring on a service**: the event source the host uses when it runs as a Windows service,
      and the restart detection of `Test-WcsEvents.ps1`, checked on a server where the host is
      installed as a service ([monitoring](integration/monitoring-and-alerting.md)).

## E. Needs a commercial decision

- [ ] **Publication date** and who announces it.
- [ ] **What Sun Tech offers around the open-source WCS**: support terms, the proprietary driver's
      licence and price, integration services, and whether any of it is mentioned in the README.
- [ ] **Support commitment**: whether issues get a response time, and how long each version line is
      maintained.
- [ ] **NuGet packages**: whether and when the contracts and clients are published as packages, under
      which owner (today the WCS is source-only).
- [ ] **Signed binaries**: whether releases are code-signed (today they are not, and the checksums do
      not prove who built them).
- [ ] **Trademarks**: use of the names "Sun Tech" and "WCS" by forks, and any trademark notice.
