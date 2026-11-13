#Requires -Version 7.4
param(
    [Parameter(Mandatory)][string]$CurrentMsi,
    [Parameter(Mandatory)][string]$BaselineMsi,
    [Parameter(Mandatory)][string]$SetupExe,
    [Parameter(Mandatory)][string]$HostPublishDirectory,
    [Parameter(Mandatory)][string]$PluginPublishDirectory,
    [Parameter(Mandatory)][string]$FakeBackendDirectory
)

# Installs the plugin MSI, upgrades it, and loads that installation through a published Vestigo host.
# The browser calls use the fake backend. This does not prove headed Chrome or Edge, a service desktop,
# attachment to an existing browser, or that a prerequisite was downloaded.
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The installer lifecycle test must run in an elevated Windows session.'
}

$currentMsi = (Resolve-Path -LiteralPath $CurrentMsi).Path
$baselineMsi = (Resolve-Path -LiteralPath $BaselineMsi).Path
$setup = (Resolve-Path -LiteralPath $SetupExe).Path
$hostPayload = (Resolve-Path -LiteralPath $HostPublishDirectory).Path
$pluginPayload = (Resolve-Path -LiteralPath $PluginPublishDirectory).Path
$fakePayload = (Resolve-Path -LiteralPath $FakeBackendDirectory).Path
$hostExe = Join-Path $hostPayload 'Vestigo.exe'
$fakeDll = Join-Path $fakePayload 'Vestigo.Playwright.FakeBackend.dll'
$dotnet = if ($env:DOTNET_HOST_PATH) { $env:DOTNET_HOST_PATH } else { Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
foreach ($path in @($currentMsi, $baselineMsi, $setup, $hostExe, (Join-Path $hostPayload 'Vestigo.Abstractions.dll'), (Join-Path $pluginPayload 'plugin.json'), $fakeDll, $dotnet)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required file is missing: $path" }
}

$currentManifest = Get-Content -Raw -LiteralPath ([IO.Path]::ChangeExtension($currentMsi, '.manifest.json')) | ConvertFrom-Json
$baselineManifest = Get-Content -Raw -LiteralPath ([IO.Path]::ChangeExtension($baselineMsi, '.manifest.json')) | ConvertFrom-Json
$currentCode = ([guid]$currentManifest.productCode).ToString('B')
$baselineCode = ([guid]$baselineManifest.productCode).ToString('B')
$logs = Join-Path $PSScriptRoot '..\artifacts\installer-logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$logs = (Resolve-Path -LiteralPath $logs).Path

$settingsRoot = Join-Path $env:ProgramData 'Nefarius Software Solutions\Vestigo'
$pluginRoot = Join-Path $settingsRoot 'plugins\playwright'
$settingsFile = Join-Path $settingsRoot 'appsettings.json'
$sentinel = Join-Path $settingsRoot 'installer-sentinel'
$profiles = Join-Path $settingsRoot 'playwright-managed-profiles'
$artifacts = Join-Path $settingsRoot 'playwright-managed-artifacts'
foreach ($existing in @($settingsRoot, $settingsFile, $sentinel, $profiles, $artifacts, $pluginRoot)) {
    if (Test-Path -LiteralPath $existing) { throw "Refusing to replace an existing Vestigo path: $existing" }
}
foreach ($code in @($currentCode, $baselineCode)) {
    $registration = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\$code"
    if (Test-Path -LiteralPath $registration) { throw "Refusing to replace an existing product registration: $code" }
}
$createdSettings = $false
$process = $null

function Format-CommandLine([string[]]$Values) {
    ($Values | ForEach-Object {
        if ($_ -notmatch '[\s"]') { $_ }
        else { '"' + $_.Replace('"', '\"') + '"' }
    }) -join ' '
}

function Invoke-Msi([string[]]$Arguments, [string]$Name) {
    $log = Join-Path $logs "$Name.log"
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList (Format-CommandLine ($Arguments + @('/norestart', '/l*v', $log))) -Wait -PassThru
    if ($process.ExitCode -notin 0, 3010) {
        throw "msiexec $Name failed with exit code $($process.ExitCode). Log: $log"
    }
}

function Invoke-Setup([string[]]$Arguments, [int]$Expected) {
    $output = Join-Path $logs ("setup-" + [guid]::NewGuid().ToString('N') + '.log')
    $process = Start-Process -FilePath $setup -ArgumentList (Format-CommandLine $Arguments) -Wait -PassThru -RedirectStandardOutput $output -RedirectStandardError "$output.err"
    if ($process.ExitCode -ne $Expected) {
        $detail = ''
        foreach ($log in @($output, "$output.err")) {
            if (Test-Path -LiteralPath $log) { $detail += "`n" + (Get-Content -Raw -LiteralPath $log) }
        }
        throw "Setup $($Arguments -join ' ') exited $($process.ExitCode); expected $Expected.$detail"
    }
}

function Convert-ProductVersion([string]$Text) {
    $parsed = [version]$Text
    $build = if ($parsed.Build -lt 0) { 0 } else { $parsed.Build }
    return "$($parsed.Major).$($parsed.Minor).$build"
}

function Get-ProductVersion([string]$ProductCode) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        return Convert-ProductVersion ([string]$installer.ProductInfo($ProductCode, 'VersionString'))
    } catch {
        return $null
    }
}

