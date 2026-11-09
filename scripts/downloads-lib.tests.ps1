# Checks the bucket helpers: powershell -NoProfile -File scripts/downloads-lib.tests.ps1 (exit code 1 on a failure).
. (Join-Path $PSScriptRoot 'downloads-lib.ps1')

$failures = 0
function Check([string] $name, $actual, $expected) {
    $a = ($actual | ForEach-Object { "$_" }) -join ','
    $e = ($expected | ForEach-Object { "$_" }) -join ','
    if ($a -ne $e) {
        Write-Host "FAIL $name`n  expected: $e`n  actual:   $a"
        $script:failures++
    } else {
        Write-Host "ok   $name"
    }
}

Check 'keeps the newest five' (Select-OldVersions -Versions '0.6.0', '0.7.0', '0.10.0', '0.12.0', '0.9.0', '0.8.0', '0.11.0' -Keep 5 -Current '0.12.0') @('0.7.0', '0.6.0')
Check 'sorts by number, not text' (Select-OldVersions -Versions '0.9.0', '0.10.0' -Keep 1 -Current '0.10.0') @('0.9.0')
Check 'a suffix comes before the final version' (Select-OldVersions -Versions '1.0.0', '1.0.0-rc.1', '0.12.0' -Keep 1 -Current '1.0.0') @('1.0.0-rc.1', '0.12.0')
Check 'never deletes the current version' (Select-OldVersions -Versions '2.0.0', '1.0.0' -Keep 1 -Current '1.0.0') @()
Check 'nothing to delete' (Select-OldVersions -Versions '0.12.0' -Keep 5 -Current '0.12.0') @()
Check 'reads versions from keys' (Get-KeyVersions -Keys 'agent/latest.json', 'agent/0.12.0/pacelaps-agent-0.12.0-win-x64.zip', 'agent/0.12.0/pacelaps-agent-0.12.0-win-x64.zip.sha256', 'agent/1.0.0-rc.1/x.zip', 'other/1.0.0/x') @('0.12.0', '1.0.0-rc.1')

$sample = @"
# Changelog

Intro text, not a release.

## 0.12.0 $([char]0x2013) 2026-11-04

- Sends the classes.
- Times the sectors.

## 0.7.0 - 2026-10-31

- Types without focus.
"@
$parsed = ConvertFrom-Changelog -Text $sample
Check 'reads the releases, newest first' ($parsed | ForEach-Object { $_.version }) @('0.12.0', '0.7.0')
Check 'reads the dates' ($parsed | ForEach-Object { $_.date }) @('2026-11-04', '2026-10-31')
Check 'reads the changes under each release' $parsed[0].changes @('Sends the classes.', 'Times the sectors.')
Check 'takes a hyphen as well as an en dash' $parsed[1].changes @('Types without focus.')
$real = @(ConvertFrom-Changelog -Text (Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md') -Raw -Encoding UTF8))
Check 'CHANGELOG.md parses' ($real.Count -ge 5) @($true)
Check 'CHANGELOG.md: every release has changes' (@($real | Where-Object { $_.changes.Count -eq 0 }).Count) @(0)
Check 'CHANGELOG.md has the first release' ($real[-1].version) @('0.5.0')

if ($failures -gt 0) {
    exit 1
}
