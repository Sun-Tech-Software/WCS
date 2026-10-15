<!--
Thank you for contributing. Read CONTRIBUTING.md first; a pull request is reviewed and merged at
Sun Tech's discretion. Security problems are never reported in a pull request: use private
vulnerability reporting (SECURITY.md). Delete the parts of this template that do not apply and
say why, rather than leaving them empty.
-->

## Motivation

<!-- What problem this solves, and the issue it closes (Closes #123), if any. -->

## Changes

<!-- What changes, in a few points. One topic per pull request: keep unrelated reformatting and renames out. -->

## Tests run

<!-- Which suites you ran (for example Wcs.Engine.Test, Wcs.Host.Test, the browser tests), with what result, and any manual step:
     on a demo, a test bench or a real plant. A behaviour change comes with a test that fails without it. -->

- [ ] The projects this change touches build without errors or new warnings.
- [ ] The test suites that cover them are green.

## Contracts and documentation

- [ ] **Contract impact**: none / described below. The host API, the WMS contract, the driver contract
      (`Wcs.Connection.Interface`), the configuration keys and the data files are contracts
      ([versioning and compatibility](https://github.com/Sun-Tech-Software/WCS/blob/main/docs/versioning-and-compatibility.md)).
- [ ] **OpenAPI**: `docs/contracts/` regenerated and checked with `tools/Wcs.ContractCheck` when a route,
      a command code or a model of the host API or the WMS contract changed.
- [ ] **Documentation**: the guides and the reference pages in `docs/` updated, and the change recorded
      under `Unreleased` in `CHANGELOG.md`.
- [ ] **SBOM**: `sbom/wcs.cdx.json` and the lock files regenerated when packages changed
      (`sbom/Test-WcsSbom.ps1` passes).

## Compatibility and breaking changes

<!-- Does anything that integrations, adapters, drivers or installations rely on change or disappear?
     If so, say what breaks and what users have to do. Write "none" if nothing does. -->

## Plant safety

<!-- Required when the change touches how devices are commanded, how routes are chosen, what is
     reported to the WMS, or the manual commands. Say how it was tried and what it does to a plant
     already running. The WCS is not a functional-safety system (SAFETY.md). -->

- [ ] This change does not make the WCS, or any documentation, rely on the WCS for the safety of people
      ([SAFETY.md](https://github.com/Sun-Tech-Software/WCS/blob/main/SAFETY.md)).

## Confidentiality

- [ ] This pull request contains no API keys, passwords, tokens, connection strings, personal data,
      customer or plant names, or proprietary information (including any part of Sun Tech's
      proprietary PLC driver or protocol), in the code, the tests, the fixtures or this description.
- [ ] I have the right to submit this contribution under the Apache License 2.0
      ([CONTRIBUTING.md](https://github.com/Sun-Tech-Software/WCS/blob/main/CONTRIBUTING.md)).
