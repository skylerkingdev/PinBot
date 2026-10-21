# Writes a Markdown summary of what changed in the Pinball Memory Maps between two upstream commits, for the body of
# the memory-maps update pull request: upstream commits, ROM ids added / changed / removed, platform files changed and
# coin/free play variant pairs added or removed. Used by .github/workflows/memory-maps.yml; runs locally too:
#   powershell -File .github/scripts/memory-maps-diff.ps1 -Repo vendor/pinball-memory-maps -Old 1bde3ed -New 68c1b33 `
#     -OldVariants old.json -NewVariants src/PinBot.Server/rom-variants.json
# A ROM counts as changed when index.json points it at a different map file, or when its map file changed.
param(
    [Parameter(Mandatory)] [string] $Repo,
    [Parameter(Mandatory)] [string] $Old,
    [Parameter(Mandatory)] [string] $New,
    [string] $OldVariants,
    [string] $NewVariants,
    [int] $MaxList = 150,
    # writes "additive=1" (only new ROM ids, no change to any map or platform PinBot already reads) or "additive=0",
    # and "added=N", to this file: the workflow merges additive updates on its own
    [string] $KindOut
)
$ErrorActionPreference = 'Stop'

function Invoke-Git { $out = & git.exe -C $Repo @args; if ($LASTEXITCODE) { throw "git $args failed" }; $out }

# index.json / rom-variants.json as an ordinal-sorted name -> value table, skipping "_note"-style keys.
function Table([string] $json) {
    $t = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    if ($json) { ($json | ConvertFrom-Json).PSObject.Properties | Where-Object { -not $_.Name.StartsWith('_') } | ForEach-Object { $t[$_.Name] = [string]$_.Value } }
    $t
}
function FileText([string] $path) { if ($path -and (Test-Path $path)) { [IO.File]::ReadAllText((Resolve-Path $path)) } }

$oldIndex = Table ((Invoke-Git show "${Old}:index.json") -join "`n")
$newIndex = Table ((Invoke-Git show "${New}:index.json") -join "`n")

# Map and platform files whose content changed (renames count as changes at both paths).
$changedFiles = @{}
$platforms = New-Object System.Collections.Generic.List[string]
foreach ($line in (Invoke-Git diff --name-status --no-renames $Old $New -- maps platforms)) {
    $status, $path = $line -split "`t", 2
    if ($path -like 'platforms/*') { $platforms.Add("$status $path") } else { $changedFiles[$path] = $status }
}

$added = @($newIndex.Keys | Where-Object { -not $oldIndex.ContainsKey($_) })
$removed = @($oldIndex.Keys | Where-Object { -not $newIndex.ContainsKey($_) })
$changed = @($newIndex.Keys | Where-Object { $oldIndex.ContainsKey($_) } | ForEach-Object {
    if ($oldIndex[$_] -ne $newIndex[$_]) { "$_ ($($oldIndex[$_]) -> $($newIndex[$_]))" }
    elseif ($changedFiles.ContainsKey($newIndex[$_])) { "$_ ($($newIndex[$_]))" }
})

function Section([string] $title, [object[]] $items) {
    $items = @($items | Where-Object { $_ })
    "### $title ($($items.Count))"
    ''
    if ($items.Count -eq 0) { '_none_' }
    else {
        $items | Select-Object -First $MaxList | ForEach-Object { "- ``$_``" }
        if ($items.Count -gt $MaxList) { "- ... and $($items.Count - $MaxList) more" }
    }
    ''
}

$commits = @(Invoke-Git log --no-merges --format='%h %s' "$Old..$New")
'### Upstream commits'
''
if ($commits.Count -eq 0) { '_history not available; compare the commits on GitHub_' }
else { $commits | Select-Object -First $MaxList | ForEach-Object { "- $_" } }
''
"Compare: https://github.com/tomlogic/pinball-memory-maps/compare/$Old...$New"
''
Section 'ROM ids added' $added
Section 'ROM ids changed' $changed
Section 'ROM ids removed' $removed
Section 'Platform files changed' $platforms.ToArray()

$variantsTouchOld = $false
if ($NewVariants) {
    $ov = Table (FileText $OldVariants); $nv = Table (FileText $NewVariants)
    $pairsAdded = @($nv.Keys | Where-Object { $ov[$_] -ne $nv[$_] })
    $pairsRemoved = @($ov.Keys | Where-Object { $ov[$_] -ne $nv[$_] })
    Section 'Coin/free play variant pairs added (rom-variants.json)' @($pairsAdded | ForEach-Object { "$_ -> $($nv[$_])" })
    Section 'Coin/free play variant pairs removed (rom-variants.json)' @($pairsRemoved | ForEach-Object { "$_ -> $($ov[$_])" })
    # a pair that groups or ungroups a ROM PinBot already knew changes which ROMs count as one game
    $variantsTouchOld = $pairsRemoved.Count -gt 0 -or @($pairsAdded | Where-Object { $oldIndex.ContainsKey($_) -or $oldIndex.ContainsKey($nv[$_]) }).Count -gt 0
}

# the maps' licence files are built into pinbot.exe and shipped: a change to them is for a person to read
$licences = @(Invoke-Git diff --name-only $Old $New -- 'LICENSE*')
if ($licences.Count -gt 0) { Section 'Licence files changed (never merged automatically)' $licences }

if ($KindOut) {
    $additive = $changed.Count -eq 0 -and $removed.Count -eq 0 -and $platforms.Count -eq 0 -and -not $variantsTouchOld -and $licences.Count -eq 0
    Set-Content -Encoding ascii $KindOut @("additive=$([int]$additive)", "added=$($added.Count)")
}
