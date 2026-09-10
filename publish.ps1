<#
.SYNOPSIS
  Publishes Aqorin.Phone for one or all supported runtime identifiers.
.EXAMPLE
  ./publish.ps1                 # all RIDs
  ./publish.ps1 -Rid win-x64    # one RID
  ./publish.ps1 -BuildInstaller # all RIDs, then build the Windows installer when ISCC is available
#>
param(
    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'all')]
    [string]$Rid = 'all',
    [switch]$FrameworkDependent,
    [switch]$BuildInstaller,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$rids = if ($Rid -eq 'all') { @('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64') } else { @($Rid) }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$propsPath = Join-Path $PSScriptRoot 'Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath
$version = [string]($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) { throw "Could not read <Version> from $propsPath" }

$publishRoot = Join-Path $PSScriptRoot (Join-Path 'publish' $version)
$appProject = Join-Path $PSScriptRoot 'src/Aqorin.Phone.App/Aqorin.Phone.App.csproj'

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    $dotnetCandidates = @()
    if ($env:ProgramW6432) { $dotnetCandidates += Join-Path $env:ProgramW6432 'dotnet/dotnet.exe' }
    if ($env:ProgramFiles) { $dotnetCandidates += Join-Path $env:ProgramFiles 'dotnet/dotnet.exe' }
    $dotnetCandidates += 'dotnet'

    foreach ($candidate in $dotnetCandidates | Select-Object -Unique) {
        if ($candidate -ne 'dotnet' -and -not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) {
            $DotnetPath = $candidate
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    throw "No .NET SDK was found. Install the .NET SDK version required by global.json."
}

Write-Host "== Using dotnet: $DotnetPath"

foreach ($r in $rids) {
    $out = Join-Path $publishRoot $r
    Write-Host "== Publishing $r -> $out"
    & $DotnetPath publish $appProject `
        --configuration Release --runtime $r --self-contained $selfContained --output $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $r" }

    $native = switch -Wildcard ($r) { 'win-*' { 'portaudio.dll' } 'osx-*' { 'libportaudio.dylib' } 'linux-*' { 'libportaudio.so' } }
    if (-not (Test-Path (Join-Path $out $native))) { throw "native PortAudio library $native missing from $out" }
    Write-Host "   OK: $native present"
}

$issPath = Join-Path $PSScriptRoot 'installer/Aqorin.Phone.iss'
$winSource = Join-Path $publishRoot 'win-x64'
if (Test-Path $issPath) {
    $isccArgs = @($issPath, "/DMyAppVersion=$version", "/DSourceDir=$winSource")
    if ($BuildInstaller) {
        if (-not (Test-Path $winSource)) { throw "Windows publish output is required before building the installer: $winSource" }
        $iscc = Get-Command iscc -ErrorAction SilentlyContinue
        if (-not $iscc) { throw "Inno Setup Compiler (iscc.exe) was not found on PATH." }
        Write-Host "== Building Windows installer"
        & $iscc.Source @isccArgs
        if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
    } else {
        Write-Host "== Windows installer script: $issPath"
        Write-Host "   Build it with: iscc `"$issPath`" /DMyAppVersion=$version /DSourceDir=`"$winSource`""
    }
}
