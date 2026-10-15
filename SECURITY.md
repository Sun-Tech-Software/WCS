# Security policy

## Supported versions

The repository is an **initial public preview**; its first version is `0.1.0-preview.1`
([versioning](docs/versioning-and-compatibility.md)). Security fixes are made on the default branch
(`main`) and come out in the next version; earlier versions are not patched. Older commits, forks and
copies modified by others are not supported.

| Version | Supported |
|---|---|
| `main` and the latest release | yes |
| earlier releases, anything else | no |

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a vulnerability.**

Report it privately through GitHub: on this repository, open the **Security** tab and choose
**Report a vulnerability** (GitHub's private vulnerability reporting). The report is visible only to
you and the maintainers, and the conversation about it stays private unless the maintainers publish a
security advisory.

Please include:

- the component (see the scope below) and the commit you tested;
- what an attacker can do, and what they need first (network access, a key, a read-only key, access
  to the server, a malicious WMS or driver);
- the steps to reproduce it, with the configuration that matters (environment, endpoints, settings;
  **never real keys**);
- a proof of concept if you have one, kept as small as possible;
- whether you found it on a demo, a test bench or a production plant, without naming the plant or the
  customer;
- how you would like to be credited, if at all.

Do not test against plants or systems you do not own or are not authorised to test. Do not include
customer data, plant names, keys or connection strings in the report.

The maintainers at Sun Tech Srl review reports and decide on fixes and their timing at their
discretion; this policy does not promise response or fix times.

## Scope

In scope, as published in this repository:

- the WCS host (`WCS/Wcs.Host`): the HTTP API, the SignalR hub, authentication with keys, transport
  checks, configuration handling;
- the web editor (`WCS/Wcs.WebEditor`), served by the host at `/editor`;
- the engine (`WCS/Wcs.Engine`) and its persistence;
- the contracts and clients (`WCS/Wcs.Api.Contracts`, `WCS/Wcs.Api.Client`,
  `WCS/Wcs.Wms.Contracts`, `WCS/Wcs.Wms.Client`, `WCS/Wcs.Connection.Interface`);
- the loopback driver (`WCS/Wcs.Connection.Test`);
- the sample WMS (`samples/Wcs.SampleWms`). It is meant for demos and tests only, with a public key:
  that key and its in-memory storage are by design, not vulnerabilities.

Out of scope:

- Sun Tech's proprietary PLC driver and protocol, which are not in this repository; and any other
  field driver not published here;
- third-party components bundled in the repository (for example draw.io in the web editor), unless
  the problem comes from how the WCS uses them: report the rest to their projects
  ([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md));
- installations that ignore the documented requirements, for example a host exposed to the Internet,
  `Wcs:AllowInsecureRemoteHttp` on an untrusted network, or keys stored in tracked files;
- the documented limits of the preview, such as shared keys without per-operator identity and the
  absence of rate limiting ([known limitations](docs/known-limitations.md)): suggestions to improve
  them are welcome as ordinary issues.

## Safety is a separate matter

The WCS is not a functional-safety system. A problem that can put people at risk because a plant
relies on the WCS for safety is a problem of that installation: see [SAFETY.md](SAFETY.md).

## Hardening

How the host is meant to be installed, its network flows, keys, TLS and audit are described in
[security and threat model](docs/integration/security-and-threat-model.md).
