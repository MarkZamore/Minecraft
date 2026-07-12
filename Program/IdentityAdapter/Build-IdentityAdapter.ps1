param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$adapterRoot = Split-Path -Parent $PSCommandPath
$programRoot = Split-Path -Parent $adapterRoot
$projectRoot = Split-Path -Parent $programRoot
$sourceRoot = Join-Path $adapterRoot "src"
$manifest = Join-Path $adapterRoot "MANIFEST.MF"
$buildRoot = Join-Path $programRoot "Build\IdentityAdapter"
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

$javac = Find-JavaTool "javac"
$jar = Find-JavaTool "jar"
$javaFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter "*.java" | ForEach-Object FullName)
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

    Write-Host "Identity adapter: $OutputPath"
} finally {
    if (Test-Path -LiteralPath $stageRoot -PathType Container) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
