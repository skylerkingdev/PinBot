# Creates deploy\.env for the PinBot server: a random database password plus your Tailscale auth key.
# Run on any Windows PC from this folder:  powershell -ExecutionPolicy Bypass -File make-env.ps1
# Writes Unix line endings and no BOM, so Docker reads the values exactly.
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '.env'
if (Test-Path $target) { throw ".env already exists in $PSScriptRoot - delete it first if you really want new secrets" }

function Secret([int]$length) {
    $chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $bytes = New-Object byte[] $length
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

$tsKey = (Read-Host 'Paste your Tailscale auth key (tskey-auth-...)').Trim()
if ($tsKey -notlike 'tskey-*') { throw 'That does not look like a Tailscale auth key (it starts with tskey-)' }

$content = "POSTGRES_PASSWORD=$(Secret 32)`nTS_AUTHKEY=$tsKey`n"
[IO.File]::WriteAllText($target, $content, (New-Object Text.UTF8Encoding $false))

Write-Host ".env written to $target"
