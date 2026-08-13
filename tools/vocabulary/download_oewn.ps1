[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$RegistryPath = Join-Path $RepositoryRoot 'data\curated\source_registry.json'
$Policy = (Get-Content -LiteralPath $RegistryPath -Raw | ConvertFrom-Json).sources.oewn_2025
$PinnedSha256 = [string]$Policy.sha256
$PinnedBytes = [int64]$Policy.bytes
$Uri = [string]$Policy.url
if ($Policy.name -ne 'Open English WordNet 2025 core JSON edition' -or
    $Policy.url -ne 'https://en-word.net/downloads/english-wordnet-2025-json.zip' -or
    $Policy.edition -ne '2025 core (without Namenet)' -or
    $Policy.role -ne 'Sense, POS, synset, antonym and derivational evidence for published lexical relations' -or
    $Policy.license -ne 'CC BY 4.0 with underlying Princeton WordNet license; see data/licenses/OEWN-2025-LICENSE.md and data/licenses/WORDNET-LICENSE.txt' -or
    $PinnedSha256 -ne '7D749F6E2C39E6970E4997839DCF6E42FD281F3C2FAE0171D2192BAE8CFA4B51' -or
    $PinnedBytes -ne 9986555) {
    throw 'Repository OEWN registry policy is invalid'
}
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
    if ((Get-Item -LiteralPath $Partial).Length -ne $PinnedBytes) {
        throw "OEWN byte count mismatch: expected $PinnedBytes"
    }
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
