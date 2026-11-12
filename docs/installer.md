# Installer

`Vestigo.Playwright.msi` and `Vestigo.Playwright.Setup.exe` are not built yet. No MSI, bootstrapper, repair, or uninstall test has been run. The notes below are the decisions the installer must keep when it is built. They are not a claim that a package already does them.

The product is installed and upgraded separately from the Vestigo MSI. It uses its own upgrade code. It does not stop or change the Vestigo service unless the administrator chooses that. It does not remove Chrome, Edge, Node.js, or browser profiles.

`installer/Vestigo.Playwright.Setup.Core` is the part that exists and has tests:

- A downloaded file is accepted only when its SHA-256 matches the digest shipped with the signed release. The mismatch error does not include the file bytes.
- Enabling the plugin does not change `TrustMode`, package hashes, API tokens, or other modules. When trust is missing or `Disabled`, the settings document is returned unchanged.
- External-endpoint setup does not require a local Node.js or browser. Managed setup does. A missing Vestigo installation is a different result from a missing browser.

Direct MSI installation must not launch prerequisite MSIs. The bootstrapper, when it exists, sequences those installs first and only from URLs and hashes embedded in the signed release. There is no `@latest` and no download when Vestigo starts.

Exit codes reserved for that bootstrapper are 0 success, 1 bad arguments, 2 missing prerequisite, 3 download failure, 4 integrity mismatch, 5 cancelled, 6 Vestigo not found, and 7 install failed.