function Protect-ServiceState([string]$directory) {
    # The test process is an administrator, not LocalSystem. Vestigo's startup repair would grant this
    # user full control of the settings directory, and the plugin loader would reject that ancestor.
    # A service running as SYSTEM does not add that grant. Apply the service ACL without changing the
    # protected plugin directories underneath it.
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]$sid,
            'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $directory -AclObject $security
}

function Assert-ProtectedDirectory([string]$entry, [int64]$dangerous, [string[]]$trusted) {
    $acl = Get-Acl -LiteralPath $entry
    if (-not $acl.AreAccessRulesProtected) { throw "The directory still inherits access: $entry" }
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -notin $trusted) { throw "$entry is owned by $owner." }
    $rules = $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = @($rules | Where-Object {
            $_.IdentityReference.Value -eq $sid -and $_.AccessControlType -eq 'Allow' -and
            -not $_.PropagationFlags.HasFlag([Security.AccessControl.PropagationFlags]::InheritOnly)
        })
        if ($rule.Count -lt 1) { throw "The directory does not grant $sid : $entry" }
        $full = [int64][Security.AccessControl.FileSystemRights]::FullControl
        if ((([int64]$rule[0].FileSystemRights) -band $full) -ne $full) { throw "$sid is missing full control on $entry." }
    }
    $users = @($rules | Where-Object {
        $_.IdentityReference.Value -eq 'S-1-5-32-545' -and $_.AccessControlType -eq 'Allow' -and
        -not $_.PropagationFlags.HasFlag([Security.AccessControl.PropagationFlags]::InheritOnly)
    })
    if ($users.Count -lt 1) { throw "Users do not have an allow entry on $entry." }
    $readExecute = [int64][Security.AccessControl.FileSystemRights]::ReadAndExecute
    if ((([int64]$users[0].FileSystemRights) -band $readExecute) -ne $readExecute) { throw "Users are missing read and execute on $entry." }
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if ($rule.PropagationFlags.HasFlag([Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
        $sid = $rule.IdentityReference.Value
        if ($trusted -contains $sid) { continue }
        $rights = [int64]$rule.FileSystemRights
        if (($rights -band $dangerous) -ne 0) {
            throw "$entry grants $sid $($rule.FileSystemRights) (0x$($rights.ToString('X')))."
        }
    }
}

function Assert-ProtectedPayload {
    $trusted = @(
        'S-1-5-18',
        'S-1-5-32-544',
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464',
        'S-1-3-0')
    # Same mask as Vestigo's plugin loader: Write, Delete, ChangePermissions, TakeOwnership,
    # and DeleteSubdirectoriesAndFiles. Compare the underlying integers so the Synchronize bit
    # on a Users read-and-execute allow is not treated as write access.
    $dangerous = [int64]0
    foreach ($right in @(
            [Security.AccessControl.FileSystemRights]::Write,
            [Security.AccessControl.FileSystemRights]::Delete,
            [Security.AccessControl.FileSystemRights]::ChangePermissions,
            [Security.AccessControl.FileSystemRights]::TakeOwnership,
            [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles)) {
        $dangerous = $dangerous -bor [int64]$right
    }
    Assert-ProtectedDirectory (Split-Path -Parent $pluginRoot) $dangerous $trusted
    Assert-ProtectedDirectory $pluginRoot $dangerous $trusted
    $entries = @($pluginRoot) + @(Get-ChildItem -LiteralPath $pluginRoot -Recurse -Force | ForEach-Object FullName)
    foreach ($entry in $entries) {
        $acl = Get-Acl -LiteralPath $entry
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $trusted) { throw "$entry is owned by $owner." }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -ne 'Allow') { continue }
            if ($rule.PropagationFlags.HasFlag([Security.AccessControl.PropagationFlags]::InheritOnly)) { continue }
            $sid = $rule.IdentityReference.Value
            if ($trusted -contains $sid) { continue }
            $rights = [int64]$rule.FileSystemRights
            if (($rights -band $dangerous) -ne 0) {
                throw "$entry grants $sid $($rule.FileSystemRights) (0x$($rights.ToString('X')))."
            }
        }
    }
    $expected = Get-ChildItem -LiteralPath $pluginPayload -Recurse -File | ForEach-Object {
        $_.FullName.Substring($pluginPayload.Length).TrimStart('\', '/')
    }
    $actual = Get-ChildItem -LiteralPath $pluginRoot -Recurse -File | ForEach-Object {
        $_.FullName.Substring($pluginRoot.Length).TrimStart('\', '/')
    }
    $difference = Compare-Object -ReferenceObject $expected -DifferenceObject $actual
    if ($difference) { throw "Installed payload does not match the published plugin.`n$($difference | Out-String)" }
    foreach ($relative in $expected) {
        $left = (Get-FileHash -LiteralPath (Join-Path $pluginPayload $relative) -Algorithm SHA256).Hash
        $right = (Get-FileHash -LiteralPath (Join-Path $pluginRoot $relative) -Algorithm SHA256).Hash
        if ($left -ne $right) { throw "Installed file hash differs for $relative." }
    }
}

function New-ListenerPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    return $port
}

