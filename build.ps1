param(
    [string]$GamePath,
    [string]$SmapiArchive,
    [string]$Dotnet = "dotnet",
    [string]$Python = "python",
    [switch]$SkipSetup,
    [switch]$Test
)
$ErrorActionPreference = "Stop"

function Invoke-Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program exited with $LASTEXITCODE"
    }
}

$versions = Get-Content -LiteralPath (Join-Path $PSScriptRoot "package/versions.json") -Raw | ConvertFrom-Json
$buildRoot = Join-Path $PSScriptRoot ".artifacts"
$package = Join-Path $PSScriptRoot "package"
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

if (!$GamePath -or $Test) {
    if (!$SmapiArchive) {
        $SmapiArchive = Join-Path $buildRoot "SMAPI-$($versions.smapiVersion)-installer.zip"
        if (!(Test-Path -LiteralPath $SmapiArchive)) {
            Invoke-WebRequest -Uri $versions.smapiUrl -OutFile "$SmapiArchive.partial"
            Move-Item -LiteralPath "$SmapiArchive.partial" -Destination $SmapiArchive
        }
    }
    $SmapiArchive = (Resolve-Path -LiteralPath $SmapiArchive).Path
    if ((Get-FileHash -LiteralPath $SmapiArchive -Algorithm SHA256).Hash -ne $versions.smapiSha256) {
        throw "SHA-256 mismatch for the official SMAPI archive: $SmapiArchive"
    }
}

$mod = Join-Path $PSScriptRoot "StokHouse_Source/Mod/StokHouse.csproj"
$modTests = Join-Path $PSScriptRoot "StokHouse_Source/Tests/StokHouse.Core.Tests.csproj"
$setup = Join-Path $PSScriptRoot "StokHouseSetup_Source/Setup/StokHouseSetup.csproj"
$setupTests = Join-Path $PSScriptRoot "StokHouseSetup_Source/Tests/StokHouse.Setup.Tests.csproj"
$packScript = Join-Path $package "pack.py"
$setupOut = Join-Path $package "setup"

if ($GamePath) {
    $GamePath = (Resolve-Path -LiteralPath $GamePath).Path
    if (!(Test-Path (Join-Path $GamePath "Stardew Valley.dll")) -or
        !(Test-Path (Join-Path $GamePath "StardewModdingAPI.dll"))) {
        throw "Укажите папку Stardew Valley с установленным SMAPI."
    }
    $reference = "-p:GamePath=$GamePath"
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $runtimeZip = Join-Path $buildRoot "smapi-windows.zip"
    $runtimeFiles = Join-Path $buildRoot "smapi-windows"
    $archive = [System.IO.Compression.ZipFile]::OpenRead($SmapiArchive)
    try {
        $entry = @($archive.Entries | Where-Object FullName -like "*/internal/windows/install.dat")
        if ($entry.Count -ne 1) {
            throw "Expected exactly one SMAPI Windows runtime bundle."
        }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry[0], $runtimeZip, $true)
    } finally {
        $archive.Dispose()
    }
    if (Test-Path -LiteralPath $runtimeFiles) {
        Remove-Item -LiteralPath $runtimeFiles -Recurse -Force
    }
    Expand-Archive -LiteralPath $runtimeZip -DestinationPath $runtimeFiles
    $reference = "-p:SmapiPath=$runtimeFiles"
}

Invoke-Native $Dotnet @("build", $mod, "-c", "Release", $reference)
Invoke-Native $Python @($packScript)
if ($Test) {
    Invoke-Native $Dotnet @("run", "--project", $modTests, "-c", "Release")
    Invoke-Native $Dotnet @("run", "--project", $setupTests, "-c", "Release", "--", $package, $SmapiArchive)
}

if (!$SkipSetup) {
    Invoke-Native $Dotnet @(
        "publish", $setup, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", $setupOut
    )
    if ($Test -and [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $smoke = Start-Process -FilePath (Join-Path $setupOut "StokHouseSetup.exe") `
            -ArgumentList "--verify-package" -Wait -PassThru
        if ($smoke.ExitCode -ne 0) {
            throw "StokHouseSetup.exe failed embedded package verification (exit $($smoke.ExitCode))."
        }
    }
}
