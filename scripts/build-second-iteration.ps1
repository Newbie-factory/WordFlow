[CmdletBinding()]
param(
    [string]$ReleaseDirectory = 'D:\baicizhan\release\WordFlow-second-iteration'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FileIdentity {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $item = Get-Item -LiteralPath $LiteralPath
    [ordered]@{
        fileName = $item.Name
        sizeBytes = $item.Length
        sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $item.FullName).Hash.ToLowerInvariant()
    }
}

function Get-TreeHashes {
    param([Parameter(Mandatory)][string]$Root)

    $rootPath = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $hashes = [ordered]@{}
    Get-ChildItem -LiteralPath $rootPath -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($rootPath.Length + 1).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
    }
    return $hashes
}

function Assert-EquivalentTrees {
    param(
        [Parameter(Mandatory)][string]$First,
        [Parameter(Mandatory)][string]$Second
    )

    $firstJson = (Get-TreeHashes -Root $First) | ConvertTo-Json -Compress
    $secondJson = (Get-TreeHashes -Root $Second) | ConvertTo-Json -Compress
    if ($firstJson -cne $secondJson) {
        throw "Published companion content differs between '$First' and '$Second'."
    }
}

function Assert-ExactSafePath {
    param(
        [Parameter(Mandatory)][string]$Actual,
        [Parameter(Mandatory)][string]$Expected
    )

    $actualPath = [System.IO.Path]::GetFullPath($Actual).TrimEnd('\')
    $expectedPath = [System.IO.Path]::GetFullPath($Expected).TrimEnd('\')
    if ($actualPath -cne $expectedPath) {
        throw "Refusing cleanup because '$actualPath' is not the expected path '$expectedPath'."
    }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repositoryRoot 'src\WordFlow.App\WordFlow.App.csproj'
$iconsDirectory = Join-Path $repositoryRoot 'src\WordFlow.App\Assets\Icons'
$photoIcon = Join-Path $iconsDirectory 'wordflow-photo.ico'
$classicIcon = Join-Path $iconsDirectory 'wordflow-classic.ico'
$photoSource = Join-Path $iconsDirectory 'photo-source.png'
$classicSource = Join-Path $iconsDirectory 'wordflow-source.png'
$expectedReleaseDirectory = 'D:\baicizhan\release\WordFlow-second-iteration'

Assert-ExactSafePath -Actual $ReleaseDirectory -Expected $expectedReleaseDirectory
foreach ($requiredPath in @($projectPath, $photoIcon, $classicIcon, $photoSource, $classicSource)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required build input does not exist: $requiredPath"
    }
}

$tempParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\')
$tempRoot = Join-Path $tempParent ("WordFlow-second-iteration-{0}" -f [guid]::NewGuid().ToString('N'))
$photoPublish = Join-Path $tempRoot 'publish-photo'
$classicPublish = Join-Path $tempRoot 'publish-classic'
$stagedRelease = Join-Path $tempRoot 'release'
New-Item -ItemType Directory -Path $photoPublish, $classicPublish, $stagedRelease | Out-Null

try {
    $publishArguments = @(
        'publish', $projectPath,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false'
    )

    & dotnet 'clean' $projectPath '--configuration' 'Release' '--verbosity' 'quiet' '-p:RuntimeIdentifier=win-x64'
    if ($LASTEXITCODE -ne 0) { throw "Photo pre-publish clean failed with exit code $LASTEXITCODE." }
    & dotnet @publishArguments '--output' $photoPublish "-p:WordFlowApplicationIcon=$photoIcon"
    if ($LASTEXITCODE -ne 0) { throw "Photo publish failed with exit code $LASTEXITCODE." }
    & dotnet 'clean' $projectPath '--configuration' 'Release' '--verbosity' 'quiet' '-p:RuntimeIdentifier=win-x64'
    if ($LASTEXITCODE -ne 0) { throw "Classic pre-publish clean failed with exit code $LASTEXITCODE." }
    & dotnet @publishArguments '--output' $classicPublish "-p:WordFlowApplicationIcon=$classicIcon"
    if ($LASTEXITCODE -ne 0) { throw "Classic publish failed with exit code $LASTEXITCODE." }

    $photoPublishedExe = Join-Path $photoPublish 'WordFlow.App.exe'
    $classicPublishedExe = Join-Path $classicPublish 'WordFlow.App.exe'
    foreach ($publishedExe in @($photoPublishedExe, $classicPublishedExe)) {
        if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
            throw "Expected single-file executable was not published: $publishedExe"
        }
    }
    $photoPublishedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $photoPublishedExe).Hash
    $classicPublishedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $classicPublishedExe).Hash
    if ($photoPublishedHash -ceq $classicPublishedHash) {
        throw 'Published executables are identical; the selected application icon was not rebuilt.'
    }

    $photoData = Join-Path $photoPublish 'Data'
    $classicData = Join-Path $classicPublish 'Data'
    if (-not (Test-Path -LiteralPath $photoData -PathType Container) -or
        -not (Test-Path -LiteralPath $classicData -PathType Container)) {
        throw 'Both publishes must contain the shared Data directory.'
    }
    $requiredDataFiles = @(
        'Data\ielts\vocabulary.sqlite3',
        'Data\ielts\relations.sqlite3',
        'Data\ielts\manifest.json',
        'Data\ielts\relations-manifest.json'
    )
    foreach ($relativeDataFile in $requiredDataFiles) {
        foreach ($publishRoot in @($photoPublish, $classicPublish)) {
            $publishedDataFile = Join-Path $publishRoot $relativeDataFile
            if (-not (Test-Path -LiteralPath $publishedDataFile -PathType Leaf)) {
                throw "Required shared data file was not published: $publishedDataFile"
            }
        }
    }
    Assert-EquivalentTrees -First $photoData -Second $classicData

    $photoCompanions = @(Get-ChildItem -LiteralPath $photoPublish -File | Where-Object Name -ne 'WordFlow.App.exe')
    $classicCompanions = @(Get-ChildItem -LiteralPath $classicPublish -File | Where-Object Name -ne 'WordFlow.App.exe')
    if ($photoCompanions.Count -ne 0 -or $classicCompanions.Count -ne 0) {
        throw 'Publish is not self-contained single-file: unexpected top-level companion files were emitted.'
    }

    $photoFinal = Join-Path $stagedRelease 'WordFlow-Photo.exe'
    $classicFinal = Join-Path $stagedRelease 'WordFlow-Classic.exe'
    Copy-Item -LiteralPath $photoPublishedExe -Destination $photoFinal
    Copy-Item -LiteralPath $classicPublishedExe -Destination $classicFinal
    Copy-Item -LiteralPath $photoData -Destination (Join-Path $stagedRelease 'Data') -Recurse

    $commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not determine the Git commit for the release manifest.'
    }

    $photoIdentity = Get-FileIdentity -LiteralPath $photoFinal
    $photoIdentity['variant'] = 'photo'
    $photoIdentity['iconArtifactSha256'] = (Get-FileHash -Algorithm SHA256 -LiteralPath $photoIcon).Hash.ToLowerInvariant()
    $photoIdentity['iconSourceFile'] = 'src/WordFlow.App/Assets/Icons/photo-source.png'
    $photoIdentity['iconSourceSha256'] = (Get-FileHash -Algorithm SHA256 -LiteralPath $photoSource).Hash.ToLowerInvariant()
    $classicIdentity = Get-FileIdentity -LiteralPath $classicFinal
    $classicIdentity['variant'] = 'classic'
    $classicIdentity['iconArtifactSha256'] = (Get-FileHash -Algorithm SHA256 -LiteralPath $classicIcon).Hash.ToLowerInvariant()
    $classicIdentity['iconSourceFile'] = 'src/WordFlow.App/Assets/Icons/wordflow-source.png'
    $classicIdentity['iconSourceSha256'] = (Get-FileHash -Algorithm SHA256 -LiteralPath $classicSource).Hash.ToLowerInvariant()

    $manifest = [ordered]@{
        schemaVersion = 1
        buildTimestampUtc = [DateTimeOffset]::UtcNow.ToString('o')
        commit = $commit
        runtime = 'win-x64'
        selfContained = $true
        singleFile = $true
        executables = @($photoIdentity, $classicIdentity)
        sharedData = [ordered]@{
            relativePath = 'Data'
            files = Get-TreeHashes -Root (Join-Path $stagedRelease 'Data')
        }
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stagedRelease 'release-manifest.json') -Encoding utf8NoBOM

    $releaseParent = Split-Path -Parent $expectedReleaseDirectory
    New-Item -ItemType Directory -Force -Path $releaseParent | Out-Null
    if (Test-Path -LiteralPath $expectedReleaseDirectory) {
        Assert-ExactSafePath -Actual $expectedReleaseDirectory -Expected 'D:\baicizhan\release\WordFlow-second-iteration'
        Remove-Item -LiteralPath $expectedReleaseDirectory -Recurse -Force
    }
    Move-Item -LiteralPath $stagedRelease -Destination $expectedReleaseDirectory

    Get-ChildItem -LiteralPath $expectedReleaseDirectory -File | Sort-Object Name |
        Select-Object Name, Length, @{Name='SHA256'; Expression={(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash}}
}
finally {
    $resolvedTempRoot = [System.IO.Path]::GetFullPath($tempRoot).TrimEnd('\')
    $requiredPrefix = "$tempParent\WordFlow-second-iteration-"
    if (-not $resolvedTempRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing temp cleanup outside the verified task prefix: $resolvedTempRoot"
    }
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