function Invoke-Mcp([string]$Base, [hashtable]$Headers, [int]$Id, [string]$Method, $Parameters) {
    $body = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12 -Compress
    $response = Invoke-WebRequest "$Base/mcp" -Method Post -Headers $Headers -ContentType 'application/json' -Body $body -ConnectionTimeoutSeconds 5 -OperationTimeoutSeconds 30
    $content = [string]$response.Content
    if ($content.StartsWith('event:') -or $content.StartsWith('data:')) {
        $content = (($content -split "`n" | Where-Object { $_.StartsWith('data:') }) -replace '^data:\s*', '') -join "`n"
    }
    return ($content | ConvertFrom-Json)
}

$missingHost = Join-Path ([IO.Path]::GetTempPath()) ('vestigo-missing-host-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $missingHost | Out-Null
try {
    Invoke-Setup @('--verify-only', '--mode', 'external', '--vestigo', $missingHost) 6
    Invoke-Setup @('--verify-only', '--mode', 'external', '--vestigo', $hostPayload) 0
    Write-Output 'Setup: missing host and embedded MSI hash checks passed.'

    New-Item -ItemType Directory -Force -Path $sentinel | Out-Null
    Set-Content -LiteralPath (Join-Path $sentinel 'settings.txt') -Value 'settings-sentinel' -Encoding utf8NoBOM
    Set-Content -LiteralPath (Join-Path $sentinel 'profile.txt') -Value 'profile-sentinel' -Encoding utf8NoBOM

    Invoke-Msi @('/i', $baselineMsi, '/qn') 'baseline-install'
    if ((Get-ProductVersion $baselineCode) -ne (Convert-ProductVersion ([string]$baselineManifest.productVersion))) {
        throw 'The baseline product version was not registered.'
    }
    Invoke-Msi @('/i', $currentMsi, '/qn') 'upgrade'
    if (Get-ProductVersion $baselineCode) { throw 'The baseline product is still installed after upgrade.' }
    if ((Get-ProductVersion $currentCode) -ne (Convert-ProductVersion ([string]$currentManifest.productVersion))) {
        throw 'The upgraded product version was not registered.'
    }
    Assert-ProtectedPayload
    Write-Output 'Installer: major upgrade, protected ACL, and exact payload passed.'

    $port = New-ListenerPort
    $token = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $settings = @{
        Vestigo = @{
            ListenAddress = '127.0.0.1'
            ListenPort = $port
            ApiTokens = @($token)
            Plugins = @{ Enabled = $true; TrustMode = 'AdministratorManaged' }
            Modules = @{
                Playwright = @{
                    Enabled = $true
                    Mode = 'Managed'
                    Headed = $false
                    Browser = 'msedge'
                    Prefix = 'playwright_'
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
    New-Item -ItemType Directory -Force -Path $settingsRoot | Out-Null
    Protect-ServiceState $settingsRoot
    Assert-ProtectedPayload
    $settings | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $settingsFile -Encoding utf8NoBOM
    $createdSettings = $true
    $start = [Diagnostics.ProcessStartInfo]::new($hostExe)
    $start.WorkingDirectory = $hostPayload
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($start.Environment.ContainsKey('VESTIGO_SETTINGS_DIR')) { $start.Environment.Remove('VESTIGO_SETTINGS_DIR') }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $base = "http://127.0.0.1:$port"
    $healthy = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        if ($process.HasExited) { break }
        try {
            $health = Invoke-WebRequest "$base/health" -ConnectionTimeoutSeconds 1 -OperationTimeoutSeconds 1 -SkipHttpErrorCheck
            if ($health.StatusCode -eq 200) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    if (-not $healthy) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        throw "Published host did not become healthy.`n$($stderr.GetAwaiter().GetResult())`n$($stdout.GetAwaiter().GetResult())"
    }
    $headers = @{ Authorization = "Bearer $token"; Accept = 'application/json, text/event-stream' }
    $initial = Invoke-Mcp $base $headers 1 'initialize' @{
        protocolVersion = '2025-11-25'; capabilities = @{}; clientInfo = @{ name = 'Vestigo.Playwright installer test'; version = '1.0' }
    }
    if ($initial.error) { throw "initialize failed: $($initial.error.message)" }
    $headers['MCP-Protocol-Version'] = $initial.result.protocolVersion
    $session = 'session-0123456789abcdef'
    $listed = Invoke-Mcp $base $headers 2 'tools/list' @{ _meta = @{ 'vestigo/sessionId' = $session } }
    if ($listed.error) { throw "tools/list failed: $($listed.error.message)" }
    $names = @($listed.result.tools | ForEach-Object { $_.name })
    if ($names -notcontains 'playwright_browser_navigate') {
        $statusName = @($names | Where-Object { $_ -like '*backend_status' } | Select-Object -First 1)
        $statusText = ''
        if ($statusName.Count -eq 1) {
            $status = Invoke-Mcp $base $headers 3 'tools/call' @{ name = $statusName[0]; _meta = @{ 'vestigo/sessionId' = $session } }
            $statusText = if ($status.error) { [string]$status.error.message } else { @($status.result.content | ForEach-Object { $_.text }) -join "`n" }
        }
        throw "Installed plugin catalog is missing playwright_browser_navigate. $statusText Present: $($names -join ', ')."
    }
    $navigated = Invoke-Mcp $base $headers 4 'tools/call' @{
        name = 'playwright_browser_navigate'
        arguments = @{ url = 'https://installer.example' }
        _meta = @{ 'vestigo/sessionId' = $session }
    }
    if ($navigated.error -or $navigated.result.isError) { throw 'The installed plugin did not call the fake backend.' }
    Write-Output 'Installer: published host loaded the installed plugin through the fake backend.'
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    $process.Dispose()
    $process = $null

    $removed = Join-Path $pluginRoot 'plugin.json'
    Remove-Item -LiteralPath $removed -Force
    Invoke-Msi @('/fa', $currentCode, '/qn') 'repair'
    if (-not (Test-Path -LiteralPath $removed)) { throw 'Repair did not restore plugin.json.' }
    $restored = (Get-FileHash -LiteralPath $removed -Algorithm SHA256).Hash
    $publishedHash = (Get-FileHash -LiteralPath (Join-Path $pluginPayload 'plugin.json') -Algorithm SHA256).Hash
    if ($restored -ne $publishedHash) { throw 'Repaired plugin.json does not match the published file.' }
    Assert-ProtectedPayload
    Write-Output 'Installer: repair restored the removed file and the protected ACL.'

    Invoke-Msi @('/x', $currentCode, '/qn') 'uninstall'
    if (Get-ProductVersion $currentCode) { throw 'The product is still installed.' }
    if (Test-Path -LiteralPath $pluginRoot) { throw 'Uninstall left the plugin directory in place.' }
    foreach ($pair in @{ 'settings.txt' = 'settings-sentinel'; 'profile.txt' = 'profile-sentinel' }.GetEnumerator()) {
        $text = (Get-Content -Raw -LiteralPath (Join-Path $sentinel $pair.Key)).Trim()
        if ($text -ne $pair.Value) { throw "Uninstall changed $($pair.Key)." }
    }
    if (-not (Test-Path -LiteralPath $settingsFile)) { throw 'Uninstall removed Vestigo settings.' }
    Write-Output 'Installer: uninstall preserved settings and profile sentinels.'

    Invoke-Setup @('--quiet', '--mode', 'external', '--vestigo', $hostPayload) 0
    if ((Get-ProductVersion $currentCode) -ne (Convert-ProductVersion ([string]$currentManifest.productVersion))) { throw 'Quiet setup did not install the product.' }
    Assert-ProtectedPayload
    Invoke-Setup @('--uninstall', '--quiet') 0
    if (Get-ProductVersion $currentCode) { throw 'Quiet setup uninstall left the product installed.' }
    if ((Get-Content -Raw -LiteralPath (Join-Path $sentinel 'profile.txt')).Trim() -ne 'profile-sentinel') {
        throw 'Setup uninstall changed the profile sentinel.'
    }
    if (-not (Test-Path -LiteralPath $settingsFile)) { throw 'Setup uninstall removed Vestigo settings.' }
    Write-Output 'Setup: quiet install and uninstall passed.'
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    foreach ($code in @($currentCode, $baselineCode)) {
        $log = Join-Path $logs ("cleanup-" + $code.Trim('{}') + '.log')
        $cleanup = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList (Format-CommandLine @('/x', $code, '/qn', '/norestart', '/l*v', $log)) -Wait -PassThru
        if ($cleanup.ExitCode -notin 0, 3010, 1605) {
            Write-Warning "Cleanup uninstall of $code exited $($cleanup.ExitCode). Log: $log"
        }
    }
    foreach ($path in @($settingsFile, $sentinel, $profiles, $artifacts)) {
        if ($path -eq $settingsFile -and -not $createdSettings) { continue }
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    if ($createdSettings -and (Test-Path -LiteralPath $settingsRoot) -and -not (Get-ChildItem -LiteralPath $settingsRoot -Force)) {
        Remove-Item -LiteralPath $settingsRoot -Force
    }
    if (Test-Path -LiteralPath $missingHost) { Remove-Item -LiteralPath $missingHost -Recurse -Force }
}
