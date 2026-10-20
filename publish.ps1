# Builds dist\pinbot.exe (cabinet agent: one self-contained file, memory maps built in; no .NET install needed)
# and dist\pinbot-server-deploy.zip (server sources + deploy\ for a NAS / Docker host, including pinbot.exe for
# /download/pinbot.exe; no secrets or data).
# -Version: the release number (default: now, UTC yyyyMMddHHmmss). -RequireKey: fail instead of creating a new signing
# key (the release workflow). -SkipTests: the caller already ran them. -SkipManifest: stop after dist\pinbot.exe, without
# dist\pinbot.json or the server package (the release workflow Authenticode-signs the exe first, then writes the manifest,
# whose SHA-256 and size must be the signed file's).
param([string]$Version, [switch]$RequireKey, [switch]$SkipTests, [switch]$SkipManifest)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
Add-Type -AssemblyName System.IO.Compression.FileSystem
git submodule update --init vendor/pinball-memory-maps
if (Test-Path dist) { Remove-Item dist -Recurse -Force }

if (-not $SkipTests) {
    dotnet test tests/PinBot.Agent.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

# Release signing for cabinet and server auto-update. The key pair lives in keys\ (git-ignored); a local build creates it
# the first time, the release workflow writes it from a GitHub secret. Cabinets and servers install only releases signed
# with the key they carry. Back up keys\update-signing.pem; without it no further release can update existing cabinets.
dotnet build tools/PinBot.Sign -c Release -o dist\signtool --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'building the signing tool failed' }
$publicKey = if ($RequireKey) { dotnet dist\signtool\PinBot.Sign.dll key keys --existing } else { dotnet dist\signtool\PinBot.Sign.dll key keys }
if ($LASTEXITCODE -ne 0 -or -not $publicKey) { throw 'release signing key unavailable' }
$version = if ($Version) { $Version } else { (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss') }
if ($version -notmatch '^\d{14}$') { throw "release number must be yyyyMMddHHmmss: $version" }

$out = 'dist\pinbot'
dotnet publish src/PinBot.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none "-p:UpdatePublicKey=$publicKey" "-p:UpdateVersion=$version" -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# .NET's zip writer uses '/' separators (Compress-Archive in Windows PowerShell writes '\', which NAS/Linux unzip mis-handles)
function Zip($dir, $zip) {
    $root = (Resolve-Path $dir).Path.TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::Open((Join-Path $PSScriptRoot $zip), 'Create')
    try {
        foreach ($f in Get-ChildItem $dir -Recurse -File) {
            $name = $f.FullName.Substring($root.Length).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $name)
        }
    } finally { $archive.Dispose() }
}
Copy-Item "$out\pinbot.exe" 'dist\pinbot.exe'
if ($SkipManifest) {
    Write-Host "release $version (no manifest)"
    exit 0
}
dotnet dist\signtool\PinBot.Sign.dll sign keys dist\pinbot.exe $version dist\pinbot.json
if ($LASTEXITCODE -ne 0) { throw 'signing the release failed' }

$stage = 'dist\server-deploy'
foreach ($p in 'src\PinBot.Contracts', 'src\PinBot.Server') {
    robocopy $p "$stage\$p" /E /XD bin obj /NFL /NDL /NJH /NJS /NP | Out-Null
}
robocopy deploy "$stage\deploy" /E /XD data backups /XF .env /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'copying deployment files failed' }   # robocopy: 0-7 = success
Copy-Item docs\DEPLOY-NAS.md, docs\DISCORD-SETUP.md $stage
Copy-Item 'dist\pinbot.exe' "$stage\deploy\agent\pinbot.exe"
Copy-Item 'dist\pinbot.json' "$stage\deploy\agent\pinbot.json"   # served at /download/pinbot.json: cabinets update from it
Zip $stage 'dist\pinbot-server-deploy.zip'

Write-Host "release $version"
Get-Item 'dist\pinbot.exe', 'dist\pinbot.json', 'dist\pinbot-server-deploy.zip' | Select-Object Name, Length
exit 0
