# Vestigo.Playwright

[![CI](https://github.com/nefarius/Vestigo.Playwright/actions/workflows/ci.yml/badge.svg)](https://github.com/nefarius/Vestigo.Playwright/actions/workflows/ci.yml)

Optional Vestigo plugin that forwards the official `@playwright/mcp` tools through Vestigo's authenticated `/mcp` endpoint. Playwright, Node.js, the browser, and the upstream MCP connection stay in this plugin. They are not part of Vestigo.

The module name is `Playwright`. It is disabled until an administrator enables it. Tool names use the prefix `playwright_` unless `Prefix` is changed. The host applies that prefix, authentication, and registration.

## Requirements

- Vestigo built with plugin API 1.1 (`Vestigo.Abstractions` 1.1.0, `ModelContextProtocol` 2.2.0)
- .NET 10 SDK 10.0.100 to build this repository
- For a managed backend: a preinstalled Node.js 18 or newer, the pinned `@playwright/mcp` 0.0.85 payload, and installed Chrome or Edge
- For a visible browser while Vestigo runs as a Windows service: an external Playwright MCP process already running as the logged-on user

See [docs/findings.md](docs/findings.md) for the pinned package and the isolation behavior that was verified from the published bundle.

## Build

```powershell
dotnet test tests\Vestigo.Playwright.Tests\Vestigo.Playwright.Tests.csproj
dotnet publish src\Vestigo.Playwright\Vestigo.Playwright.csproj -c Release -o artifacts\plugin
```

Publish removes the shared host assemblies (`Vestigo.Abstractions`, `ModelContextProtocol*`, `Microsoft.Extensions.AI.Abstractions`). The host loads those from its own directory. Do not trim or single-file publish the plugin.

When `artifacts/contracts` exists, restore adds it as a package source. A fresh checkout does not need that folder to exist. Pack the contracts from a Vestigo checkout into it when `Vestigo.Abstractions` 1.1.0 is not on NuGet.org:

```powershell
dotnet pack <vestigo>\server\Vestigo.Abstractions\Vestigo.Abstractions.csproj -c Release -o artifacts\contracts
```

## Deploy

On Windows, `scripts/Build-Installer.ps1` builds `Vestigo.Playwright-<version>.msi` and `Vestigo.Playwright.Setup-<version>.exe`. Setup.exe checks the embedded MSI hash, requires a Vestigo installation, and in `--mode managed` also checks for Node.js and Chrome or Edge. It does not download those prerequisites, change Vestigo settings, or restart the service. The MSI copies files into `%ProgramData%\Nefarius Software Solutions\Vestigo\plugins\playwright` and grants SYSTEM and Administrators full control and Users read and execute on that directory and on the `plugins` directory above it. Loading plugins and enabling the `Playwright` module are still separate settings. See [docs/installer.md](docs/installer.md) and [docs/deployment.md](docs/deployment.md).

A manual copy of the publish directory to that same plugin path is the non-installer deployment. The directory must be writable only by SYSTEM and Administrators.

## Select a browser session

Every list and call for a browser uses one application session ID of 16–128 letters, digits, underscores, or hyphens. Send it as the `X-Vestigo-Session-Id` header or as `_meta["vestigo/sessionId"]`. The published-host test used both. This value is not the MCP transport session ID.

```json
{
  "name": "playwright_browser_navigate",
  "arguments": { "url": "https://example.com" },
  "_meta": { "vestigo/sessionId": "0123456789abcdef0123456789abcdef" }
}
```

The same Vestigo bearer token plus the same session ID is the same browser. A different token or a different ID is a different browser. After a restart, owner IDs change; choose a new session ID rather than expecting the previous browser to reattach.

## Tests already run

`dotnet test` covers settings rejection, session isolation, cancellation, tombstones, catalog forwarding, and a stdio round trip through the fake MCP backend. `tests/Test-PublishedHost.ps1` starts a published Vestigo host and checks discovery, calls, images, upstream tool errors, owner and session isolation, a disabled module, and a prefix collision.

Those tests use a fake backend. They do not prove a visible Chrome or Edge window, a Windows service desktop, or attachment to an existing browser. An opt-in smoke test against the pinned official backend is still separate; headless execution would not prove the interactive desktop either.

GitHub Actions runs the same checks on Windows. It packs `Vestigo.Abstractions` 1.1.0 from the Vestigo commit pinned in [`.github/workflows/ci.yml`](.github/workflows/ci.yml), publishes this plugin and the fake backend, publishes that host, and runs `tests/Test-PublishedHost.ps1`. A following Windows job builds the unsigned MSI and Setup.exe from those published files and runs `tests/Test-Installer.ps1`: upgrade, repair, protected ACLs, a fake-backend call through the installed plugin, and uninstall. That job does not sign the packages, download Node.js or a browser, launch Chrome or Edge, or restart a Vestigo service. The workflow also runs secret scanning and dependency checks. A real `@playwright/mcp` session remains a separate check.

## Configuration and failures

[docs/configuration.md](docs/configuration.md) lists the settings. [docs/security.md](docs/security.md) covers credentials, profiles, and loopback exposure. [docs/troubleshooting.md](docs/troubleshooting.md) lists the stable tool-result codes.
