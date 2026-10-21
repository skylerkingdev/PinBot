# Builds dist\pinbot.exe (PinBot for a VPin: one self-contained file, memory maps built in; no .NET install needed) and
# dist\pinbot.json (its signed description, which VPins check before they update). The release workflow runs this too.
#   -Version        the release number (default: now, UTC yyyyMMddHHmmss)
#   -PublicKeyFile  build in this public key and do not touch the private key (the release workflow: the private key
#                   is written only for its signing step); with -SkipManifest nothing secret is needed at all
#   -SkipManifest   stop after dist\pinbot.exe (the release workflow, which can Authenticode-sign the exe first, then signs the
#                   manifest, whose SHA-256 and size must be the signed file's)
#   -SkipTests      the caller already ran them
# Without -PublicKeyFile it uses keys\update-signing.pem, creating a new key pair the first time. A key made that way
# is for testing only: VPins and servers accept only releases signed with the maintainer's key (keys\update-signing.pub).
param([string]$Version, [string]$PublicKeyFile, [switch]$SkipTests, [switch]$SkipManifest)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
git submodule update --init vendor/pinball-memory-maps
if (Test-Path dist) { Remove-Item dist -Recurse -Force }

if (-not $SkipTests) {
    dotnet test tests/PinBot.Agent.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

dotnet build tools/PinBot.Sign -c Release -o dist\signtool --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'building the signing tool failed' }
$publicKey = if ($PublicKeyFile) { (Get-Content $PublicKeyFile -Raw).Trim() } else { dotnet dist\signtool\PinBot.Sign.dll key keys }
if (-not $publicKey) { throw 'release public key unavailable' }
$version = if ($Version) { $Version } else { (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') }
if ($version -notmatch '^\d{14}$') { throw "release number must be yyyyMMddHHmmss: $version" }

$out = 'dist\pinbot'
dotnet publish src/PinBot.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none "-p:UpdatePublicKey=$publicKey" "-p:UpdateVersion=$version" -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Copy-Item "$out\pinbot.exe" 'dist\pinbot.exe'

if (-not $SkipManifest) {
    dotnet dist\signtool\PinBot.Sign.dll sign keys dist\pinbot.exe $version dist\pinbot.json
    if ($LASTEXITCODE -ne 0) { throw 'signing the release failed' }
}

Write-Host "release $version$(if ($SkipManifest) { ' (no manifest)' })"
Get-Item 'dist\pinbot.exe', 'dist\pinbot.json' -ErrorAction SilentlyContinue | Select-Object Name, Length
exit 0
