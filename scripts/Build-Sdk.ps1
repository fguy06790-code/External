[CmdletBinding()]
param(
    # Use only after building ExternalExecutor.Core in Release configuration.
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$CoreProject = Join-Path $ProjectRoot 'src/ExternalExecutor.Core/ExternalExecutor.Core.csproj'
$CoreOutput = Join-Path $ProjectRoot 'src/ExternalExecutor.Core/bin/Release/net10.0'
$NativeModule = Join-Path $ProjectRoot 'native/Quorum-External-Module.dll'
$ExpectedSha256 = 'f2c2999ab2410c4da5169d4abbe9e083f39bdec7c6f5580438618d6771bb7bf6'
$SdkDirectory = Join-Path $ProjectRoot 'artifacts/sdk'
$PackagesDirectory = Join-Path $SdkDirectory 'packages'
$ArchivePath = Join-Path $ProjectRoot 'artifacts/RYN-Executor-API-SDK.zip'

if (-not (Test-Path -LiteralPath $NativeModule -PathType Leaf)) {
    throw 'Native module missing. Run ./scripts/Fetch-Module.ps1 first.'
}
if ((Get-FileHash -LiteralPath $NativeModule -Algorithm SHA256).Hash.ToLowerInvariant() -ne $ExpectedSha256) {
    throw 'Native module differs from the pinned version. Run ./scripts/Fetch-Module.ps1 to restore it.'
}

[xml]$CoreProjectXml = Get-Content -LiteralPath $CoreProject -Raw
$VersionNode = $CoreProjectXml.SelectSingleNode('/Project/PropertyGroup/Version')
$PackageIdNode = $CoreProjectXml.SelectSingleNode('/Project/PropertyGroup/PackageId')
if ($null -eq $VersionNode -or $null -eq $PackageIdNode) {
    throw 'Core project must declare Version and PackageId before packaging the SDK.'
}
$Version = $VersionNode.InnerText
$PackageId = $PackageIdNode.InnerText
$CoreAssembly = Join-Path $CoreOutput 'ExternalExecutor.Core.dll'
if ($SkipBuild -and -not (Test-Path -LiteralPath $CoreAssembly -PathType Leaf)) {
    throw 'Release Core assembly missing. Build Core first, or run Build-Sdk.ps1 without -SkipBuild.'
}

if (Test-Path -LiteralPath $SdkDirectory) {
    Remove-Item -LiteralPath $SdkDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $PackagesDirectory -Force | Out-Null
$PackArguments = @('pack', $CoreProject, '-c', 'Release', '-o', $PackagesDirectory)
if ($SkipBuild) {
    # Publish.ps1 suppresses PDB output. Pack must use the same symbol settings.
    $PackArguments += @('--no-build', '-p:DebugType=None', '-p:DebugSymbols=false')
}
& dotnet @PackArguments
if ($LASTEXITCODE -ne 0) { throw 'SDK NuGet package build failed.' }

$PackagePath = Join-Path $PackagesDirectory "$PackageId.$Version.nupkg"
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Expected NuGet package missing: $PackagePath"
}
if (-not (Test-Path -LiteralPath $CoreAssembly -PathType Leaf)) {
    throw "Release Core assembly missing: $CoreAssembly"
}

$LibraryDirectory = Join-Path $SdkDirectory 'lib'
$NativeDirectory = Join-Path $SdkDirectory 'native'
$ExampleDirectory = Join-Path $SdkDirectory 'example'
foreach ($Directory in @($LibraryDirectory, $NativeDirectory, $ExampleDirectory)) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
}
Copy-Item -LiteralPath $CoreAssembly -Destination $LibraryDirectory
$XmlDocumentation = Join-Path $CoreOutput 'ExternalExecutor.Core.xml'
if (Test-Path -LiteralPath $XmlDocumentation -PathType Leaf) {
    Copy-Item -LiteralPath $XmlDocumentation -Destination $LibraryDirectory
}
Copy-Item -LiteralPath $NativeModule -Destination $NativeDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'native/Tutorial.txt') -Destination $NativeDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'native/PROVENANCE.md') -Destination $NativeDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'SDK.md') -Destination $SdkDirectory
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'licenses') -Destination $SdkDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $ProjectRoot 'examples/SdkConsole/Program.cs') -Destination $ExampleDirectory

# The source example references the development project. The distributed example
# restores the bundled package, without depending on the source checkout.
[xml]$ExampleProject = Get-Content -LiteralPath (Join-Path $ProjectRoot 'examples/SdkConsole/SdkConsole.csproj') -Raw
$ProjectReferences = @($ExampleProject.SelectNodes('/Project/ItemGroup/ProjectReference'))
if ($ProjectReferences.Count -ne 1) {
    throw 'The SDK example must contain exactly one development ProjectReference.'
}
$PackageReference = $ExampleProject.CreateElement('PackageReference')
$PackageReference.SetAttribute('Include', $PackageId)
$PackageReference.SetAttribute('Version', $Version)
$null = $ProjectReferences[0].ParentNode.ReplaceChild($PackageReference, $ProjectReferences[0])
$ExampleNativeContent = $ExampleProject.SelectSingleNode('/Project/ItemGroup/Content')
if ($null -eq $ExampleNativeContent) {
    throw 'The SDK example must include the native module as content.'
}
$ExampleNativeContent.SetAttribute('Include', '../native/Quorum-External-Module.dll')
$ExampleNativeContent.SetAttribute('Condition', "Exists('../native/Quorum-External-Module.dll')")
$ExampleProject.Save((Join-Path $ExampleDirectory 'SdkConsole.csproj'))

@'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="RYN bundled packages" value="./packages" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $SdkDirectory 'NuGet.Config') -Encoding UTF8

if (Test-Path -LiteralPath $ArchivePath) {
    Remove-Item -LiteralPath $ArchivePath -Force
}
Compress-Archive -Path (Join-Path $SdkDirectory '*') -DestinationPath $ArchivePath
Write-Host "RYN Executor API SDK ${Version}: $ArchivePath"
Write-Host 'Extract the archive, then run: dotnet run --project example'
Write-Host 'Publish.ps1 runs the Core, SDK and module checks before building both release archives.'
