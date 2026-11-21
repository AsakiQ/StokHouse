param(
    [Parameter(Mandatory)][string]$SmapiPath,
    [Parameter(Mandatory)][string]$RollbackPackage,
    [string]$GamePath,
    [string]$Dotnet = "dotnet",
    [string]$Python = "python",
    [string]$NuGetSource
)
$ErrorActionPreference = 'Stop'

function Invoke-Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Program exited with $LASTEXITCODE"
    }
}

$SmapiPath = (Resolve-Path -LiteralPath $SmapiPath).Path
$RollbackPackage = (Resolve-Path -LiteralPath $RollbackPackage).Path
$stable = Get-Content -LiteralPath (Join-Path $RollbackPackage 'modpack.json') -Raw | ConvertFrom-Json
$stableZip = Join-Path $RollbackPackage 'StokHouse-mod.zip'
if ($stable.modVersion -cne '0.1.3-alpha' -or (Get-FileHash -LiteralPath $stableZip).Hash -ne $stable.modSha256) {
    throw 'The rollback package must be a verified 0.1.3-alpha build.'
}

$output = Join-Path $PSScriptRoot '.artifacts/family-budget-playtest'
$modOutput = Join-Path $output 'mod'
$payload = Join-Path $output 'payload'
$setupOutput = Join-Path $output 'setup'
$mod = Join-Path $PSScriptRoot 'StokHouse_Source/Mod/StokHouse.csproj'
$setup = Join-Path $PSScriptRoot 'StokHouseSetup_Source/Playtest/StokHouse.FamilyBudget.Setup.csproj'
$extra = @()
if ($NuGetSource) { $extra += "-p:RestoreSources=$((Resolve-Path -LiteralPath $NuGetSource).Path)" }
$references = @("-p:SmapiPath=$SmapiPath")
if ($GamePath) { $references += "-p:GamePath=$((Resolve-Path -LiteralPath $GamePath).Path)" }
Invoke-Native $Dotnet (@('build', $mod, '-c', 'Release', '-p:FamilyBudgetPlaytest=true', '-o', $modOutput) + $references + $extra)

$manifestPath = Join-Path $modOutput 'manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.Name = 'Stok House — Family Budget Playtest'
$manifest.Version = '0.1.4-family-budget-playtest.1'
$manifest.Description = 'Экспериментальный семейный бюджет. Только для новой тестовой фермы.'
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
Invoke-Native $Python @((Join-Path $PSScriptRoot 'package/pack.py'), $modOutput, $payload)

Invoke-Native $Dotnet (@('publish', $setup, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none',
    "-p:PlaytestPayload=$payload", "-p:RollbackPackage=$RollbackPackage", '-o', $setupOutput) + $extra)

if ($IsWindows) {
    $verify = Start-Process -FilePath (Join-Path $setupOutput 'StokHouseFamilyBudget-Playtest-Setup.exe') -ArgumentList '--verify-package' -Wait -PassThru
    if ($verify.ExitCode -ne 0) {
        throw "The embedded package check failed with exit code $($verify.ExitCode)."
    }
}
