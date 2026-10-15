# Supported platforms

Which operating systems, processor architectures, browsers and .NET SDK each part of the WCS has been
run on for `0.1.0-preview.1`, and with what evidence. This page states what was **verified**; it is
not a promise of support. **Sun Tech has not yet published an official support matrix**: until it
does, nothing here is "officially supported", and the open points are listed in the
[publication checklist](../publication-checklist.md).

## How to read the matrix

| State | Meaning |
|---|---|
| **Tested** | the component's build and its test suite ran green on that platform, as described under [evidence](#evidence) |
| **Expected to work, not verified** | nothing in the code or its dependencies is known to prevent it, but nobody has run it there for this release |
| **Experimental** | runs, but is outside the compatibility rules and has no tests here |
| **Unsupported** | known not to work, or deliberately out of scope |
| **Not applicable** | the combination does not make sense (a library has no browser, a test project is not deployed) |

"Expected to work" is a reading of the code, not a test result: do not plan a production installation
on it without running the [test-bench checks](production-readiness-checklist.md) on that platform first.

## The matrix

| Component | Windows 11 x64 | Windows Server x64 | Linux x64 | Windows or Linux ARM64 | Browser | Notes |
|---|---|---|---|---|---|---|
| `Wcs.Host` (engine, API, hub) | Tested | Expected to work, not verified | Expected to work, not verified | Expected to work, not verified | Not applicable | runs as a Windows service with `UseWindowsService`; elsewhere as a plain process |
| `Wcs.WebEditor` (served by the host) | Tested, with the host | as the host | as the host | as the host | Chromium: Tested (Playwright). Edge and Chrome: Expected to work, not verified. Firefox, Safari: not verified | Blazor Server plus the draw.io bundle, running in the browser |
| `Wcs.DbTool` | Tested | Expected to work, not verified | Expected to work, not verified; `restore` needs `--assume-stopped` there | Expected to work, not verified | Not applicable | |
| `Wcs.Api.Client`, `Wcs.Api.Contracts` | Tested | Expected to work, not verified | Expected to work, not verified | Expected to work, not verified | Not applicable | `net10.0` libraries |
| `Wcs.Wms.Client`, `Wcs.Wms.Contracts` | Tested | Expected to work, not verified | Expected to work, not verified | Expected to work, not verified | Not applicable | `net10.0` libraries |
| `Wcs.Connection.Interface` | Tested (through the engine and host suites) | Expected to work, not verified | Expected to work, not verified | Expected to work, not verified | Not applicable | the contract only; a driver brings its own platform limits |
| `Wcs.Connection.Test` (loopback driver) | Tested (through the engine, host and end-to-end suites) | Expected to work, not verified | Expected to work, not verified | Expected to work, not verified | Not applicable | simulates the machines in the process |
| a field driver (Sun Tech's or yours) | depends on the driver | depends on the driver | depends on the driver | depends on the driver | Not applicable | see [field drivers](#field-drivers-and-native-libraries) |
| `Wcs.Viewer` | Experimental | Experimental | Experimental | Experimental | Not applicable | no tests, no sample; see [Wcs.Viewer](#wcsviewer) |
| bUnit tests (`Wcs.WebEditor.Test`) | Tested | Not applicable | Expected to work, not verified | Expected to work, not verified | none (bUnit runs no JavaScript) | |
| Playwright tests (`Wcs.WebEditor.Browser.Test`) | Tested, Chromium | Not applicable | Not verified (the suite starts `Wcs.Host.exe`; its README describes the Windows setup) | Not verified | Chromium only | needs a built host and `playwright.ps1 install chromium` |
| Sample WMS (`samples/Wcs.SampleWms`) and its tests | Tested | Not applicable (never for a plant) | Expected to work, not verified | Expected to work, not verified | Not applicable | demo and tests only |

**.NET SDK**: the one pinned by `global.json` at the root of the repository, `10.0.402`, with
`rollForward: latestPatch` (a later patch of the same feature band is accepted, previews are not).
The released binaries are framework-dependent and need the .NET 10 runtime (ASP.NET Core Runtime 10 for
the host and the sample WMS, .NET Runtime 10 for `Wcs.DbTool`) ([release process](../release-process.md)).

**Windows Server**: no Windows Server version has been run for this release, so none is listed as
Tested. The host uses nothing that Windows 11 has and Windows Server lacks, as far as the code shows,
but that is a reading, not a test.

## Evidence

| Evidence | What it covers | Kind |
|---|---|---|
| Local runs on Windows 11 Pro for Workstations, x64, .NET SDK 10.0.402, in October 2026, while this release was prepared | on a clean clone of the public repository: the build of every project, the suites of the engine, the host and the web editor (bUnit), and the quick start. On 2026-10-15: the suites of the clients and contracts, `Wcs.DbTool`, the link checker, the contract checker and the sample WMS end to end (with the monitoring scripts), and the browser suite with Chromium (284 tests) | observed on the local machine |
| GitHub Actions workflows ([`ci.yml`](../../.github/workflows/ci.yml), [`browser.yml`](../../.github/workflows/browser.yml), [`release.yml`](../../.github/workflows/release.yml)), every job on `windows-latest` | the same suites on every pull request and before a release | automatic, once the repository runs them on GitHub |
| The test suites themselves | what each suite asserts; a suite proves nothing about a platform it has not run on | automatic |

`windows-latest` is a label GitHub moves from one Windows Server image to the next; the image a run
used is printed at the top of its log. A green run there is evidence for that image only, and for the
x64 runners GitHub provides; it says nothing about Windows 11 on ARM64 or about Linux.

Until the workflows have run on the public repository, the only evidence is the local one. Who checks
the first runs, and where the results are kept, is in the [publication checklist](../publication-checklist.md).

## Portable .NET code and Windows components

Every project under `WCS/`, `samples/` and `tools/` targets `net10.0`, not `net10.0-windows`; the three
shared libraries under `Common/` (`Helper`, `Log.Interface`, `Log.Windows`) target both. The parts that touch Windows:

| Part | Windows-specific piece | Used by | Off Windows |
|---|---|---|---|
| `Wcs.Host` | `UseWindowsService` (service name `Wcs.Host`) | the host | does nothing: the host runs as a plain process |
| `Wcs.Host` | the ASP.NET Core Event Log provider, added by the default host builder on Windows | the host's warnings and errors | absent: the host logs to the console only |
| [`Common/Log.Windows`](../../Common/Log.Windows) | targets `net10.0` and `net10.0-windows10.0.19041.0`; writes the Windows event log with `System.Diagnostics.EventLog` | `Wcs.Viewer` only (the host does not use it) | the writer does not create or use `EventLog`; `WriteLog` still emits its debug trace |
| [`Common/Helper`](../../Common/Helper) | targets `net10.0` and `net10.0-windows10.0.19041.0`; an internal RSA helper uses `RSACryptoServiceProvider`, `CspParameters` and `WindowsIdentity` | the engine references the library; nothing in the WCS calls that helper | not reached |
| `Wcs.DbTool restore` | the check that no process holds the data files open | `restore` | not available: `restore` refuses unless `--assume-stopped` says the host is stopped |

The release check performs a clean build with warnings treated as errors. Windows-only calls in
`Common/Log.Windows` are guarded by `OperatingSystem.IsWindows()`; `Common/Helper`'s internal RSA
helper is compiled for its platform-specific target and is not reached by the WCS. A platform warning
therefore fails release validation instead of being accepted as known residue.

The SQLite engine is a native library (`e_sqlite3`, from `SQLitePCLRaw`). A framework-dependent
publish without a runtime identifier, as the release does, carries it for many platforms under
`runtimes/<rid>/native/`, among them `win-x64`, `win-arm64`, `linux-x64` and `linux-arm64` (observed
in the build output). That makes ARM64 and Linux possible; it does not make them tested.

## Field drivers and native libraries

The driver is the part most likely to tie an installation to one platform:

- **Sun Tech's driver** for its PLCs is proprietary and not in this repository. Its platforms are
  declared by Sun Tech, not by this page; ask before planning anything other than Windows x64.
- **A driver of your own** is loaded from the host folder through `Wcs:ConnectionFactory`
  ([connecting machines](connecting-machines.md#choosing-the-driver)). If it uses a vendor library
  (a PLC communication DLL, an OPC or fieldbus stack), that library decides: many are x64-only, some
  Windows-only, and an x64 native DLL does not load in an ARM64 process. Check every native dependency
  of the driver for the target architecture before choosing the hardware.
- **Emulation is not a test**: an x64 process running under emulation on Windows ARM64 is a different
  setup from a native ARM64 process, with its own timing. Neither has been run for this release.

## Wcs.Viewer

`WCS/Wcs.Viewer` keeps a read-only copy of the engine state from the hub. It is **experimental**: its
WinForms test bench is not published, so it has no tests and no sample here, and it is outside the
[compatibility rules](../versioning-and-compatibility.md). It writes its warnings to the Windows event
log through `Common/Log.Windows`. Use `Wcs.Api.Client` and its `PlantEventsConnection` instead
([known limitations](../known-limitations.md#wcsviewer)).

## Before running on another platform

A prudential recommendation, not a requirement of the code:

1. Build and run the suites on that platform (`dotnet test` for the engine, host, bUnit, clients and
   sample WMS; the browser suite if the web editor matters).
2. Run the [quick start](../quick-start.md) and the
   [backup and restore test](backup-restore-and-disaster-recovery.md) there.
3. With a field driver, run its connection-loss and reconnection tests on the target hardware.
4. Run the capacity test of [performance and sizing](performance-and-sizing.md) on the target
   hardware.
5. Keep the results: they are the evidence a future version of this matrix needs.
