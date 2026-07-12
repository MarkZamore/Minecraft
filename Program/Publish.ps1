param(
    [string]$SourceRevisionId = "",
    [string]$PublishDir = "",
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
    (Join-Path $projectRoot "Minecraft\Personal\Build")
)

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = $projectRoot
} elseif (-not [System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir = Join-Path $projectRoot $PublishDir
}
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir)

if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    $SourceRevisionId = (& git -C $projectRoot rev-parse HEAD).Trim()
}
if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    throw "Source revision could not be determined."
}

try {
    if (-not $NoRestore) {
        & dotnet restore $projectFile -r win-x64 -p:Configuration=Release
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }
    }

    & dotnet build $projectFile -c Release --no-restore "-p:SourceRevisionId=$SourceRevisionId"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

    # WPF's markup pass creates a temporary assembly. Publishing without a second
    # build guarantees that the final pass-two assembly is the one bundled.
    & dotnet publish $projectFile -c Release --no-build --no-restore "-p:SourceRevisionId=$SourceRevisionId" "-p:PublishDir=$PublishDir"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

    $executable = Join-Path $PublishDir "Minecraft.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Minecraft.exe was not published to $PublishDir."
    }
} finally {
    & dotnet build-server shutdown | Out-Null
    Start-Sleep -Seconds 1
    foreach ($buildRoot in $buildRoots) {
        for ($attempt = 0; $attempt -lt 5 -and (Test-Path -LiteralPath $buildRoot -PathType Container); $attempt++) {
            Remove-Item -LiteralPath ('\\?\' + [System.IO.Path]::GetFullPath($buildRoot)) -Recurse -Force
            if (Test-Path -LiteralPath $buildRoot -PathType Container) {
                Start-Sleep -Milliseconds 500
            }
        }
    }
}

Write-Host "Published: $(Join-Path $PublishDir 'Minecraft.exe')"
