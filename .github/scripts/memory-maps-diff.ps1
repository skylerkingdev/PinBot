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
    # writes "considerable=0" or "considerable=1", "why=..." and "added=N" to this file. The workflow merges and releases
    # an update on its own unless it is considerable: one that could break score reading for many servers at once
    # (a ROM id removed, a platform file changed, a licence file changed, or more than MaxChangedPercent of the ROM
    # ids PinBot already reads changed). Weekly updates have changed 1-2%; the two that needed fixes, 11% and 91%.
    [string] $KindOut,
    [double] $MaxChangedPercent = 5
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

if ($NewVariants) {
    $ov = Table (FileText $OldVariants); $nv = Table (FileText $NewVariants)
    $pairsAdded = @($nv.Keys | Where-Object { $ov[$_] -ne $nv[$_] })
    $pairsRemoved = @($ov.Keys | Where-Object { $ov[$_] -ne $nv[$_] })
    Section 'Coin/free play variant pairs added (rom-variants.json)' @($pairsAdded | ForEach-Object { "$_ -> $($nv[$_])" })
    Section 'Coin/free play variant pairs removed (rom-variants.json)' @($pairsRemoved | ForEach-Object { "$_ -> $($ov[$_])" })
}

# the maps' licence files are built into pinbot.exe and shipped: a change to them is for a person to read
$licences = @(Invoke-Git diff --name-only $Old $New -- 'LICENSE*')
if ($licences.Count -gt 0) { Section 'Licence files changed' $licences }

if ($KindOut) {
    $percent = if ($oldIndex.Count) { 100.0 * $changed.Count / $oldIndex.Count } else { 100.0 }
    $why = @(
        if ($removed.Count -gt 0) { "$($removed.Count) ROM id$(if ($removed.Count -ne 1) { 's' }) removed" }
        if ($platforms.Count -gt 0) { "$($platforms.Count) platform file$(if ($platforms.Count -ne 1) { 's' }) changed" }
        if ($licences.Count -gt 0) { 'licence files changed' }
        if ($percent -gt $MaxChangedPercent) { "$($changed.Count) of $($oldIndex.Count) ROM ids changed ($([math]::Round($percent, 1))%, limit $MaxChangedPercent%)" }
    )
    Set-Content -Encoding ascii $KindOut @("considerable=$([int]($why.Count -gt 0))", "why=$($why -join '; ')", "added=$($added.Count)")
}
