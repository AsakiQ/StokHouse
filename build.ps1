param(
    [Parameter(Mandatory = $true)][string]$GamePath,
    [string]$Dotnet = "dotnet",
    [string]$Python = "python",
    [switch]$SkipSetup
)
$ErrorActionPreference = "Stop"

function Invoke-Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program exited with $LASTEXITCODE"
    }
}

$GamePath = (Resolve-Path -LiteralPath $GamePath).Path
if (!(Test-Path (Join-Path $GamePath "Stardew Valley.dll")) -or
    !(Test-Path (Join-Path $GamePath "StardewModdingAPI.dll"))) {
    throw "Укажите папку Stardew Valley 1.6.15 с установленным SMAPI."
}
$mod = Join-Path $PSScriptRoot "StokHouse_Source/Mod/StokHouse.csproj"
$setup = Join-Path $PSScriptRoot "StokHouseSetup_Source/Setup/StokHouseSetup.csproj"
$packScript = Join-Path $PSScriptRoot "package/pack.py"
$setupOut = Join-Path $PSScriptRoot "package/setup"

Invoke-Native $Dotnet @("build", $mod, "-c", "Release", "-p:GamePath=$GamePath")
Invoke-Native $Python @($packScript)
if (!$SkipSetup) {
    Invoke-Native $Dotnet @(
        "publish", $setup, "-c", "Release", "-r", "win-x64", "--self-contained", "true",
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true", "-p:DebugType=none", "-o", $setupOut
    )
}
