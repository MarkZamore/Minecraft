param(
    [string]$SourceRevisionId = "",
    [string]$PublishDir = "",
    [int]$ReleaseNumber = 1,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$env:MSBUILDDISABLENODEREUSE = "1"
$programDir = Split-Path -Parent $PSCommandPath
$projectRoot = Split-Path -Parent $programDir
$projectFile = Join-Path $programDir "Minecraft.csproj"
$buildRoots = @(
    (Join-Path $programDir "bin"),
    (Join-Path $programDir "obj"),
    (Join-Path $programDir "Build"),
    (Join-Path $programDir "Patch\bin"),
    (Join-Path $programDir "Patch\obj"),
    (Join-Path $projectRoot "Minecraft\Personal\Build")
)

function Remove-BuildRoot([string]$buildRoot, [bool]$throwOnFailure) {
    $lastCleanupError = $null
    for ($attempt = 0; $attempt -lt 5 -and (Test-Path -LiteralPath $buildRoot -PathType Container); $attempt++) {
        try {
            Remove-Item -LiteralPath ('\\?\' + [System.IO.Path]::GetFullPath($buildRoot)) -Recurse -Force -ErrorAction Stop
            $lastCleanupError = $null
        } catch {
            $lastCleanupError = $_
        }
        if (Test-Path -LiteralPath $buildRoot -PathType Container) {
            Start-Sleep -Milliseconds 500
        }
    }
    if ($throwOnFailure -and (Test-Path -LiteralPath $buildRoot -PathType Container)) {
        $detail = if ($lastCleanupError) { $lastCleanupError.Exception.Message } else { "The directory was recreated after cleanup." }
        throw "Build output could not be removed: $buildRoot. $detail"
    }
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = $projectRoot
} elseif (-not [System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir = Join-Path $projectRoot $PublishDir
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)

if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $SourceRevisionId = (& git -C $projectRoot rev-parse HEAD).Trim()
}
if ($ReleaseNumber -lt 1) {
    throw "ReleaseNumber must be at least 1."
}
if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    throw "Source revision could not be determined."
}

try {
    if (-not $NoRestore) {
        & dotnet restore $projectFile -r win-x64 -p:Configuration=Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    }

    & dotnet build $projectFile -c Release --no-restore "-p:SourceRevisionId=$SourceRevisionId" "-p:ReleaseNumber=$ReleaseNumber"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

    # WPF's markup pass creates a temporary assembly. Publishing without a second
    # build guarantees that the final pass-two assembly is the one bundled.
    & dotnet publish $projectFile -c Release --no-build --no-restore "-p:SourceRevisionId=$SourceRevisionId" "-p:ReleaseNumber=$ReleaseNumber" "-p:PublishDir=$PublishDir"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $executable = Join-Path $PublishDir "Minecraft.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Minecraft.exe was not published to $PublishDir."
    }
} finally {
    & dotnet build-server shutdown | Out-Null
    for ($cleanupPass = 0; $cleanupPass -lt 3; $cleanupPass++) {
        Start-Sleep -Seconds 1
        foreach ($buildRoot in $buildRoots) {
            Remove-BuildRoot $buildRoot ($cleanupPass -eq 2)
        }
    }
}

Write-Host "Published: $(Join-Path $PublishDir 'Minecraft.exe')"
