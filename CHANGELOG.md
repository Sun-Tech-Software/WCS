# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html) as described in
[docs/versioning-and-compatibility.md](docs/versioning-and-compatibility.md). How a version is
released, and how its section here becomes the text of the GitHub release, is in
[docs/release-process.md](docs/release-process.md).

## [Unreleased]

### Fixed

- Engine: the second extraction trigger point selects the storing missions coming from its own
  group; it compared against the first point's group, so with only the second point loaded every
  storing mission was received.
- Engine: a loaded source whose load unit has no route yet no longer hides the sources after it
  from the device that receives from them.
- Engine: updates and removals of commands and extraction queue rows are announced to the event
  hub only after the save succeeds, as insertions already were.
- Host: `PUT /api/v1/plant/graph` with a null list or a null entry, and
  `PUT /api/v1/items/geometry` with a null entry, answer 400 instead of 500.
- `Wcs.Api.Client`: a `BaseAddress` that is not a bare http(s) origin is refused when the client
  is built; a successful response with a `null` body is a `WcsApiException` naming the request.
- `Wcs.DbTool`: `verify` and `restore` refuse a manifest whose file names are not bare WCS
  database names, are listed twice, or whose format is unknown, before touching any file.

### Changed

- Host: a warning at startup when `Wcs:ApiKey` or `Wcs:ReadOnlyApiKey` is shorter than 32
  characters.
- Engine: transit times (`GET /api/v1/log/transit-times`) read each month once, with one query
  instead of three: about four times faster on a month of 200,000 log rows.
- Engine: a group's loading decision reads the load units ready to load once, instead of up to
  four times.
- `Wcs.Api.Client` disposes every HTTP response; the host compares API keys without allocating.

## [0.1.0-preview.1] - 2026-10-15

### Added

Initial public preview of the Sun Tech WCS, extracted from Sun Tech's internal repository and
published under the Apache License 2.0. It contains:

- **The engine** (`WCS/Wcs.Engine`): the plant graph built from the plant's devices and junctions,
  missions and device states, the route recomputed at every hop, persistence on SQLite (`wcs.db`,
  monthly history files, the driver's message log) with schema migrations applied when a file is
  opened, and four durable queues that deliver the physical facts to the WMS at least once, with
  operation ids for receipts.
- **The host** (`WCS/Wcs.Host`): the executable that runs the engine, as a console application or a
  Windows service; the HTTP API under `/api/v1` and the SignalR event hub at `/hub/plant`; a full key
  and an optional read-only key in the `X-Api-Key` header; the edit lease for plant changes; refusal
  to start with placeholder keys, or with a plain HTTP endpoint off the loopback unless explicitly
  allowed.
- **The web editor** (`WCS/Wcs.WebEditor`), served by the host at `/editor`: Blazor Server with a
  bundled draw.io, to draw and configure the plant, link devices to the WMS's cells, and operate the
  engine.
- **The contracts and clients**: `Wcs.Api.Contracts` and `Wcs.Api.Client` (the host API and its typed
  .NET client), `Wcs.Wms.Contracts` and `Wcs.Wms.Client` (the 43 HTTP routes the WCS calls on a WMS,
  `IWarehouse`), `Wcs.Connection.Interface` (the field-driver contract, `IConnection` and
  `IConnectionFactory`) and `Wcs.Common`. `Wcs.Viewer` (a read-only copy of the engine state from the
  hub) is included as **experimental**: no sample, no tests here, outside the compatibility rules.
- **The loopback driver** (`WCS/Wcs.Connection.Test`), which simulates the machines in the process, and
  the loading of a field driver of your own through the `Wcs:ConnectionFactory` setting.
- **Fail-closed connections**: a device of connection type Connected without its connection makes the
  engine refuse Run and goes OffLine, instead of being served by the simulator. The opposite
  behaviour exists only behind `Wcs:AllowSimulatorForMissingConnections`, which the host refuses in
  the Production environment.
- **A sample WMS and a sample plant** (`samples/`): a runnable in-memory WMS that answers the WCS's
  contract, a plant on the loopback driver, the script that loads it, and their tests.
- **English guides** (`docs/`): quick start, overview and boundary, the WMS contract, writing a WMS
  adapter, the host API, plant modelling, connecting machines, deployment and operations,
  troubleshooting, security and threat model, production readiness, known limitations, and a
  reference of routes, commands, configuration and terms.
- **Test suites** for the engine, the host, the web editor (bUnit, and Playwright in a real browser),
  the contracts and clients, and the sample WMS.
- **Build and release tooling**: the .NET SDK pinned by `global.json`, a committed
  `packages.lock.json` per project with locked restores in CI, the version of the WCS in
  `Directory.Build.props`, GitHub Actions workflows (build and pack, the test suites, the browser
  tests, vulnerable packages, Markdown links, the SBOM, a clean working tree; tagged releases as
  drafts with SHA-256 checksums), immutable commit pins for third-party GitHub Actions, a trusted
  in-repository digest for the Gitleaks release binary, and Dependabot for NuGet packages and GitHub
  Actions.
- **Source-only distribution**: no NuGet package is published for this version; the libraries are
  referenced from a clone or a submodule at a release tag (README, "Using the libraries from your
  code").
- **Project documents**: [SAFETY.md](SAFETY.md), [SECURITY.md](SECURITY.md), [SUPPORT.md](SUPPORT.md),
  [CONTRIBUTING.md](CONTRIBUTING.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), third-party notices
  and a CycloneDX SBOM.

### Not included

Sun Tech's proprietary PLC driver and its protocol; the legacy WinForms plant editor and the WinForms
test bench of the viewer; the internal tool that migrates plants from Sun Tech's WMS database; the
internal demo scripts.

### Known limitations

See [docs/known-limitations.md](docs/known-limitations.md).

[Unreleased]: https://github.com/Sun-Tech-Software/WCS/compare/v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/Sun-Tech-Software/WCS/releases/tag/v0.1.0-preview.1
