# Helpers for upload-downloads.ps1 (dot-sourced; tested by scripts/downloads-lib.tests.ps1).

# The versions to delete from the bucket: all but the newest $Keep, never $Current. Versions sort by number; a
# version with a suffix (1.0.0-rc.1) comes before the same number without one.
function Select-OldVersions {
    param(
        [string[]] $Versions,
        [int] $Keep,
        [string] $Current
    )
    $parsed = foreach ($v in ($Versions | Select-Object -Unique)) {
        if ($v -match '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.]+)?$') {
            [pscustomobject]@{
                Version = $v
                Number  = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
                Final   = [int](-not $Matches[4])
            }
        }
    }
    $newestFirst = @($parsed | Sort-Object -Property @{ Expression = 'Number'; Descending = $true }, @{ Expression = 'Final'; Descending = $true }, @{ Expression = 'Version'; Descending = $true })
    $old = @($newestFirst | Select-Object -Skip $Keep | ForEach-Object { $_.Version })
    return @($old | Where-Object { $_ -ne $Current })
}

# The release versions in a list of bucket keys (agent/<version>/...).
function Get-KeyVersions {
    param([string[]] $Keys)
    return @($Keys | ForEach-Object { if ($_ -match '^agent/(\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?)/') { $Matches[1] } } | Select-Object -Unique)
}

# The releases in CHANGELOG.md, newest first: each "## <version> <dash> <yyyy-mm-dd>" heading (an en dash or a hyphen) and
# the "- " lines under it, for agent/changelog.json and the website's download page.
function ConvertFrom-Changelog {
    param([string] $Text)
    $releases = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match '^##\s+(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\s+\p{Pd}\s+(\d{4}-\d{2}-\d{2})\s*$') {
            $current = [ordered]@{ version = $Matches[1]; date = $Matches[2]; changes = [System.Collections.Generic.List[string]]::new() }
            $releases.Add($current)
        } elseif ($line -match '^#') {
            $current = $null
        } elseif ($null -ne $current -and $line -match '^-\s+(.+?)\s*$') {
            $current.changes.Add($Matches[1])
        }
    }
    return @($releases | ForEach-Object { [ordered]@{ version = $_.version; date = $_.date; changes = @($_.changes) } })
}
