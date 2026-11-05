[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$NativeDirectory = Join-Path $ProjectRoot 'native'
$Destination = Join-Path $NativeDirectory 'Quorum-External-Module.dll'
$ExpectedSha256 = 'f2c2999ab2410c4da5169d4abbe9e083f39bdec7c6f5580438618d6771bb7bf6'
$DownloadUri = 'https://raw.githubusercontent.com/fguy06790-code/External/b2124f20ada06b595d1062064ef4c5af2d42add9/Quorum-External-Module.dll'

New-Item -ItemType Directory -Path $NativeDirectory -Force | Out-Null
if (Test-Path -LiteralPath $Destination) {
    $ExistingHash = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExistingHash -eq $ExpectedSha256) {
        Write-Host 'The pinned native module is already present and verified.'
        return
    }
}

$TemporaryFile = Join-Path $NativeDirectory ([System.IO.Path]::GetRandomFileName())
try {
    Invoke-WebRequest -Uri $DownloadUri -OutFile $TemporaryFile
    $DownloadedHash = (Get-FileHash -LiteralPath $TemporaryFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($DownloadedHash -ne $ExpectedSha256) {
        throw "Native module checksum mismatch. Expected $ExpectedSha256; got $DownloadedHash."
    }
    Move-Item -LiteralPath $TemporaryFile -Destination $Destination -Force
    Write-Host "Downloaded and verified $Destination"
}
finally {
    if (Test-Path -LiteralPath $TemporaryFile) {
        Remove-Item -LiteralPath $TemporaryFile -Force
    }
}

