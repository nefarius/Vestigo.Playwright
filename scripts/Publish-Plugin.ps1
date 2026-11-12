#Requires -Version 7.4
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\plugin')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\Vestigo.Playwright\Vestigo.Playwright.csproj'
dotnet publish $project -c Release -o $Output --nologo
if ($LASTEXITCODE -ne 0) { throw "Plugin publish failed with exit code $LASTEXITCODE." }

$published = Resolve-Path -LiteralPath $Output
foreach ($name in @(
    'Vestigo.Abstractions.dll',
    'ModelContextProtocol.dll', 'ModelContextProtocol.Core.dll', 'ModelContextProtocol.AspNetCore.dll',
    'Microsoft.Extensions.AI.Abstractions.dll')) {
    if (Test-Path -LiteralPath (Join-Path $published $name)) {
        throw "Published plugin still contains the shared host assembly $name."
    }
}
foreach ($required in @('plugin.json', 'Vestigo.Playwright.dll', 'Vestigo.Playwright.deps.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $published $required))) {
        throw "Published plugin is missing $required."
    }
}
Write-Output $published.Path
