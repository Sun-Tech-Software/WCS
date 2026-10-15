# Wcs.WebEditor.Browser.Test — the tests that see draw.io

These are the **only** automated tests that really run JavaScript: the tests of
`Wcs.WebEditor.Test` run on bUnit, which does not execute JavaScript, so everything they know
about what draw.io sends back is a document we wrote by hand. Here the browser is real, draw.io
runs, and the host is a real process with its own database.

## `dotnet test` on the whole solution turns red here, on purpose

These tests **do not skip themselves** when the environment is missing. A test that skips in
silence is worse than a missing test: nobody notices that it no longer tests anything, and the
green keeps coming. When something is missing, the failure says **what** and **how** to fix it.

Two things are needed, once:

1. **Playwright's browsers.** The script lands next to the tests once the project has been
   built:

   ```powershell
   pwsh "WCS\Wcs.WebEditor\tests\Wcs.WebEditor.Browser.Test\bin\Debug\net10.0\playwright.ps1" install chromium
   ```

2. **An already built `Wcs.Host`**, named by `WCS_HOST_EXE`. The host is deliberately not rebuilt
   from here: on a developer's machine it is often running and its files are in use.

   ```powershell
   dotnet build WCS\Wcs.Host\src\Wcs.Host.csproj -c Debug
   $env:WCS_HOST_EXE = "$PWD\WCS\Wcs.Host\src\bin\Debug\net10.0\Wcs.Host.exe"
   dotnet test WCS\Wcs.WebEditor\tests\Wcs.WebEditor.Browser.Test\Wcs.WebEditor.Browser.Test.csproj `
       --logger "console;verbosity=detailed"
   ```

   The verbosity is not a whim: it is how you read the line that states **which binary** the
   tests ran on, with its date.

## Rebuild the host first, always

This project has no `ProjectReference` to `Wcs.WebEditor`: it drives a process that is already
built, so MSBuild does not tie it to those sources and rebuilds nothing. Measured: with a line of
`DrawioDelta.cs` switched off and the tests run without rebuilding the host, they stayed green.
`ProductBinary` now refuses to start when a source of the editor is newer than the binary that
should carry it, and says which one.

There are **two** reference points, because the sources do not all end up in the same place:

| What changes | Compared with | What the rebuild moves |
|---|---|---|
| `.cs`, `.razor` | `Wcs.WebEditor.dll` next to the executable | recompiles the library |
| `.js`, `.css` | `Wcs.Host.staticwebassets.endpoints.json` next to the executable | rewrites the manifest |

A changed `.js` does **not** recompile the library: comparing it with the library gave a refusal
that the prescribed rebuild did not clear, a dead end whose only exit was loosening the check.
Measured: after touching `wwwroot/drawio-bridge.js`, a `dotnet build ... -c Debug` rewrites the
manifest and leaves the `.dll` where it was.

The tests that start nothing — `ProductBinaryTest`, `WcsHostProcessTest` — run anywhere and
without `WCS_HOST_EXE`: they are the safety net of this safety net.
