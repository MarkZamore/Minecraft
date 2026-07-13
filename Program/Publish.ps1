param(
    [string]$SourceRevisionId = "",
    [string]$PublishDir = "",
    [int]$ReleaseNumber = 0,
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

function Get-PositiveInteger([object]$value) {
    $number = 0
    if ($null -ne $value -and [int]::TryParse([string]$value, [ref]$number) -and $number -gt 0) {
        return $number
    }
    return 0
}

function Test-WorktreeIsDirty {
    $status = & git -C $projectRoot status --porcelain --untracked-files=normal 2>$null
    return $LASTEXITCODE -ne 0 -or -not [string]::IsNullOrWhiteSpace(($status -join "`n"))
}

function Get-ExistingExecutableReleaseNumber {
    $existingExecutable = Join-Path $projectRoot "Minecraft.exe"
    if (-not (Test-Path -LiteralPath $existingExecutable -PathType Leaf)) {
        return 0
    }

    try {
        $version = (Get-Item -LiteralPath $existingExecutable).VersionInfo.FileVersionRaw
        if ($null -ne $version -and $version.Build -gt 0) {
            return $version.Build
        }
    } catch {
        Write-Verbose "The existing executable version could not be read: $($_.Exception.Message)"
    }
    return 0
}

function Resolve-ReleaseNumber([string]$revision) {
    $environmentNumber = Get-PositiveInteger $env:RELEASE_NUMBER
    if ($environmentNumber -gt 0) {
        return $environmentNumber
    }

    $isDirty = Test-WorktreeIsDirty
    try {
        $manifest = Invoke-RestMethod `
            -Uri "https://github.com/MarkZamore/Minecraft/releases/latest/download/update.json" `
            -TimeoutSec 10
        $latestNumber = Get-PositiveInteger $manifest.releaseNumber
        $latestCommit = ([string]$manifest.commitSha).Trim()
        if ($latestNumber -gt 0) {
            if (-not $isDirty -and $latestCommit.Equals($revision, [StringComparison]::OrdinalIgnoreCase)) {
                return $latestNumber
            }
            return $latestNumber + 1
        }
    } catch {
        Write-Warning "The latest release number is unavailable; using the local executable as fallback. $($_.Exception.Message)"
    }

    $existingNumber = Get-ExistingExecutableReleaseNumber
    if ($existingNumber -gt 0) {
        return $(if ($isDirty) { $existingNumber + 1 } else { $existingNumber })
    }
    return 1
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
if ([string]::IsNullOrWhiteSpace($SourceRevisionId)) {
    throw "Source revision could not be determined."
}
if ($ReleaseNumber -lt 0) {
    throw "ReleaseNumber cannot be negative."
}
if ($ReleaseNumber -eq 0) {
    $ReleaseNumber = Resolve-ReleaseNumber $SourceRevisionId
}
Write-Host "Release number: $ReleaseNumber"

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
