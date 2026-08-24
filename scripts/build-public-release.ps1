[CmdletBinding()]
param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $artifactsRoot 'public-release'
}

$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$requiredPrefix = "$artifactsRoot\"
if (-not $outputPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory must remain below '$artifactsRoot'."
}

$projectPath = Join-Path $repositoryRoot 'src\WordFlow.App\WordFlow.App.csproj'
$iconPath = Join-Path $repositoryRoot 'src\WordFlow.App\Assets\Icons\wordflow-classic.ico'
$stagePath = Join-Path $artifactsRoot ('.public-release-stage-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    & dotnet publish $projectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $stagePath `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:WordFlowApplicationIcon=$iconPath"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishedExe = Join-Path $stagePath 'WordFlow.App.exe'
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw "Published executable was not found: $publishedExe"
    }
    Move-Item -LiteralPath $publishedExe -Destination (Join-Path $stagePath 'WordFlow.exe')

    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Recurse -Force
    }
    Move-Item -LiteralPath $stagePath -Destination $outputPath
    $stagePath = $null

    Get-ChildItem -LiteralPath $outputPath -Recurse -File |
        Sort-Object FullName |
        Select-Object FullName, Length
}
finally {
    if ($stagePath -and (Test-Path -LiteralPath $stagePath)) {
        $resolvedStage = [System.IO.Path]::GetFullPath($stagePath)
        if (-not $resolvedStage.StartsWith("$artifactsRoot\.public-release-stage-", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean an unexpected staging path: $resolvedStage"
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
