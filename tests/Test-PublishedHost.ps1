#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$HostPublishDirectory,
    [Parameter(Mandatory)][string]$PluginPublishDirectory,
    [Parameter(Mandatory)][string]$FakeBackendDirectory
)

# Exercises a published Vestigo host and this plugin. Does not install a service or change machine settings.
$ErrorActionPreference = 'Stop'
$hostPayload = (Resolve-Path -LiteralPath $HostPublishDirectory).Path
$pluginPayload = (Resolve-Path -LiteralPath $PluginPublishDirectory).Path
$fakePayload = (Resolve-Path -LiteralPath $FakeBackendDirectory).Path
$hostExe = Join-Path $hostPayload 'Vestigo.exe'
$fakeDll = Join-Path $fakePayload 'Vestigo.Playwright.FakeBackend.dll'
$dotnet = if ($env:DOTNET_HOST_PATH) { $env:DOTNET_HOST_PATH } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
foreach ($path in @($hostExe, (Join-Path $hostPayload 'Vestigo.Abstractions.dll'), (Join-Path $pluginPayload 'plugin.json'), $fakeDll, $dotnet)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Required file is missing: $path" }
}
foreach ($shared in @('Vestigo.Abstractions.dll', 'ModelContextProtocol.dll', 'ModelContextProtocol.Core.dll', 'Microsoft.Extensions.AI.Abstractions.dll')) {
    if (Test-Path -LiteralPath (Join-Path $pluginPayload $shared)) { throw "Plugin payload must not ship shared host assembly $shared." }
}

