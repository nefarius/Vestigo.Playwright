# Upstream findings for `@playwright/mcp` 0.0.85

These notes record what was verified before the plugin implemented a path. They are not a live two-browser experiment. Isolation behavior below comes from the published package and its pinned `playwright-core` bundle. A headed Chrome/Edge run, a Windows service run, and attachment to an existing browser are separate and are not claimed here.

## Pinned package

| Item | Value |
| --- | --- |
| Package | `@playwright/mcp` 0.0.85 |
| Integrity | `sha512-bSfECMbLXhJdWkGhLzNMxTZuazG+9IDJ679gflji3yyD94e7ARnGdqmJmG6lTPB+gYX+OiFLmX5myBl62+wZEw==` |
| SHA-1 | `9319f03746cd67a9cb9de1fbfd3652f66596717f` |
| Node | `>=18` |
| Runtime dependency | `playwright` and `playwright-core` `1.66.0-alpha-2026-11-09` |

The released npm package depends on an alpha `playwright-core` build. The plugin does not float that dependency and does not install it when Vestigo starts. A backend payload must be produced at release time with a lockfile.

The package is a thin CLI (`cli.js`) over `playwright-core/lib/coreBundle`. The inspected bundle matches the current Playwright MCP program on these points:

- `useSharedBrowser = config.sharedBrowserContext || config.browser.isolated`
- `--isolated` uses one browser and `browser.newContext()` per HTTP client
- `--shared-browser-context` uses the first existing context for every client
- the default persistent mode gives each client the same user-data directory, which the browser locks; it is not safe for concurrent clients
- stdio serves one client per process
- paths other than `/sse` use Streamable HTTP; `/sse` is legacy SSE. The listen text includes `Listening on ${url}` and a client URL of `${url}/mcp`
- extension and CDP attachment share the existing browser
- `--ignore-roots` uses the working directory when the client cannot answer roots
- headed is the default; `--headless` opts out
- browser values include `chrome` and `msedge`
- `--allowed-origins` is semicolon-separated and does not confine redirects
- `--no-sandbox` exists and is not a default
- screenshots can be inline images; some artifacts are files under `--output-dir`

## What the plugin can do with API 1.1

API 1.1 forwards tools. It does not proxy MCP resources, roots, sampling, elicitation, or callbacks. The plugin does not advertise those capabilities. Navigate, click, type, snapshot, and inline screenshots do not need another host API. Resource links are replaced with an explanation, and a text block that is only an absolute path or `file:` URI keeps the original text and adds a note that API 1.1 does not proxy that file. That is a documented gap, not a missing Vestigo contract. No host contract change is proposed.

Vestigo returns one unpaginated catalog and asks the provider again before each call. It does not push `tools/list_changed`. Clients must list again. The plugin aggregates upstream pages itself (at most 50) and returns unprefixed descriptors. The host adds the configured prefix.

## Isolation chosen from those findings

Managed mode starts one owned child per application identity. Stdio is the default because it has no listener. Ephemeral profiles pass `--isolated`. Persistent profiles use a hashed directory and an exclusive `.vestigo.lock`; lock failure refuses the session. The plugin never falls back to a shared browser.

External mode cannot see the upstream flags. `PerSession` means the administrator asserts that the endpoint was started with `--isolated` and without `--shared-browser-context`, extension, or CDP. `Exclusive` leases that endpoint to one Vestigo identity. `ShareBrowser` is off unless `AllowSharedBrowser` is set, and it collapses every identity onto one session. Attachment (`Cdp` or `Extension`) is managed-only and also requires `AllowSharedBrowser`.

Separate HTTP connections do not imply separate browser contexts. Sharing a Vestigo bearer token and the same application session ID deliberately selects the same identity.

## Desktop and service

Managed headed mode is rejected when this process is in Windows session 0. A Windows service cannot show a browser on the logged-on user's desktop. The supported visible-browser deployment is an external Playwright MCP endpoint already running as that user. The plugin does not impersonate and does not launch a service-to-desktop browser.

On this development machine Edge is installed (`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`). Chrome was not found under `Program Files\Google`. Node was `v24.22.1`. `npm.ps1` is blocked by execution policy; `npm.cmd` works.

## Host result types

The published-host test against Vestigo API 1.1 preserved upstream text, an inline image, structured content, and `IsError`. Progress tokens and `vestigo/sessionId` are removed before the upstream call. Other metadata is forwarded. Provider exceptions are sanitized by the host, so operational failures are returned as intentional tool results. After a connected session is lost, that identity is tombstoned and the failed action is not replayed. A new application session ID is required.
