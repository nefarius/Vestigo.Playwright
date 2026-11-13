# Installer

The Windows package is a per-machine x64 MSI, `Vestigo.Playwright-<version>.msi`, and a self-contained `Vestigo.Playwright.Setup-<version>.exe` that embeds that MSI. Both are separate from the Vestigo product. The upgrade code is `6f3c8e21-4b7a-4d19-8c52-9e1a0d6b7f24`. Each version has its own product code, derived as a UUID v5 from namespace `2c91e7a4-8d05-4f6b-b3a1-7e5c9d2f8140` and the name `Vestigo.Playwright:<major>.<minor>.<build>`. A newer version replaces an older one. A downgrade is refused.

Installation copies the published plugin into:

`%ProgramData%\Nefarius Software Solutions\Vestigo\plugins\playwright`

SYSTEM and Administrators receive full control of that directory and of the `plugins` directory above it. Users receive read and execute. Neither directory inherits write access from ProgramData. The plugin loader checks the `plugins` directory before it loads a deployment. The package does not change Vestigo settings, stop or restart the Vestigo service, install Node.js or a browser, or remove Chrome, Edge, Node.js, browser profiles, or other files in the Vestigo settings directory. Loading plugins and enabling the `Playwright` module remain administrator settings. See [deployment.md](deployment.md).

The MSI does not launch other installers. Setup.exe does not download prerequisites. Exit code 3 is reserved for a download failure and is not produced by this build.

## Commands

Build both packages on Windows with PowerShell 7.4 or later, WiX 5.0.2, the UI and Util extensions, and the .NET 10 SDK. The script publishes the plugin once, checks that shared host assemblies are absent, builds the MSI, and embeds that MSI and its SHA-256 in Setup.exe.

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.UI.wixext/5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2
pwsh ./scripts/Build-Installer.ps1
```

Pull-request builds are unsigned. A later tagged release can sign the published plugin binaries, the MSI, and Setup.exe. `Build-Installer.ps1 -RequireSignature` rejects a package that is not Authenticode signed. Without that switch the script accepts `NotSigned` or `Valid` and rejects a hash mismatch.

Install, quietly, for a normal Vestigo installation:

```powershell
.\Vestigo.Playwright.Setup-1.0.0.exe --mode external
.\Vestigo.Playwright.Setup-1.0.0.exe --mode external --quiet
```

`--vestigo` selects the fully qualified directory that contains `Vestigo.exe` and `Vestigo.Abstractions.dll` when Vestigo is portable or installed outside Program Files. It does not change the install destination. The MSI always writes the plugin to `%ProgramData%\Nefarius Software Solutions\Vestigo\plugins\playwright`. A portable host that loads plugins from another root needs the manual copy in [deployment.md](deployment.md). `--mode managed` also requires Chrome or Edge registered in App Paths. When `Vestigo:Modules:Playwright:ExecutablePath` is set, that file is the only managed host that is accepted. When it is not set, `node.exe` must be on `PATH` or under the usual Node.js install directories. `--verify-only` checks the embedded MSI hash and the selected prerequisites, then exits without installing. `--uninstall` removes the product whose code is embedded in that Setup.exe.

The MSI can be installed directly. It does not check for Vestigo, Node.js, or a browser.

```powershell
msiexec /i Vestigo.Playwright-1.0.0.msi
msiexec /i Vestigo.Playwright-1.0.0.msi /qn /norestart
msiexec /fa {6a3efdd9-227a-5d27-8282-6cf022f3a59d} /qn /norestart
msiexec /x {6a3efdd9-227a-5d27-8282-6cf022f3a59d} /qn /norestart
```

The product code above is version 1.0.0. Repair and uninstall of another version use that version's product code. Uninstall removes the plugin directory and leaves Vestigo settings and browser profiles in place.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Setup finished. Windows Installer codes 3010 and 1641 are also reported as success. Both mean a reboot is still required before replaced files are loaded. |
| 1 | The arguments are not valid, or this build has no embedded MSI. |
| 2 | Managed mode did not find Node.js or Chrome or Edge. |
| 3 | Reserved for a download failure. This build does not download anything. |
| 4 | The embedded MSI hash did not match the hash compiled into Setup.exe. The error text does not include the file bytes. |
| 5 | Windows Installer was cancelled. |
| 6 | Vestigo was not found. |
| 7 | Windows Installer failed. |

## What the tests do not prove

`dotnet test` covers product codes, argument parsing, host/Node/browser decisions, hash checks, and Windows Installer exit codes. On a Windows runner, `tests/Test-Installer.ps1` installs version 0.9.0, upgrades to the current MSI, checks the protected ACL and the exact published payload, loads that installation through the pinned published Vestigo host and the fake backend, repairs a deleted file, and uninstalls while the settings and profile sentinels remain. It also runs Setup.exe for a missing host, external validation, the embedded hash, a quiet install, and uninstall.

That host exercise uses the fake backend. It does not prove a visible Chrome or Edge window, a Windows service desktop, attachment to an existing browser, or an MSI upgrade of Vestigo itself. Setup.exe does not download or install Node.js, Chrome, Edge, or the `@playwright/mcp` payload.