function New-ProtectedState {
    $state = Join-Path ([IO.Path]::GetTempPath()) ('vestigo-playwright-host-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $state | Out-Null
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    foreach ($sid in @($currentSid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            $sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $state -AclObject $security
    return $state
}

function New-ListenerPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Write-Settings([string]$state, [int]$port, [string[]]$tokens, [bool]$enabled, [string]$prefix) {
    $profiles = Join-Path $state 'profiles'
    $artifacts = Join-Path $state 'artifacts'
    New-Item -ItemType Directory -Path $profiles, $artifacts -Force | Out-Null
    $settings = @{
        Vestigo = @{
            ListenAddress = '127.0.0.1'
            ListenPort = $port
            ApiTokens = $tokens
            Plugins = @{ Enabled = $true; TrustMode = 'AdministratorManaged' }
            Modules = @{
                Playwright = @{
                    Enabled = $enabled
                    Mode = 'Managed'
                    Headed = $false
                    Browser = 'msedge'
                    Prefix = $prefix
                    ExecutablePath = $dotnet
                    Arguments = @('exec', $fakeDll)
                    ProfilesRoot = $profiles
                    ArtifactsRoot = $artifacts
                    StartupTimeout = '00:00:20'
                    ConnectionTimeout = '00:00:20'
                    CallTimeout = '00:00:20'
                    ListTimeout = '00:00:20'
                }
            }
        }
    }
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $state 'appsettings.json') -Encoding utf8NoBOM
}

function Install-Plugin([string]$state) {
    $deployment = Join-Path $state 'plugins\playwright'
    New-Item -ItemType Directory -Path $deployment -Force | Out-Null
    Copy-Item -Path (Join-Path $pluginPayload '*') -Destination $deployment -Recurse -Force
}

function Start-Vestigo([string]$state) {
    $start = [Diagnostics.ProcessStartInfo]::new($hostExe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['VESTIGO_SETTINGS_DIR'] = $state
    $process = [Diagnostics.Process]::Start($start)
    $script:stdout = $process.StandardOutput.ReadToEndAsync()
    $script:stderr = $process.StandardError.ReadToEndAsync()
    return $process
}

function Stop-Vestigo($process) {
    if ($null -ne $process -and !$process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    if ($null -ne $process) { $process.Dispose() }
}

function Wait-Healthy([string]$base, $process) {
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        if ($process.HasExited) {
            throw "Published host exited.`n$($script:stderr.GetAwaiter().GetResult())`n$($script:stdout.GetAwaiter().GetResult())"
        }
        try {
            $health = Invoke-WebRequest "$base/health" -ConnectionTimeoutSeconds 1 -OperationTimeoutSeconds 1 -SkipHttpErrorCheck
            if ($health.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    throw 'Published host did not become healthy.'
}

function Invoke-Mcp([string]$base, [hashtable]$headers, [int]$id, [string]$method, $parameters) {
    $body = @{ jsonrpc = '2.0'; id = $id; method = $method; params = $parameters } | ConvertTo-Json -Depth 12 -Compress
    $response = Invoke-WebRequest "$base/mcp" -Method Post -Headers $headers -ContentType 'application/json' -Body $body -ConnectionTimeoutSeconds 5 -OperationTimeoutSeconds 30
    $content = [string]$response.Content
    if ($content.StartsWith('event:') -or $content.StartsWith('data:')) {
        $content = (($content -split "`n" | Where-Object { $_.StartsWith('data:') }) -replace '^data:\s*', '') -join "`n"
    }
    return ($content | ConvertFrom-Json)
}

$session = 'session-0123456789abcdef'
$otherSession = 'session-fedcba9876543210'
$enabledState = New-ProtectedState
Install-Plugin $enabledState
$port = New-ListenerPort
$owner = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$otherOwner = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
Write-Settings $enabledState $port @($owner, $otherOwner) $true 'playwright_'
$process = Start-Vestigo $enabledState
$base = "http://127.0.0.1:$port"
try {
    Wait-Healthy $base $process
    $headers = @{ Authorization = "Bearer $owner"; Accept = 'application/json, text/event-stream' }
    $initial = Invoke-Mcp $base $headers 1 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'Vestigo.Playwright host test'; version = '1.0' }
    }
    if ($initial.error) { throw "initialize failed: $($initial.error.message)" }
    $headers['MCP-Protocol-Version'] = $initial.result.protocolVersion

    $listed = Invoke-Mcp $base $headers 2 'tools/list' @{ _meta = @{ 'vestigo/sessionId' = $session } }
    if ($listed.error) { throw "tools/list failed: $($listed.error.message)" }
    $names = @($listed.result.tools | ForEach-Object { $_.name })
    foreach ($expected in @('playwright_browser_navigate', 'playwright_browser_snapshot', 'playwright_browser_click', 'playwright_browser_take_screenshot', 'list_jobs')) {
        if ($names -notcontains $expected) { throw "Catalog is missing $expected." }
    }
    if ($names | Where-Object { $_ -like 'playwright_playwright_*' }) { throw 'The host prefix was applied twice.' }

    $anonymous = @{ Authorization = "Bearer $owner"; Accept = 'application/json, text/event-stream'; 'MCP-Protocol-Version' = $headers['MCP-Protocol-Version'] }
    $missing = Invoke-Mcp $base $anonymous 3 'tools/list' @{}
    if (@($missing.result.tools | Where-Object { $_.name -like 'playwright_*' }).Count -ne 0) { throw 'Tools that require a session were listed without one.' }
    $rejected = Invoke-Mcp $base $anonymous 4 'tools/call' @{ name = 'playwright_browser_navigate'; arguments = @{ url = 'https://one.example' } }
    if ([string]$rejected.error.message -notlike '*session_identity_required*') { throw 'A call without a session did not fail closed.' }

    $navigated = Invoke-Mcp $base $headers 5 'tools/call' @{
        name = 'playwright_browser_navigate'
        arguments = @{ url = 'https://one.example' }
        _meta = @{ 'vestigo/sessionId' = $session; keep = 'kept-meta' }
    }
    if ($navigated.error -or $navigated.result.isError) { throw 'navigate failed.' }
    $headerClient = @{ Authorization = "Bearer $owner"; Accept = 'application/json, text/event-stream'; 'MCP-Protocol-Version' = $headers['MCP-Protocol-Version']; 'X-Vestigo-Session-Id' = $session }
    $snapshot = Invoke-Mcp $base $headerClient 6 'tools/call' @{ name = 'playwright_browser_snapshot' }
    $snapshotText = @($snapshot.result.content | ForEach-Object { $_.text }) -join "`n"
    if ($snapshotText -notlike '*https://one.example*') { throw 'Browser state did not survive the next call.' }
    $click = Invoke-Mcp $base $headers 7 'tools/call' @{ name = 'playwright_browser_click'; _meta = @{ 'vestigo/sessionId' = $session } }
    if (-not $click.result.isError -or -not $click.result.structuredContent.kept) { throw 'Upstream tool error or structured content was not preserved.' }
    $image = Invoke-Mcp $base $headers 8 'tools/call' @{ name = 'playwright_browser_take_screenshot'; _meta = @{ 'vestigo/sessionId' = $session } }
    if (@($image.result.content | Where-Object { $_.type -eq 'image' }).Count -lt 1) { throw 'Inline image content was dropped.' }

    $secondHeaders = @{ Authorization = "Bearer $otherOwner"; Accept = 'application/json, text/event-stream'; 'MCP-Protocol-Version' = $headers['MCP-Protocol-Version'] }
    $other = Invoke-Mcp $base $secondHeaders 9 'tools/call' @{ name = 'playwright_browser_snapshot'; _meta = @{ 'vestigo/sessionId' = $session } }
    $otherText = @($other.result.content | ForEach-Object { $_.text }) -join "`n"
    if ($otherText -notlike '*about:blank*') { throw 'A different owner observed the first browser.' }
    $fresh = Invoke-Mcp $base $headers 10 'tools/call' @{ name = 'playwright_browser_snapshot'; _meta = @{ 'vestigo/sessionId' = $otherSession } }
    $freshText = @($fresh.result.content | ForEach-Object { $_.text }) -join "`n"
    if ($freshText -notlike '*about:blank*') { throw 'A different session ID observed the first browser.' }
    Write-Output 'Published host: discovery, invocation, images, tool errors, and owner/session isolation passed.'
}
finally {
    Stop-Vestigo $process
    Write-Output "Enabled-module diagnostics: $enabledState"
}

$disabledState = New-ProtectedState
Install-Plugin $disabledState
$disabledPort = New-ListenerPort
$disabledToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
Write-Settings $disabledState $disabledPort @($disabledToken) $false 'playwright_'
$disabled = Start-Vestigo $disabledState
try {
    Wait-Healthy "http://127.0.0.1:$disabledPort" $disabled
    $disabledHeaders = @{ Authorization = "Bearer $disabledToken"; Accept = 'application/json, text/event-stream' }
    $disabledInit = Invoke-Mcp "http://127.0.0.1:$disabledPort" $disabledHeaders 1 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'Vestigo.Playwright host test'; version = '1.0' }
    }
    $disabledHeaders['MCP-Protocol-Version'] = $disabledInit.result.protocolVersion
    $disabledList = Invoke-Mcp "http://127.0.0.1:$disabledPort" $disabledHeaders 2 'tools/list' @{ _meta = @{ 'vestigo/sessionId' = $session } }
    $disabledNames = @($disabledList.result.tools | ForEach-Object { $_.name })
    if ($disabledNames -contains 'playwright_browser_navigate') { throw 'A disabled Playwright module contributed tools.' }
    if ($disabledNames -notcontains 'list_jobs') { throw 'Disabling Playwright removed built-in tools.' }
    Write-Output 'Published host: disabled module contributed no tools.'
}
finally {
    Stop-Vestigo $disabled
    Write-Output "Disabled-module diagnostics: $disabledState"
}

$conflictState = New-ProtectedState
Install-Plugin $conflictState
$conflictPort = New-ListenerPort
$conflictToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
Write-Settings $conflictState $conflictPort @($conflictToken) $true 'list_'
$conflict = Start-Vestigo $conflictState
try {
    $failed = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        if ($conflict.HasExited) { $failed = $true; break }
        try {
            $health = Invoke-WebRequest "http://127.0.0.1:$conflictPort/health" -ConnectionTimeoutSeconds 1 -OperationTimeoutSeconds 1 -SkipHttpErrorCheck
            if ($health.StatusCode -eq 200) { throw 'A conflicting playwright prefix was allowed to serve.' }
        } catch {
            if ("$_" -like '*conflicting playwright prefix*') { throw }
        }
        Start-Sleep -Milliseconds 100
    }
    if (!$failed) { throw 'A conflicting prefix did not stop the host.' }
    $detail = "$($script:stderr.GetAwaiter().GetResult())`n$($script:stdout.GetAwaiter().GetResult())"
    if ($detail -notlike '*provider_registration_invalid*') { throw "Prefix conflict did not report provider_registration_invalid.`n$detail" }
    Write-Output 'Published host: conflicting prefix stopped startup.'
}
finally {
    Stop-Vestigo $conflict
    Write-Output "Prefix-conflict diagnostics: $conflictState"
}
