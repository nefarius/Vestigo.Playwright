# Security

The plugin runs in-process with Vestigo. If Vestigo is a Windows service, that can be LocalSystem. Assembly isolation is not a sandbox. Enable plugin loading only with `AdministratorManaged` or `PinnedSha256` trust, and enable the `Playwright` module only when browser automation is intended.

Every valid Vestigo API token can call every enabled plugin tool. This plugin adds no extra scope. A shared token and a shared `X-Vestigo-Session-Id` are the same browser. Give independent agents different credentials or different session IDs.

## Upstream credentials

`ExternalHeaders` are the only credentials sent to Playwright MCP. The plugin never forwards the caller's Vestigo bearer token and never uses a caller-supplied endpoint. Header values of four or more characters are treated as secrets: they are removed from diagnostic text and must not appear in tool results.

Remote endpoints need HTTPS, unless `AllowInsecureRemote` is an explicit deployment choice, and they need at least one configured request header. The plugin does not check that the header is a credential; the Playwright MCP backend has to enforce authentication. Loopback without a header is rejected unless `AllowUnauthenticatedLoopback` is set. Another local user can otherwise connect to that endpoint. Managed HTTP has the same loopback exposure and no upstream authentication; stdio does not listen.

## Browser and files

Dedicated automation profiles are the default. Persistent profiles take an exclusive lock and are not shared. Attaching to an existing personal profile or CDP endpoint is opt-in, shared, and not isolated.

Profile and artifact directories are created with inheritance disabled and full control for SYSTEM, Administrators, and the current user. They must sit outside the plugin deployment directory. Caller IDs are hashed before they become directory names. Ephemeral artifact directories are deleted when that session ends. Persistent profile directories are kept, but a lost identity is not bound to them again.

The plugin does not pass `--no-sandbox`. `--allowed-origins` and the upstream filesystem guards are defense in depth. They do not confine redirects or make the browser a security sandbox. DevTools endpoints stay on loopback.

API 1.1 does not proxy MCP resources, roots, sampling, or elicitation. Resource links are not returned to the client. A text block that is only a local path is annotated. Do not treat those paths as files Vestigo can fetch.

## Installer and host policy

The MSI and Setup.exe copy plugin files. They do not change `TrustMode`, package hashes, API tokens, or module settings, and they do not stop or restart the Vestigo service. A direct MSI does not launch other installers. Setup.exe embeds one MSI and refuses it when the SHA-256 compiled into that executable does not match. It does not download Node.js, Chrome, Edge, or `@playwright/mcp`. Exit code 3 remains reserved for a download failure.

The `plugins` directory and the installed plugin directory are writable by SYSTEM and Administrators and readable and executable by Users. They do not inherit write access from ProgramData. That is the ACL the Vestigo plugin loader accepts for a service. Pull-request packages are unsigned. A tagged release can sign the plugin binaries, the MSI, and Setup.exe; an unsigned package is not a signed release.

Browser profiles are not removed with the product. Shared Chrome, Edge, and Node installations are not uninstalled with this plugin. Uninstall leaves the Vestigo settings file in place.
