# Versioning and compatibility

This page says what a version number of the WCS promises: which parts of the WCS are covered, what
counts as a breaking change for each of them, and how something is deprecated before it goes. How a
version is tagged and published is in the [release process](release-process.md).

## The version number

The WCS follows [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html):
`MAJOR.MINOR.PATCH`, with a pre-release suffix `-preview.N` for previews.

- **One number for the whole repository.** The host, the contracts and clients, the driver contract,
  the web editor, `Wcs.DbTool` and the sample WMS are released together, from one tag, with the same
  version. Use the parts of one version together: a WMS adapter, a client program or a driver built
  against version X is meant for a host of version X (and, within the rules below, of later minor and
  patch versions of the same major).
- **Written in one place**: the `Version` property of `Directory.Build.props` at the root of the
  repository. Every project takes it from there; a project file never sets its own.
- **Where it shows**: the release tag (`v0.1.0-preview.1`), the release and its files, the package
  version when the projects are packed, and the assembly version `MAJOR.MINOR.PATCH.0`, which the host
  reports in the `version` field of `GET /api/v1/status` (`0.1.0.0` for `0.1.0-preview.1`).
- **The first version is `0.1.0-preview.1`**, a source-only preview: there are no NuGet packages, and
  the libraries are referenced from a clone or a submodule at a release tag
  ([README](../README.md#using-the-libraries-from-your-code)).

### Before 1.0

Semantic Versioning lets anything change while the major version is 0. This project narrows that:

- a **patch** (`0.1.0` to `0.1.1`) never breaks anything listed on this page;
- a **minor** (`0.1` to `0.2`) may break, and every breaking change is listed in
  [CHANGELOG.md](../CHANGELOG.md) under **Changed** or **Removed**, marked **Breaking**, with what to do;
- **previews** (`-preview.N`) of the same version carry no promise among themselves: read the changelog
  of each one.

From `1.0.0` on, a breaking change needs a new major version.

## What is versioned

| Surface | What it is | Identifier |
|---|---|---|
| **Host API** | the HTTP routes under `/api/v1` (methods, paths, bodies, status codes, the `X-Api-Key` rules) and the SignalR hub at `/hub/plant` (methods and events); the command codes of `POST /api/v1/commands` ([reference](reference/host-api.md), [commands](reference/commands.md)) | the `v1` path segment, and the WCS version |
| **API contracts and .NET client** | the public types of `Wcs.Api.Contracts` (routes, DTOs) and `Wcs.Api.Client` (`WcsClient`, `PlantEventsConnection`, `WcsClientOptions`), and `Wcs.Common`, which they share | the WCS version |
| **WMS contract** | the 43 HTTP routes the WCS calls on a WMS under `/api/wcs/warehouse`, as they travel on the wire, the status-code rules and the receipts ([the WMS contract](integration/wms-contract.md), [routes](reference/wms-routes.md)); the public types of `Wcs.Wms.Contracts` (`IWarehouse`, the route constants, the records) and `Wcs.Wms.Client` | the WCS version |
| **Driver contract** | the public types of `Wcs.Connection.Interface` (`IConnection`, `IConnectionFactory`, their events and completion codes) and the way the host loads a driver (`Wcs:ConnectionFactory`) ([connecting machines](integration/connecting-machines.md)) | the WCS version |
| **Configuration** | the settings of the host and their defaults ([configuration](reference/configuration.md)) | the WCS version |
| **Data files** | `wcs.db` and the monthly history and driver-log files of the data folder, and the backup folders of `Wcs.DbTool` | the schema migrations of each build |
| **Operations tools** | the command line of `Wcs.DbTool` and its exit codes | the WCS version |

The [machine-readable contracts](contracts/README.md) describe the host API and the WMS contract of a
version in a form tools can compare; where they and this page differ on what a change means, this page
decides, and the difference is a defect of the contracts.

**Not covered** (may change in any release, without notice):

- the public types of the engine, the host and the web editor that are not listed above: they are
  public for the host and the tests, not as a programming interface;
- the web editor's pages and their layout;
- the wording of messages and log lines (many are in Italian): program against status codes and
  fields, never against texts;
- **`Wcs.Viewer`, which is experimental**: it has no sample and no tests in this repository and may
  change or disappear in any release;
- the sample WMS and the sample plant, which are examples to copy, not dependencies;
- the historical notes under `history/`.

## What counts as a breaking change

### Host API and .NET client

Breaking:

- removing or renaming a route, an HTTP method, a hub method or a hub event; moving a route;
- removing or renaming a field of a request or an answer, changing its JSON name or casing, or its
  type (a number becoming a string, a single value becoming a list, a field becoming nullable in an
  answer);
- a new **required** field in a request, or an optional request field becoming required;
- refusing a request that used to be accepted (stricter validation), or a different status code for
  the same outcome;
- changing the number or the wire name of an enumeration value, or the meaning of a command code;
- changing what a key may do (for example a route the read-only key could call becoming full-key only);
- in `Wcs.Api.Contracts`, `Wcs.Api.Client` and `Wcs.Common`: removing or renaming a public type or
  member, changing a signature, or changing the parameters of a record's constructor.

Not breaking: a new route, hub event or command code; a new optional request field; a new field in an
answer or an event; a new value of an enumeration that the WCS sends; a new overload or member in the
client. **Clients must ignore unknown fields and tolerate unknown enumeration values**: that is what
lets these changes happen in a minor or patch version.

### WMS contract

The WMS serves these routes and the WCS calls them, so a change is judged from the adapter's side.
Breaking:

- a **new route** the WCS calls, or a route it calls in a new situation (every adapter has to implement
  it);
- a **route removed from the contract**: its constant and records leave `Wcs.Wms.Contracts`, and an
  adapter built on them no longer compiles;
- a field the WCS **sends** removed, renamed, or of a different type (a DTO field changing type is
  breaking in either direction);
- a field the WCS **expects** in an answer that is new and has no default when the WMS leaves it out, or
  an answer field that becomes required;
- a change in how the WCS reads a status code (for example a `4xx` that used to be retried now being
  parked), or in the rules of operation ids and receipts.

Not breaking: a new optional field in what the WCS sends (adapters ignore unknown fields); a new answer
field the WCS reads only when present; a route the WCS stops calling while it stays in the contract.

### Driver contract

A driver is compiled against `Wcs.Connection.Interface` and loaded by the host, so any change to the
existing contract is breaking: a member, event or parameter added to, removed from or changed in
`IConnection` or `IConnectionFactory` (a driver must implement every member); a different meaning of a
completion code or of an event; a change in how the host finds and creates the factory
(`Wcs:ConnectionFactory`). Not breaking: a new, separate interface that a driver may implement to offer
more.

### Configuration and commands

Breaking: a setting renamed or removed; a default changed in a way that changes behaviour; a setting
that was optional becoming required; a command code removed or renumbered. Not breaking: a new setting
whose default keeps the previous behaviour.

### Data files

A new schema migration is **not** breaking: the host applies it when it opens a file. What it costs is
the way back: a migrated file is not for older binaries, and `Wcs.DbTool` of an older version refuses a
backup with a newer schema, so a rollback goes through the backup taken before the upgrade
([upgrade and rollback](integration/backup-restore-and-disaster-recovery.md#upgrade-and-rollback)).
Breaking: a version that can no longer open, or migrate, the files written by an earlier released
version; a backup folder of an earlier released version that the new `Wcs.DbTool` can no longer verify
or restore.

## Deprecation

Something that is going to be removed or changed incompatibly is first **deprecated**, in a minor or
patch version:

1. it keeps working as before;
2. the [CHANGELOG](../CHANGELOG.md) lists it under **Deprecated**, with what replaces it and from which
   version it may go;
3. the reference pages say it, and in the .NET libraries the type or member is marked `[Obsolete]` with
   the same message;
4. it is removed no earlier than the next major version, or, before 1.0, the next minor version after
   at least one release that carried the deprecation.

A security fix may have to break something without a deprecation period; the changelog then says so and
why.
