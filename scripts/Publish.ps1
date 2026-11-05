[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$NativeModule = Join-Path $ProjectRoot 'native/Quorum-External-Module.dll'
$ExpectedSha256 = 'f2c2999ab2410c4da5169d4abbe9e083f39bdec7c6f5580438618d6771bb7bf6'
$OutputDirectory = Join-Path $ProjectRoot 'artifacts/win-x64'
$ArchivePath = Join-Path $ProjectRoot 'artifacts/ExternalExecutor-win-x64.zip'
$AppProject = Join-Path $ProjectRoot 'src/ExternalExecutor.App/ExternalExecutor.App.csproj'
$ChecksProjects = @(
    'tests/ExternalExecutor.Core.Checks/ExternalExecutor.Core.Checks.csproj'
    'tests/ExternalExecutor.Sdk.Checks/ExternalExecutor.Sdk.Checks.csproj'
    'tests/ExternalExecutor.Module.Checks/ExternalExecutor.Module.Checks.csproj'
)

if (-not (Test-Path -LiteralPath $NativeModule)) {
    throw 'Native module missing. Run ./scripts/Fetch-Module.ps1 first.'
}
$ModuleHash = (Get-FileHash -LiteralPath $NativeModule -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ModuleHash -ne $ExpectedSha256) {
    throw 'Native module differs from the pinned version. Run ./scripts/Fetch-Module.ps1 to restore it.'
}

foreach ($ChecksProject in $ChecksProjects) {
    & dotnet run --project (Join-Path $ProjectRoot $ChecksProject) -c Release
    if ($LASTEXITCODE -ne 0) { throw "Checks failed: $ChecksProject" }
}

if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
& dotnet publish $AppProject -c Release -r win-x64 --self-contained true -o $OutputDirectory -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

$PublishedEntryPage = Join-Path $OutputDirectory 'Assets/index.html'
if (-not (Test-Path -LiteralPath $PublishedEntryPage)) {
    throw 'Frontend entry page was not published. Check the Assets content settings in the app project.'
}
$WebViewLoader = Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter 'WebView2Loader.dll' | Select-Object -First 1
if ($null -eq $WebViewLoader) {
    throw 'The native WebView2 loader was not published. Check the WebView2 package reference.'
}

Copy-Item -LiteralPath $NativeModule -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'README.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'SDK.md') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'sample.lua') -Destination $OutputDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'licenses') -Destination $OutputDirectory -Recurse
if (Test-Path -LiteralPath (Join-Path $ProjectRoot 'VALIDATION.md')) {
    Copy-Item -LiteralPath (Join-Path $ProjectRoot 'VALIDATION.md') -Destination $OutputDirectory
}
$NativeDocsDirectory = Join-Path $OutputDirectory 'native'
New-Item -ItemType Directory -Path $NativeDocsDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'native/PROVENANCE.md') -Destination $NativeDocsDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'native/Tutorial.txt') -Destination $NativeDocsDirectory

if (Test-Path -LiteralPath $ArchivePath) {
    Remove-Item -LiteralPath $ArchivePath -Force
}
Compress-Archive -Path (Join-Path $OutputDirectory '*') -DestinationPath $ArchivePath

# The checks above already built the Core SDK in Release configuration.
& (Join-Path $PSScriptRoot 'Build-Sdk.ps1') -SkipBuild

Write-Host "RYN Executor Suite Windows x64 release: $ArchivePath"
Write-Host 'The target PC also needs the Microsoft Edge WebView2 Evergreen Runtime.'
