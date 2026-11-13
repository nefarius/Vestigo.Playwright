#Requires -Version 7.4
param(
    [string]$PluginOutput = (Join-Path $PSScriptRoot '..\artifacts\plugin'),
    [string]$Version,
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\installer'),
    [switch]$SkipPublish,
    [switch]$MsiOnly,
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$UpgradeCode = '6f3c8e21-4b7a-4d19-8c52-9e1a0d6b7f24'
$KnownProductCodes = @{
    '0.9.0' = 'bb464d5a-9211-5bb9-b1ed-20832bbbc134'
    '1.0.0' = '6a3efdd9-227a-5d27-8282-6cf022f3a59d'
}

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'Publish-Plugin.ps1') -Output $PluginOutput
}

$payload = (Resolve-Path -LiteralPath $PluginOutput).Path
foreach ($required in @('plugin.json', 'Vestigo.Playwright.dll', 'Vestigo.Playwright.deps.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Plugin payload is missing $required." }
}
$shared = @(
    'Vestigo.Abstractions.dll',
    'ModelContextProtocol.dll', 'ModelContextProtocol.Core.dll', 'ModelContextProtocol.AspNetCore.dll',
    'Microsoft.Extensions.AI.Abstractions.dll')
foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
    if ($shared -contains $file.Name) { throw "Plugin payload still contains the shared host assembly $($file.Name)." }
}

if (-not $Version) {
    $pluginManifest = Get-Content -Raw -LiteralPath (Join-Path $payload 'plugin.json') | ConvertFrom-Json
    $Version = [string]$pluginManifest.version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Installer version '$Version' must be major.minor.build." }

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$outDir = (Resolve-Path -LiteralPath $Output).Path
$msi = Join-Path $outDir "Vestigo.Playwright-$Version.msi"
$builder = Join-Path $root 'installer\Vestigo.Playwright.Installer\Vestigo.Playwright.Installer.csproj'
dotnet run --project $builder -c Release -- --payload $payload --version $Version --output $msi
if (-not (Test-Path -LiteralPath $msi)) { throw "WiX did not produce $msi." }

$manifestPath = [IO.Path]::ChangeExtension($msi, '.manifest.json')
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
if ($manifest.sha256 -ne $hash) { throw 'The MSI manifest hash does not match the file.' }
if ($manifest.productVersion -ne $Version) { throw "The MSI manifest version is $($manifest.productVersion)." }
if ([guid]$manifest.upgradeCode -ne [guid]$UpgradeCode) { throw 'The MSI manifest upgrade code is not the Playwright upgrade code.' }
if ($KnownProductCodes.ContainsKey($Version) -and [guid]$manifest.productCode -ne [guid]$KnownProductCodes[$Version]) {
    throw "The product code for $Version changed."
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.OpenDatabase($msi, 0)
function Get-MsiProperty([string]$Name) {
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$Name'")
    $null = $view.Execute()
    $record = $view.Fetch()
    if (-not $record) { throw "The MSI is missing property $Name." }
    $value = [string]$record.StringData(1)
    $null = $view.Close()
    return $value
}
$productCode = Get-MsiProperty 'ProductCode'
$upgradeCode = Get-MsiProperty 'UpgradeCode'
$productVersion = Get-MsiProperty 'ProductVersion'
if ([guid]$productCode -ne [guid]$manifest.productCode) { throw "The MSI product code is $productCode." }
if ([guid]$upgradeCode -ne [guid]$UpgradeCode) { throw "The MSI upgrade code is $upgradeCode." }
function Convert-ProductVersion([string]$Text) {
    $parsed = [version]$Text
    $build = if ($parsed.Build -lt 0) { 0 } else { $parsed.Build }
    return "$($parsed.Major).$($parsed.Minor).$build"
}
if ((Convert-ProductVersion $productVersion) -ne (Convert-ProductVersion $Version)) {
    throw "The MSI product version is $productVersion."
}
$summary = $database.SummaryInformation(0)
$template = [string]$summary.Property(7)
if ($template -notlike '*x64*') { throw "The MSI platform is '$template'." }

function Assert-SignatureState([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($RequireSignature) {
        if ($signature.Status -ne 'Valid') { throw "$Path is not signed (status $($signature.Status))." }
        return
    }
    if ($signature.Status -notin @('NotSigned', 'Valid')) {
        throw "$Path has signature status $($signature.Status)."
    }
}
Assert-SignatureState $msi

if ($MsiOnly) {
    Write-Output $msi
    return
}

$setupProject = Join-Path $root 'installer\Vestigo.Playwright.Setup\Vestigo.Playwright.Setup.csproj'
$publish = Join-Path $outDir 'setup-publish'
dotnet publish $setupProject -c Release -r win-x64 --self-contained true -o $publish `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:SetupMsiPath=$msi `
    -p:SetupMsiHash=$hash `
    -p:SetupProductVersion=$Version
$published = Join-Path $publish 'Vestigo.Playwright.Setup.exe'
if (-not (Test-Path -LiteralPath $published)) { throw "Setup publish did not produce $published." }
$setup = Join-Path $outDir "Vestigo.Playwright.Setup-$Version.exe"
Copy-Item -LiteralPath $published -Destination $setup -Force
Assert-SignatureState $setup
Write-Output $msi
Write-Output $setup
