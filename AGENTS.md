# AI-assisted Vestigo plugin development

These instructions apply to this plugin repository. The Vestigo checkout is a host
reference and an integration-test target. Do not add Playwright to the host, do
not reference `Vestigo.Server`, and do not change host contracts unless a separate
gap is recorded. Follow the task instructions and the sections below.

## This repository

- Plugin id `playwright`, module `Playwright`, entry `Vestigo.Playwright.Plugin`,
  `hostApiVersion` 1.1. The module is disabled by default. `ToolTypes` is empty.
  One singleton `IVestigoToolProvider` forwards `@playwright/mcp` tools.
- Pin `@playwright/mcp` 0.0.85 and the MCP SDK 2.2.0 already recorded in
  `docs/findings.md`. Do not download packages when Vestigo starts.
- Key every browser by the full `PluginSessionIdentity`. Do not infer a session
  from a client name, address, or MCP transport session. Do not reconnect a
  tombstoned identity or replay a failed action.
- Managed headed mode is refused in Windows session 0. External mode is the
  visible-browser path for a Vestigo service.
- Source files are UTF-8 without BOM.

## Start with the supported API

- In a Vestigo checkout, read `docs/plugins.md`, the public contracts in
  `server/Vestigo.Abstractions`, and `examples/Vestigo.ExamplePlugin` before editing.
  For an independent plugin, consult the target host release's
  [plugin guide](https://github.com/nefarius/Vestigo/blob/master/docs/plugins.md)
  and the contracts package actually referenced by that project. Verify signatures
  against that version; do not infer APIs from the server's internal classes.
- Establish the plugin ID, module names, tool names, dependencies, settings,
  endpoints and security-relevant capabilities. Keep new examples harmless.
- Keep corporate source, binaries, credentials and private feeds in the private
  plugin repository. Never add them to Vestigo's public source or release payload.

## Implement a plugin, not a second host

- Target .NET 10 (the host's Windows TFM when Windows APIs require it), enable
  `EnableDynamicLoading`, and reference `Microsoft.AspNetCore.App` plus a compatible
  released MCP SDK. Independent plugins reference `Vestigo.Abstractions` as a
  NuGet package; the in-repository example may use its existing project reference.
  Do not reference `Vestigo.Server` or copy host implementations into the plugin.
- Use `Vestigo.Extensibility.IVestigoPlugin` with a public concrete entry type and
  public parameterless constructor. Return explicit `VestigoModule` instances
  through `Modules`; do not scan unrelated assemblies or modify `ModuleRegistry.All`.
- Derive modules from `Vestigo.Extensibility.VestigoModule`. Register services in
  `ConfigureServices(IServiceCollection, PluginContext)` and use constructor DI.
  Read module settings from `PluginContext.Settings`. Do not build a second service
  provider, mutate the host provider at runtime, or require `VestigoOptions`.
- Contribute attributed MCP tool classes through `ToolTypes` to the existing `/mcp`
  pipeline. Assign stable explicit tool names; check collisions across public and
  non-public attributed methods using the SDK's resolved names, not class names.
- For protocol-defined catalogs, require API 1.1 and register singleton
  `IVestigoToolProvider` services. Reserve a stable prefix; let the host compose
  handlers. Preserve schemas/results and use the full `PluginSessionIdentity`
  owner/session key for isolation. Do not replace SDK handlers or infer sessions
  from client names/IPs. Providers own backend leases, concurrency and cleanup.
- Map HTTP routes through `MapEndpoints(IEndpointRouteBuilder, PluginContext)`.
  Paths are relative to `/plugins/<id>`. Preserve host authorization; never call
  `AllowAnonymous()` on an endpoint or route group. All valid host tokens can use
  all enabled plugin tools; implement additional authorization when required.
- Keep loading and external module activation opt-in. Constructors may run during
  discovery even for disabled modules: avoid side effects there. Declare module
  dependencies and informational `Capabilities`; capability declarations do not
  constrain execution. Bound work and output, honor cancellation, confine client
  paths to the plugin's configured allowed roots, and never log secrets or tokens.

## Package and deploy deliberately

- Ship a strict `plugin.json` with `id`, `version`, `hostApiVersion`, `entryAssembly`
  and `entryType`. IDs are 1–64 lowercase letters/digits/hyphens, starting with a
  letter, without trailing whitespace. The version is numeric; the API requirement
  is major.minor (currently 1.1; existing API 1.0 plugins remain supported), independent of the Vestigo application version.
  Use an entry DLL filename directly in the plugin directory, never an absolute
  path or traversal. Check the target host's compatibility policy before upgrading.
- Publish the complete dependency graph: entry DLL, `.deps.json`, manifest, private
  managed dependencies and native RID assets. Do not trim or single-file bundle a
  plugin. Share the host's contracts/SDK identities; do not try to bypass a rejected
  contract version by loading another copy. Private dependencies stay plugin-local.
- Use a protected plugin root separate from the host installation. In development,
  use an isolated `VESTIGO_SETTINGS_DIR` and its reserved `plugins` subdirectory.
  Preserve ACL, reparse-point and device-path checks. Never make executable code
  writable through MCP file tools or relax trust checks to get a test passing.
- Plugins are trusted in-process code with the service's privileges, possibly
  LocalSystem. AssemblyLoadContext provides dependency isolation, not a security
  sandbox. Use an explicit administrative or pinned-package trust policy; loading
  and enabling modules are separate administrator choices. Updates require restart.
  Keep local verification isolated from a live service unless deployment is in scope.

## Verify and hand off

- Test actual published artifacts, including an independent build without a server
  reference, private dependency resolution, and the intended host architecture.
  Verify MCP discovery and invocation, injected services, disabled-module absence,
  and authenticated HTTP routes when provided. Include negative tests for duplicate
  names, bad manifests, missing dependencies, anonymous groups and trust rejection
  when the change affects those behaviors. Assert the intended failure diagnostic.
- Run `dotnet test tests/Vestigo.Playwright.Tests`. With PowerShell 7.4+, run
  `tests/Test-PublishedHost.ps1` against a published Vestigo host, this plugin's
  publish directory, and the fake backend publish directory. Do not point that
  script at the example echo plugin.
- A fake backend does not prove headed Chrome or Edge, a Windows service desktop,
  or attachment to an existing browser. Say so. Never claim an MSI upgrade was
  tested using only a test host.
- Document manifest fields, module settings, data disclosure, dependencies, trust
  requirements, and installation steps in this repository. Do not edit Vestigo's
  docs for this plugin.
