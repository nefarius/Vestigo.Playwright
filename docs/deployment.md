# Deployment

Publish this plugin and copy the directory into the protected Vestigo plugin root. Stop Vestigo before replacing a plugin that is already loaded, then start it again. The public Vestigo MSI does not carry this plugin.

```powershell
dotnet publish src\Vestigo.Playwright\Vestigo.Playwright.csproj -c Release -o artifacts\plugin
```

The publish folder contains `plugin.json`, `Vestigo.Playwright.dll`, and `Vestigo.Playwright.deps.json`. It must not contain `Vestigo.Abstractions.dll` or the Model Context Protocol assemblies. Place it at:

`%ProgramData%\Nefarius Software Solutions\Vestigo\plugins\playwright`

Protect that tree so only SYSTEM and Administrators can write it. A development host can use `VESTIGO_SETTINGS_DIR`; its `plugins` child is the default root. `tests/Test-PublishedHost.ps1` does that with an ACL-protected temporary directory and does not install a service.

Enable plugin loading and the module separately:

```json
{
  "Vestigo": {
    "Plugins": { "Enabled": true, "TrustMode": "AdministratorManaged" },
    "Modules": { "Playwright": { "Enabled": true } }
  }
}
```

`PinnedSha256` additionally requires `Vestigo:Plugins:PackageHashes:playwright` to be the tree hash Vestigo computes. Do not weaken trust policy to make a deployment succeed.

## Visible browser with a Windows service

Run `@playwright/mcp` as the logged-on user, headed, with `--isolated` when more than one Vestigo identity will connect. Point `Mode` at `External` and set `ExternalEndpoint` to that process's `/mcp` URL. Configure an upstream credential header, or set `AllowUnauthenticatedLoopback` only when other local users cannot reach the port.

Do not use managed headed mode for a service. Session 0 has no interactive desktop. This plugin will not impersonate the logged-on user or start a browser from the service.

## Managed backend in an interactive session

Use managed mode only when Vestigo itself runs in the interactive session that should own the browser. `ExecutablePath` is the absolute path of `node.exe` or another preinstalled host. `Arguments` are the pinned CLI and its fixed arguments, for example the path of `cli.js`. The plugin appends the browser, profile, output, and `--ignore-roots` flags. It does not run `npm install` at startup.

Set `Headed` to false for an interactive session that should stay headless. Headless does not demonstrate a visible desktop.

## What was verified

On this machine, a published Vestigo host loaded the published plugin, listed and called the fake backend through `/mcp`, isolated two owners and two session IDs, hid the tools when the module was disabled, and refused to start when `Prefix` was `list_`. The fake backend is not Playwright. Official-browser, service, and existing-browser attachment runs are still open.
