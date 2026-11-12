# Troubleshooting

Startup failures from invalid configuration stop the host. The message is the safe text from the plugin; it does not include header values, process arguments that contain secrets, or raw exception dumps. Operational failures after startup are tool results so they survive the host's exception sanitizer. A protocol error of `provider_call_failed` or `provider_catalog_invalid` means an unexpected provider exception, not one of the codes below.

| Code | Meaning |
| --- | --- |
| `playwright_configuration_invalid` | The module settings cannot be used. Typical causes are a relative executable path, a profile directory inside the plugin folder, a denied browser flag, a remote URL without HTTPS or a credential, or managed headed mode in Windows session 0. |
| `playwright_backend_unavailable` | The configured process did not start, or the upstream MCP endpoint did not complete initialization. A later call with the same session ID can try again only when no connection was stored. |
| `playwright_startup_timeout` | The managed HTTP listener did not accept connections before `StartupTimeout`. |
| `playwright_session_lost` | A connected browser or transport failed. The action was not replayed. Choose a new application session ID. |
| `playwright_session_expired` | The session was idle longer than `IdleTimeout` and had no active operation. Choose a new application session ID. |
| `playwright_operation_timeout` | A list or call exceeded its deadline. The session is tombstoned and the action was not replayed. |
| `playwright_busy` | Too many operations are waiting on a session (`MaxWaitingOperations`). |
| `playwright_capacity` | `MaxSessions` is full, or an exclusive external endpoint is already leased. |
| `playwright_shutting_down` | Vestigo is stopping. New sessions are rejected. |
| `playwright_unsupported` | The request needs a capability this plugin does not proxy. |
| `session_identity_required` | The host hid or rejected the call because no authenticated application session ID was supplied. This text is the host's, not a plugin code. |
| `provider_registration_invalid` | The host rejected the prefix, usually because it collides with another tool namespace. |

When the backend has never produced a catalog, the provider lists a single tool named `playwright_vestigo_backend_status` (the host adds the prefix). Calling it returns the recorded failure. Discovery failures are kept for `IdleTimeout` and only up to a fixed number of identities. A failure for an identity that still has a session, or whose loss is still recorded, stays available to the status tool. A new discovery failure is still recorded when those retained losses already fill the fixed number, and recording it does not drop a retained loss. A later successful list clears that identity's discovery failure. After a session is lost but a catalog was cached, list still returns that catalog so a following call can show `playwright_session_lost` instead of a missing tool.

An upstream tool that returns `IsError` is a normal tool result. It does not tombstone the browser. Fix the page action rather than choosing a new session ID.

A persistent profile that is already open fails with `playwright_configuration_invalid` and is not shared. Close the other owner of that profile, or use ephemeral profiles.

Managed headed mode from a service logs that the browser cannot be shown from session 0 and tells the operator to use external mode or `Headed: false`.
