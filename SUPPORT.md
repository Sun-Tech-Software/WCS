# Support

## Community support: GitHub issues

Questions, bug reports and suggestions go in the **issues** of this repository. Support there is a
community effort: the maintainers at Sun Tech Srl and other users answer when they can, and there is
**no guaranteed response time** and no guarantee that a question will be answered or a bug fixed.

Before opening an issue:

- read the [documentation](docs/README.md), in particular [troubleshooting](docs/integration/troubleshooting.md)
  and the [known limitations](docs/known-limitations.md);
- search the existing issues.

A useful issue says what you did, what you expected and what happened; the commit you used, the
operating system and the .NET SDK version; whether it was a demo, a test bench or a production plant;
the relevant host log lines; and, for routing problems, a minimal plant exported from the web editor.
**Never include** API keys, connection strings, customer data or plant names.

## Vulnerabilities

Not in issues: report them privately as described in [SECURITY.md](SECURITY.md).

## Safety

The WCS is not a functional-safety system: read [SAFETY.md](SAFETY.md). The issues are not a channel
for safety incidents on a plant; those belong to the plant's own organisation and its suppliers.

## Commercial support, proprietary components and project integration

For any of the following, contact Sun Tech: <https://www.suntechonline.it>.

- commercial support for an installation of the WCS;
- Sun Tech's own PLC driver and its protocol, which are proprietary and **not** in this repository;
- the integration of the WCS into a plant or with a WMS.

Nothing in this repository is a commitment to provide them, or to provide them on particular terms.

## Drivers

| Driver | Where | Support |
|---|---|---|
| Loopback driver (connection type Test) | `WCS/Wcs.Connection.Test` | community, through issues |
| Sun Tech's PLC driver | not in this repository | contact Sun Tech |
| A driver of your own | your code | yours; questions about the driver contract (`IConnection`, `IConnectionFactory`) are welcome in issues |

## Supported versions

Only the **latest release** (today `0.1.0`) and the default branch, `main`, are supported, in the
sense above: fixes come out in the next version, earlier versions are not patched, and there is no
long-term support for any commit.
