[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$PinnedSha256 = '7D749F6E2C39E6970E4997839DCF6E42FD281F3C2FAE0171D2192BAE8CFA4B51'
$Uri = 'https://en-word.net/downloads/english-wordnet-2025-json.zip'
$RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$DestinationDirectory = Join-Path $RepositoryRoot 'data\sources\oewn'
$Destination = Join-Path $DestinationDirectory 'english-wordnet-2025-json.zip'
$Partial = "$Destination.partial"

if ($ExpectedSha256.ToUpperInvariant() -ne $PinnedSha256) {
    throw "Expected SHA-256 does not match the repository pin: $PinnedSha256"
}

New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    Invoke-WebRequest -Uri $Uri -OutFile $Partial
    $ActualSha256 = (Get-FileHash -LiteralPath $Partial -Algorithm SHA256).Hash
    if ($ActualSha256 -ne $PinnedSha256) {
        throw "OEWN hash mismatch: expected $PinnedSha256, got $ActualSha256"
    }
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Partial)
    try {
        if ($archive.Entries.Count -eq 0 -or -not ($archive.Entries.Name -like 'entries-*.json')) {
            throw 'OEWN archive structure is invalid'
        }
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.Contains('/') -or $entry.FullName.Contains('\') -or $entry.FullName.Contains('..')) {
                throw "Unsafe OEWN archive member: $($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    Move-Item -LiteralPath $Partial -Destination $Destination -Force
}
finally {
    if (Test-Path -LiteralPath $Partial) {
        Remove-Item -LiteralPath $Partial -Force
    }
}

Get-Item -LiteralPath $Destination | Select-Object FullName, Length
Get-FileHash -LiteralPath $Destination -Algorithm SHA256
