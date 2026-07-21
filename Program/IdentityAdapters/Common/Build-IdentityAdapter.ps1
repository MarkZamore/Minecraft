param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    [string]$AdapterName,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$commonRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSCommandPath))
$adaptersRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $commonRoot))
$programRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $adaptersRoot))
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $programRoot))
$adaptersPrefix = $adaptersRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$adapterRoot = [System.IO.Path]::GetFullPath((Join-Path $adaptersRoot $AdapterName))
if ($AdapterName -eq "Common" -or
    -not $adapterRoot.StartsWith($adaptersPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Identity adapter name escapes the adapters directory: $AdapterName"
}
if (-not (Test-Path -LiteralPath $adapterRoot -PathType Container)) {
    throw "Identity adapter source directory was not found: $adapterRoot"
}

$manifest = Join-Path $commonRoot "MANIFEST.MF"
$buildRoot = Join-Path $programRoot "Build\IdentityAdapters\$AdapterName"
$stageRoot = Join-Path $buildRoot ("stage-" + [Environment]::ProcessId + "-" + [Guid]::NewGuid().ToString("N"))
$classes = Join-Path $stageRoot "classes"
$temporaryJar = Join-Path $stageRoot "portable-identity-adapter.jar"
$backupJar = Join-Path $stageRoot "previous-portable-identity-adapter.jar"

function Find-JavaTool([string]$name) {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $candidate = Join-Path $env:JAVA_HOME "bin\$name.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    $command = Get-Command "$name.exe" -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $runtimeRoot = Join-Path $projectRoot "Minecraft\Launcher\Runtimes"
    if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $runtimeRoot -Recurse -File -Filter "$name.exe" |
            Where-Object { $_.FullName -match '\\runtime\\' } |
            Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw "Java 21 $name.exe was not found. Install a JDK 21 or prepare a local runtime first."
}

foreach ($requiredFile in @(
    (Join-Path $commonRoot "PortableIdentityAgent.java"),
    (Join-Path $commonRoot "PortableIdentityReflection.java"),
    (Join-Path $commonRoot "PortableLanTitleHooks.java"),
    (Join-Path $commonRoot "PortableLanTitleTransformer.java"),
    (Join-Path $adapterRoot "PortableIdentityHooks.java"),
    (Join-Path $adapterRoot "PortableIdentityTransformer.java"),
    $manifest
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required identity adapter file was not found: $requiredFile"
    }
}

$javac = Find-JavaTool "javac"
$jar = Find-JavaTool "jar"
$javaFiles = @(
    Get-ChildItem -LiteralPath $commonRoot -Recurse -File -Filter "*.java"
    Get-ChildItem -LiteralPath $adapterRoot -Recurse -File -Filter "*.java"
) | Sort-Object FullName | ForEach-Object FullName
if ($javaFiles.Count -eq 0) { throw "Identity adapter Java sources were not found." }

try {
    New-Item -ItemType Directory -Path $classes -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null

    & $javac `
        -source 21 `
        -target 21 `
        --add-exports "java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED" `
        --add-exports "java.base/jdk.internal.org.objectweb.asm.tree=ALL-UNNAMED" `
        -d $classes `
        @javaFiles
    if ($LASTEXITCODE -ne 0) { throw "Identity adapter javac failed with exit code $LASTEXITCODE." }

    & $jar cfm $temporaryJar $manifest -C $classes .
    if ($LASTEXITCODE -ne 0) { throw "Identity adapter jar failed with exit code $LASTEXITCODE." }

    $destination = [System.IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        [System.IO.File]::Replace($temporaryJar, $destination, $backupJar, $true)
        Remove-Item -LiteralPath $backupJar -Force -ErrorAction SilentlyContinue
    } else {
        try {
            [System.IO.File]::Move($temporaryJar, $destination)
        } catch [System.IO.IOException] {
            if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) { throw }
            [System.IO.File]::Replace($temporaryJar, $destination, $backupJar, $true)
            Remove-Item -LiteralPath $backupJar -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Identity adapter ${AdapterName}: $OutputPath"
} finally {
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
