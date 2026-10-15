# Security and threat model

What the WCS host protects, what it trusts, what it does not do, and how to install it so that its
weak points are covered by the network around it. Every statement here describes the code of this
repository; where the host has no mechanism, the page says so.

**In one sentence:** the WCS host is meant to live on a segregated plant network, reachable only by a
few known systems; an API key alone is **not** adequate protection for a host exposed to the Internet,
and the host must never be exposed that way.

- [What is at stake](#what-is-at-stake)
- [The expected network](#the-expected-network)
- [Ports and flows](#ports-and-flows)
- [Trusted parties and the WCS/WMS boundary](#trusted-parties-and-the-wcswms-boundary)
- [Keys: how they work](#keys-how-they-work)
- [Rotating and revoking keys](#rotating-and-revoking-keys)
- [Transport: HTTP, HTTPS and a reverse proxy](#transport-http-https-and-a-reverse-proxy)
- [Logging and audit](#logging-and-audit)
- [Threats and what covers them](#threats-and-what-covers-them)
- [Demo, test bench and production](#demo-test-bench-and-production)

## What is at stake

Whoever holds the full key of a host can do everything the plant's operators can: start and stop the
engine, move load units, empty cells logically, mark steps done by hand, replace the plant. With a
real driver, that means **moving machines**. The data held by the WCS (plant layout, load unit ids,
mission history) is usually less sensitive than this ability to command.

The WCS is not a safety system and does not protect people from machines: see
[SAFETY.md](../../SAFETY.md).

## The expected network

```text
  operators' browsers            your systems (WMS, MES, screens)
          |  HTTPS                        |  HTTPS (or HTTP on a segregated LAN)
          v                               v
   [ reverse proxy or Kestrel HTTPS ] ----+
          |
   WCS host (Wcs.Host)  127.0.0.1:5580  -- HTTP(S), X-Api-Key -->  WMS adapter (Wms:ApiAddress)
          |
          +-- field driver (in-process) -- driver's own protocol -->  PLCs / gateways
          |
          +-- Data folder (SQLite: wcs.db, history, driver message log)
```

- The host, the WMS adapter and the PLCs sit on a plant network that is separated from the office
  network and from the Internet by a firewall. Only the systems in the table below reach the host.
- The field network (PLCs) is separate again; the host server is the only bridge to it, through the
  driver.
- Remote access for support goes through the company's own VPN or jump host, never through a port
  opened on the WCS.

## Ports and flows

| From | To | Port / path | Protocol | Authentication | Notes |
|---|---|---|---|---|---|
| your systems, viewers | WCS host | `Kestrel:Endpoints` (default `http://127.0.0.1:5580`), `/api/v1/...` | HTTP or HTTPS | `X-Api-Key` header (full or read-only) | a plain HTTP endpoint on anything but the loopback stops the host at start unless `Wcs:AllowInsecureRemoteHttp` is true |
| your systems, viewers | WCS host | `/hub/plant` | SignalR (WebSocket, or its fallbacks) | `X-Api-Key` header; `access_token` in the URL is refused | events only: the hub has no method a client can call |
| browsers | WCS host | `/editor`, `/_blazor`, `/_framework`, `/_content/Wcs.WebEditor`, `/_content/Microsoft.FluentUI.AspNetCore.Components` | HTTP(S), WebSocket (Blazor Server) | none to load the pages; the key is typed in the page | the key travels from the browser to the host inside the Blazor connection and stays on the server |
| anyone who reaches the port | WCS host | `/openapi/v1.json` | HTTP(S) | none | mapped in every environment except Production |
| web editor (inside the host) | WCS host | `Kestrel:Endpoints:Http:Url` (or `Https:Url`) | HTTP(S) | the key typed in the editor | the host calls itself; keep the loopback endpoint |
| WCS host | WMS adapter | `Wms:ApiAddress` + `/api/wcs/warehouse/...` | HTTP or HTTPS, as the address says | `X-Api-Key` = `Wms:ApiKey` | the 43 routes of the [WMS contract](wms-contract.md) |
| WCS host (driver) | PLCs, gateways | as the driver settings say (`plcAddress`, `pcmPort`, `localAddress`, `localPort`) | the driver's own | the driver's own | the loopback driver opens no socket |
| WCS host | disk | `Wcs:DatabaseDirectory` (default `Data` next to the executable) | file | file system permissions | SQLite files; keep them readable only by the service account and administrators |

No other port is opened by the host. There is no CORS policy, so a browser page from another origin
cannot call the API with a key.

## Trusted parties and the WCS/WMS boundary

- **The WMS adapter is trusted for warehouse decisions.** The WCS asks it which cell to use, what a load
  unit is and what to extract, and acts on the answers. A compromised or wrong adapter can send load
  units to wrong places; the WCS checks the plant's routes, not the WMS's intentions.
- **The WCS is trusted by the WMS for physical facts.** The facts it reports (allocated, released,
  extracted) are what happened on the field; the WMS records them and cannot veto them
  ([overview and boundary](overview-and-boundary.md)). An adapter must accept requests only with the key
  it expects in `X-Api-Key` and should listen where only the WCS host reaches it.
- **The field driver runs inside the host process**, with the host's rights. It is loaded from the
  host folder by the type named in `Wcs:ConnectionFactory`: anyone who can write that folder or that
  setting can run code as the host. Protect both like the executable itself.
- **Holders of the full key are trusted completely**; holders of the read-only key can read
  everything (plant, missions, logs, driver settings including PLC addresses) and receive events.

## Keys: how they work

| Key | Setting | Who sends it | Rights |
|---|---|---|---|
| Full | `Wcs:ApiKey` | your systems, the web editor (typed by a person) | everything |
| Read-only | `Wcs:ReadOnlyApiKey` (optional) | dashboards, viewers, monitoring | `GET` requests and the hub; any other method answers `403` |
| WMS | `Wms:ApiKey` | the WCS host, towards your adapter | whatever your adapter grants |

- Keys are compared in constant time. They are accepted only in the `X-Api-Key` header; a key in the
  URL (`access_token`) is refused with `401` and a warning in the host log, because URLs end up in logs.
- The host **does not start** while `Wcs:ApiKey` or `Wms:ApiKey` is empty or the placeholder of the
  shipped `appsettings.json`, or while the read-only key equals the full key or the placeholder.
- Keep keys in `appsettings.Secrets.json` next to the executable (never tracked, readable only by the
  service account and administrators) or in machine-level environment variables, never in
  `appsettings.json`, which is tracked and overwritten by `dotnet publish`.
- Use long random values (for example 32 random bytes, Base64-encoded), one per installation, and
  different keys for test benches and production.
- **Shared-key risk.** A key is shared by every person and program that uses it, and the host cannot
  tell them apart. A key that leaks from one screen is the key of the whole plant. Give programs that
  only read the read-only key, keep the full key to the few systems and people who must command, and
  rotate it when someone who knew it leaves.
- **No per-operator identity.** See [known limitations](../known-limitations.md#authentication-and-identity):
  the editor's edit lease names a browser session, not a person, and `userId`/`workstationId` in a
  command are written by the caller and not verified.

## Rotating and revoking keys

The host reads `Wcs:ApiKey`, `Wcs:ReadOnlyApiKey` and `Wms:ApiKey` **once, at start**. Editing
`appsettings.Secrets.json` while the host runs changes nothing until the next start. There is one
valid full key and at most one read-only key at a time: there is no overlap period, and no list of keys
to revoke one by one.

To rotate the full key:

1. Choose the new key; prepare it in every system that calls the host.
2. Stop the engine (command 1002) at a quiet moment, then stop the service.
3. Write the new key in `appsettings.Secrets.json` (or the environment variable).
4. Start the service; check `GET /api/v1/status` with the new key, and that the old one answers `401`.
5. Switch the callers to the new key; web editor sessions must enter it again.

The read-only key is rotated the same way. **Revoking** a key means rotating it: the old value stops
working at the restart. To revoke the read-only key without a replacement, remove `Wcs:ReadOnlyApiKey`
and restart.

`Wms:ApiKey` must change together with the key your adapter expects: change the adapter to accept the
new key (and, if it can, the old one for the duration of the switch), then restart the WCS host with
the new value. While the two disagree, every call to the WMS fails and is retried, and the facts wait
in the durable queues.

## Transport: HTTP, HTTPS and a reverse proxy

- **The default** is `http://127.0.0.1:5580`: the loopback only, so only programs on the same server
  reach it.
- **The host can serve HTTPS itself**, with the standard Kestrel configuration and a certificate you
  provide (`Kestrel:Endpoints:Https`, see [deployment](deployment-and-operations.md#network)). No
  certificate is shipped, and the host does not redirect HTTP to HTTPS or send HSTS headers.
- **A plain HTTP endpoint that is not the loopback stops the host at start**, unless
  `Wcs:AllowInsecureRemoteHttp` is true; then the host logs a `Critical` line at every start. Whoever
  reads that traffic reads the key and can command the plant: use it only on a segregated plant LAN,
  and prefer HTTPS there too.
- **Behind a reverse proxy** (IIS with ARR, nginx, or similar) the host keeps listening on the
  loopback and the proxy terminates TLS. The proxy must pass WebSocket upgrades for `/hub/plant` and
  `/_blazor` (the web editor), must forward the `X-Api-Key` header, and must not log request headers.
  The host does not configure forwarded-header processing (`X-Forwarded-*`); none of its decisions
  depends on the client's address or scheme.
- **Towards the WMS** the transport is the one in `Wms:ApiAddress`: use `https://` unless the adapter
  is on the same server or on a segregated LAN. Certificates are validated by .NET as usual.
- **Towards the PLCs** the transport is the driver's; most field protocols have no encryption, which is
  one more reason for a separate field network.

## Logging and audit

| What | Where | Who | Notes |
|---|---|---|---|
| start-up, configuration refusals, driver loading, engine warnings and errors, failed calls to the WMS, parked facts | host log: console and, on Windows, the Event Log (warnings and errors) | - | `Logging:LogLevel` |
| commands (`POST /api/v1/commands`) | `wcs.db`, `GET /api/v1/commands` | `userId`, `workstationId` as the caller wrote them | not verified |
| what the engine did with each load unit, commands requested, refusals | operators' log, monthly history files, `GET /api/v1/log` | as above | **any holder of the full key can also add lines** (`POST /api/v1/log`): the log is not tamper-evident |
| messages the driver chose to record | `wcs-plc-YYYY-MM.db`, `GET /api/v1/history/plc-messages` | - | kept 2 months |
| a key sent in the URL | host log, warning | - | |

What the host does **not** log: successful or failed authentications, the key used for a request, the
client address, plant edits with the identity of a person. If you need an access log, take it from the
reverse proxy (without headers). Keep in mind that history files are deleted after
`Wcs:HistoryMonthsToKeep` months.

## Threats and what covers them

| Threat | In the host | Must be covered by |
|---|---|---|
| someone on the network sends commands | full key required on every API route and the hub | network segregation, firewall rules allowing only known systems, HTTPS |
| key sniffed on the wire | HTTP refused on non-loopback endpoints unless explicitly allowed | HTTPS (Kestrel or proxy) |
| key guessed by brute force | constant-time comparison; **no rate limiting, no lockout, failed attempts not logged** | long random keys; a proxy or firewall that limits and logs |
| key leaked from a screen, a script or a person | one key for all, rotation needs a restart | the read-only key wherever possible; rotation procedure; secrets file permissions |
| an operator acts wrongly or maliciously | no identity, no roles | operating procedures, who knows the full key, physical access to the plant |
| a large request body exhausts memory | requests up to `Wcs:MaxRequestBodyMegabytes` (256 MB by default) | the network; lower the setting if your plant file is small |
| route map read from the OpenAPI document | not served in Production | run production hosts in the Production environment |
| malicious driver DLL or settings | loads the type named in the setting from the host folder | file permissions on the host folder and its settings files |
| a WMS adapter impersonated | the WCS sends `Wms:ApiKey` and validates TLS certificates when the address is `https` | HTTPS to the adapter, or the adapter on the same server |
| data folder copied or altered | none (SQLite files are not encrypted) | file system permissions, encrypted disk or backups as your policy says |
| the host exposed to the Internet | **nothing adequate** | do not do it; use a VPN |

## Demo, test bench and production

| | Demo | Test bench (commissioning) | Production |
|---|---|---|---|
| Environment | Development | a non-Production environment, or Production | Production |
| Endpoint | loopback | loopback, or HTTPS on the test network | loopback behind a proxy, or HTTPS; never HTTP outside a segregated LAN |
| Keys | any non-placeholder value | test keys, never the production ones | long random keys in the secrets file, rotated by procedure |
| WMS | the sample WMS, public key `sample-wms-key` | your adapter (test instance) | your adapter |
| `Wcs:AllowSimulatorForMissingConnections` | as needed | only while a driver is missing, never with people near machines that may move | refused: the host does not start with it |
| OpenAPI | served without a key | served without a key outside Production | not served |

The [production readiness checklist](production-readiness-checklist.md) collects these points.
