# Configuration

Settings live under `Vestigo:Modules:Playwright` in the host `appsettings.json`. The module stays off until `Enabled` is true. Loading the plugin and enabling the module are separate.

```json
{
  "Vestigo": {
    "Plugins": { "Enabled": true, "TrustMode": "AdministratorManaged" },
    "Modules": {
      "Playwright": {
        "Enabled": true,
        "Mode": "External",
        "ExternalEndpoint": "http://127.0.0.1:8931/mcp",
        "ExternalIsolation": "Exclusive",
        "AllowUnauthenticatedLoopback": true,
        "Headed": true,
        "Browser": "msedge"
      }
    }
  }
}
```

## Modes

`Mode` is `Managed` (default) or `External`.

Managed mode starts `ExecutablePath` with `Arguments`, then the plugin's own flags. There is no shell. The plugin does not install or update npm packages. `ManagedTransport` is `Stdio` (default) or `Http`. HTTP binds `127.0.0.1` on a port the plugin allocates and connects to `http://127.0.0.1:{port}/mcp`. Stdio is preferred because it has no listener.

External mode connects to `ExternalEndpoint`. The caller cannot choose that URL. `ExternalTransport` is `StreamableHttp` (default), `Sse`, or `Auto`. `ExternalHeaders` are sent upstream and are not the Vestigo bearer token. Header values are redacted from logs. Redirects are disabled. User info, a query, and a fragment are rejected.

A remote endpoint must be HTTPS unless `AllowInsecureRemote` is true, and `ExternalHeaders` must contain at least one entry. That check only tests whether a header is present. It does not verify that the header is a credential. The Playwright MCP backend has to enforce its own authentication before this check can be treated as access control. A loopback endpoint with no header requires `AllowUnauthenticatedLoopback`. Loopback binding alone does not authenticate other local processes.

## Browser and profiles

`Browser` is `msedge` (default) or `chrome`. `Headed` defaults to true. Managed headed mode is rejected when Vestigo is in Windows session 0. Use external mode for a visible browser behind a service, or set `Headed` to false.

`ProfileMode` is `Ephemeral` (default) or `Persistent`. Ephemeral managed browsers get `--isolated`. Persistent profiles and artifact directories default under `%ProgramData%\Nefarius Software Solutions\Vestigo Playwright`. Both roots must be absolute and outside the plugin deployment directory. The directory leaf is a SHA-256 of the owner ID and session ID, not the raw IDs.

`Attach` is `None` (default), `Cdp`, or `Extension`. Attachment is managed-only, requires `AllowSharedBrowser`, and shares the existing browser. `CdpEndpoint` must be a loopback `http`, `https`, `ws`, or `wss` URL without credentials or a query.

## Isolation

`ExternalIsolation` is `PerSession` (default) or `Exclusive`. Per-session means the administrator started that endpoint with `--isolated` and without a shared context, extension, or CDP. The plugin cannot detect those flags. Exclusive leases the endpoint to one application identity.

`ShareBrowser` defaults to false and requires `AllowSharedBrowser`. It collapses every identity onto one browser. It cannot be combined with exclusive isolation. The plugin does not fall back to sharing when isolation cannot be established.

## Limits

| Setting | Default | Range |
| --- | --- | --- |
| `Prefix` | `playwright_` | 2–32 characters, lowercase letter, then letters, digits, or underscores, trailing `_` |
| `MaxSessions` | 8 | 1–100 |
| `MaxConcurrentStartups` | 2 | 1–32 |
| `MaxWaitingOperations` | 32 | 1–1000 |
| `IdleTimeout` | 30 minutes | positive, at most 24 hours |
| `StartupTimeout` | 30 seconds | same |
| `ConnectionTimeout` | 20 seconds | same |
| `CallTimeout` | 2 minutes | same |
| `ListTimeout` | 20 seconds | same |
| `ShutdownTimeout` | 10 seconds | same |

`AllowedOrigins` entries are absolute `http` or `https` origins. They are passed as `--allowed-origins` and are not a security boundary. `Capabilities` are lowercase names passed as `--caps`.

These managed flags are rejected in `Arguments`: `--no-sandbox`, `--shared-browser-context`, `--extension`, `--cdp-endpoint`, `--allow-unrestricted-file-access`, `--host`, `--port`, `--user-data-dir`, `--isolated`, `--browser`, `--executable-path`.
