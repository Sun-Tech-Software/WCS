# Sun Tech WCS

**A warehouse control system: the layer between the warehouse management system and the machines.**

> **Safety first.** The WCS is not a functional-safety system. It does not replace safety PLCs,
> emergency stops or interlocks, and machine safety must not depend on it. Read
> [SAFETY.md](SAFETY.md) before connecting it to any machine.
>
> **Version 0.1.0.** The first release after the public preview. It is **source-only**: build it
> from this repository; no NuGet packages are published yet
> ([using the libraries](#using-the-libraries-from-your-code)). Before 1.0 a minor version may
> change things in incompatible ways ([versioning](docs/versioning-and-compatibility.md)). Read the
> [known limitations](docs/known-limitations.md) before planning a production installation.

The warehouse management system (WMS) reasons about goods: codes, lots, orders, stock. The
machines reason about movement: start a motor, read a photocell. Between the two, someone has to
reason about **routes** — knowing that to take a pallet from the bay to the requested cell it must
go over that conveyor and then that stacker crane, and that if the crane is down there is another
way. That is the job of the WCS.

Two ideas shape everything else:

- **The plant is a drawing, not a program.** The points of the plant and the arrows that connect
  them are data, not code. The engine builds a graph from the drawing and computes routes on it.
  Adding a conveyor means drawing an arrow in the editor, not writing another `case`.
- **The route is recomputed at every hop.** A mission says "this load unit must get there", not
  through which stops. At every step the engine looks at the situation of the moment and picks the
  next hop, so a device that fails half-way does not leave the pallet holding a plan that has gone
  stale.

Sun Tech declares that the engine has run in **50 plants, with up to 386 machines at the same time**.
This is Sun Tech's statement, not a reproducible benchmark or a guaranteed limit: see
[Performance and sizing](docs/integration/performance-and-sizing.md) for how to measure a plant of yours.
Its rules were straightened out by reality rather than designed on paper; if something in the
engine looks odd, it is more likely to answer a real case than to be a mistake.

## Components

| Project | What it is |
|---|---|
| `WCS/Wcs.Engine` | **The engine**: the plant graph, missions, device states, route computation, persistence on SQLite |
| `WCS/Wcs.Host` | **The production executable**: hosts the engine, exposes the HTTP API and the SignalR event hub, serves the web editor |
| `WCS/Wcs.WebEditor` | The web editor (Blazor + a bundled [draw.io](https://github.com/jgraph/drawio)), served by the host at `/editor` |
| `WCS/Wcs.Api.Contracts`, `WCS/Wcs.Api.Client` | The API contract (DTOs and routes) and its typed client, with automatic hub reconnection |
| `WCS/Wcs.Wms.Contracts`, `WCS/Wcs.Wms.Client` | The contract the WCS declares towards the WMS (`IWarehouse`) and its HTTP implementation |
| `WCS/Wcs.Connection.Interface` | The contract towards the machines: `IConnection`, and `IConnectionFactory`, through which the host loads a field driver |
| `WCS/Wcs.Connection.Test` | The loopback driver: it simulates the PLCs in-process (no sockets, no database), so the whole WCS runs without machines |
| `WCS/Wcs.Viewer` | **Experimental**: a library that keeps a read-only copy of the engine state from the hub. It has no sample and no tests in this repository, is not covered by the compatibility rules, and may change or be removed |
| `WCS/Wcs.Common` | Shared vocabulary: enumerations and a few event types |
| `Common/Helper`, `Common/Log.Interface`, `Common/Log.Windows` | Small shared libraries (conversions, logging contract, Windows event log writer) |
| `samples/Wcs.SampleWms`, `samples/plant` | A runnable sample WMS that answers the WCS's contract, and a sample plant: the starting point of an integration |

Not included in this repository: Sun Tech's own PLC driver and its protocol, which are proprietary
(available from Sun Tech);
the legacy WinForms plant editor and the WinForms test bench of the Viewer (`Wcs.Viewer.Test`),
which depend on commercial UI toolkits; the internal tool that migrates plants from the database of
Sun Tech's WMS; and the internal demo scripts. The web editor replaces the WinForms editor. A plant
is drawn in the web editor or sent as JSON with `PUT /api/v1/plant/graph`, as the sample plant is
([Modelling a plant](docs/integration/plant-modelling.md#creating-a-plant)).

Out of the box the WCS drives every device through the loopback driver (connection type `Test`).
A driver for real machines implements `IConnection` and `IConnectionFactory` and is named in the
`Wcs:ConnectionFactory` setting of the host; without it, a device with connection type `Connected`
stays without connection (OffLine) and the host log says why.

## Quick start

Requirements: Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download), ports 5142 and 5580
free. The full walk-through, with every output, is in [docs/quick-start.md](docs/quick-start.md):
it runs the WCS against a sample WMS, loads a sample plant, stores a pallet and brings it back out.

This is a **demo**: one machine, the sample WMS and the loopback driver, nothing physical moves.
A test bench with real machines, and production, need what [SAFETY.md](SAFETY.md) and the
[production readiness checklist](docs/integration/production-readiness-checklist.md) describe.

```powershell
git clone https://github.com/Sun-Tech-Software/WCS.git
cd WCS

# terminal 1: the sample WMS on http://localhost:5142 (key sample-wms-key)
dotnet run --project samples/Wcs.SampleWms

# terminal 2: the WCS host on http://127.0.0.1:5580. It refuses the placeholder keys of its
# appsettings.json: give it a key of its own, and the sample WMS's key.
$env:Wcs__ApiKey = 'my-wcs-key'
$env:Wms__ApiKey = 'sample-wms-key'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project WCS/Wcs.Host/src

# terminal 3: load the sample plant, then open http://127.0.0.1:5580/editor (key my-wcs-key)
powershell -NoProfile -ExecutionPolicy Bypass -File ./samples/plant/load-sample-plant.ps1 -ApiKey my-wcs-key
```

Notes:

- `Development` matters: with `dotnet run` the web editor's static files are found only in the
  Development environment. For an installation use `dotnet publish`
  ([deployment](docs/integration/deployment-and-operations.md)).
- The engine does not need the WMS to load; without one answering at `Wms:ApiAddress` it logs every
  failed call and the load units that need the WMS wait. To run the API without the engine at all,
  add `--Wcs:StartEngine=false`.
- Keys belong in environment variables or in `appsettings.Secrets.json` next to the executable, never
  in `appsettings.json`. Start a published host from its own folder: from a console it reads its
  settings files, and serves the web editor, from the working directory. Every setting is in [docs/reference/configuration.md](docs/reference/configuration.md).

## Build and test

The .NET SDK is pinned by [global.json](global.json) (a later patch of the same feature band is
accepted), and every project restores against its committed `packages.lock.json`.

```powershell
dotnet build Wcs.slnx
dotnet test WCS/Wcs.Engine/tests/Wcs.Engine.Test/Wcs.Engine.Test.csproj
dotnet test WCS/Wcs.Host/tests/Wcs.Host.Test/Wcs.Host.Test.csproj
dotnet test WCS/Wcs.WebEditor/tests/Wcs.WebEditor.Test/Wcs.WebEditor.Test.csproj
dotnet run --project tools/Wcs.LinkCheck/src -- .
pwsh -File tools/Test-DocumentEncoding.ps1 -Path .
```

The engine tests run on in-memory SQLite and need nothing from the environment. The browser tests
(`Wcs.WebEditor.Browser.Test`) need Playwright browsers and a built host, and fail on purpose when
they are missing; see the README in that folder. Running `dotnet test` on the whole solution
therefore needs that setup first. More in
[CONTRIBUTING.md](CONTRIBUTING.md).

GitHub Actions runs the same commands on every pull request, on Windows
([`.github/workflows/`](.github/workflows/ci.yml)): a locked restore, the build and `dotnet pack`, each
test suite, the browser tests, the vulnerable-package check, the link check, the SBOM check, and a
check that the working tree is still clean afterwards.

## Using the libraries from your code

The WCS is distributed **as source only**. The libraries a WMS adapter or a client program
uses (`Wcs.Wms.Contracts`, `Wcs.Api.Contracts`, `Wcs.Api.Client`, `Wcs.Common`,
`Wcs.Connection.Interface`) are **not published on NuGet yet**; packages will come with a later
release. Until then the supported way is a `ProjectReference` to a copy of this repository **at a
release tag**, never at a moving branch:

- a clone checked out at a tag, next to your solution: `git clone --branch v0.1.0 https://github.com/Sun-Tech-Software/WCS.git`;
- or a git submodule of your repository, pinned to a tag:
  `git submodule add https://github.com/Sun-Tech-Software/WCS.git external/WCS`, then
  `git -C external/WCS checkout v0.1.0` and commit the submodule.

Then reference the projects you need, for example
`<ProjectReference Include="external\WCS\WCS\Wcs.Api.Client\src\Wcs.Api.Client.csproj" />`. Moving to
a new version means moving the clone or the submodule to the new tag and reading the
[changelog](CHANGELOG.md) and the [compatibility rules](docs/versioning-and-compatibility.md). The
guides show it for the [WMS contract](docs/integration/writing-a-wms-adapter.md) and for the
[.NET client](docs/integration/host-api.md#the-net-client-wcsapiclient).

## Documentation

Start from [docs/](docs/README.md): English guides for a developer whose company has a WMS and wants
to put this WCS under it, with real, runnable examples.

| Guide | What it covers |
|---|---|
| [Quick start](docs/quick-start.md) | the WCS, the sample WMS and the sample plant on one machine: store a pallet, extract it |
| [Overview and boundary](docs/integration/overview-and-boundary.md) | what the WCS does, what stays in the WMS, the pull model, the storing and extraction sequences |
| [How the WCS thinks](docs/how-the-wcs-thinks.md) | how the engine reasons about devices, routes and missions, and how a new machine is driven |
| [The WMS contract](docs/integration/wms-contract.md) | the 43 HTTP routes your WMS serves, as they travel on the wire: casing, ids, refusals, durable queues, receipts |
| [Writing a WMS adapter](docs/integration/writing-a-wms-adapter.md) | from the sample WMS to an adapter in front of your WMS, with a checklist and tests through the WCS's own client |
| [The host API](docs/integration/host-api.md) | `/api/v1`, keys, the edit lease, the SignalR hub, the .NET client |
| [Modelling a plant](docs/integration/plant-modelling.md) | devices, junctions, relocators, door mappings, linked cells, import and export |
| [Connecting machines](docs/integration/connecting-machines.md) | the driver contract, the loopback driver, a driver of your own, the completion codes |
| [Deployment and operations](docs/integration/deployment-and-operations.md) | publish, Windows service, data files, logs, upgrades, backups, security |
| [Troubleshooting](docs/integration/troubleshooting.md) | symptoms, messages and fixes |
| [Security and threat model](docs/integration/security-and-threat-model.md) | the expected network, keys and their rotation, TLS, logging and audit, threats |
| [Backup, restore and disaster recovery](docs/integration/backup-restore-and-disaster-recovery.md) | backing up and restoring the data, recovering a host |
| [Production readiness checklist](docs/integration/production-readiness-checklist.md) | from the test bench to production |
| [Known limitations](docs/known-limitations.md) | what the WCS does not do yet |
| [Supported platforms](docs/integration/supported-platforms.md) | what is tested where: Windows, Linux, x64, ARM64, browsers, .NET |
| [Performance and sizing](docs/integration/performance-and-sizing.md) | what drives CPU, memory and disk, and how to measure your plant |
| [Monitoring and alerting](docs/integration/monitoring-and-alerting.md) | health checks and scripts with exit codes for your monitoring system |
| [Publication checklist](docs/publication-checklist.md) | what is done, and what is left before a release is published |
| [Versioning and compatibility](docs/versioning-and-compatibility.md) | what a version number promises for the host, the API, the WMS contract and the driver contract |
| [Release process](docs/release-process.md) | how a version is tagged, built and verified, and how to upgrade |
| [Reference](docs/README.md#reference) | WMS routes, host API, command codes, configuration, glossary |

[`samples/`](samples/README.md) holds the runnable sample WMS and the sample plant the guides use.

The guides in `docs/` are the reference. The notes written for Sun Tech's internal repository, mostly
in Italian, are kept under [docs/history/](docs/history/README.md) as **historical notes**: they record how the
engine came to be as it is, may describe parts that are not published here or no longer exist, and
are not maintained as a reference.

The route-search tests run on an invented sample plant
(`WCS/Wcs.Engine/tests/Wcs.Engine.Test/Routing/Fixtures/sample-plant.json`), not on a customer's.

## Relation with Kazam WMS

The WCS was born inside **Kazam**, Sun Tech's warehouse management system, and was extracted from
it into a repository of its own. Kazam is source-available; the WCS is open source under the
Apache License 2.0. The WCS talks to a WMS only through its own contract (`IWarehouse`, in
`Wcs.Wms.Contracts`), realised over HTTP by `Wcs.Wms.Client`: no project here references the WMS
code, and a WMS that serves those routes can sit on the other side.

## Project documents

| Document | What it is |
|---|---|
| [SAFETY.md](SAFETY.md) | what the WCS is not, and what every installation with machines needs |
| [SECURITY.md](SECURITY.md) | supported versions and how to report a vulnerability (privately, never in an issue) |
| [SUPPORT.md](SUPPORT.md) | community support through issues, commercial support, drivers |
| [CONTRIBUTING.md](CONTRIBUTING.md) | build, tests, style, commits and pull requests |
| [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | the Contributor Covenant 2.1 |
| [CHANGELOG.md](CHANGELOG.md) | the changes of the project, version by version ([Semantic Versioning](docs/versioning-and-compatibility.md)) |

## Licence

Copyright 2026 Sun Tech Srl. Licensed under the [Apache License, Version 2.0](LICENSE). Third-party
components bundled in this repository, and their licences, are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (summary in [NOTICE](NOTICE)); a CycloneDX SBOM is in
[sbom/wcs.cdx.json](sbom/wcs.cdx.json).

## Contacts

Questions and bug reports: the issues of this repository ([SUPPORT.md](SUPPORT.md)). Vulnerabilities:
[SECURITY.md](SECURITY.md). Commercial support, Sun Tech's PLC driver and project integration: Sun Tech
Srl, <https://www.suntechonline.it>.

---

### In italiano

Il WCS di Sun Tech è il livello che sta fra il gestionale di magazzino e le macchine: sa com'è
fatto l'impianto e come portare un'unità di carico da un punto a un altro. L'impianto è un
disegno, non un programma, e la strada si ricalcola a ogni salto. Secondo Sun Tech il motore ha governato 50
impianti, fino a 386 macchine in contemporanea: è una dichiarazione di Sun Tech, non una misura riproducibile né un limite garantito. Le guide in inglese sono in [docs/](docs/README.md); le note
storiche del repository interno, quasi tutte in italiano, sono in [docs/history/](docs/history/) e non
sono un riferimento aggiornato. Il WCS non è un sistema di sicurezza funzionale: prima di collegarlo a
una macchina si legga [SAFETY.md](SAFETY.md). Il driver PLC di Sun Tech e il suo protocollo
sono proprietari e non sono in questo repository. Il codice è distribuito con licenza Apache 2.0.
