# Copies the shared JSON contracts from the web service repository into contracts/.
# The web service owns them; the agent keeps a copy so its CI can test against them.
# A test (ContractCopyTests) fails when the copy and ../webservice/contracts differ.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = Join-Path (Split-Path -Parent $repo) 'webservice\contracts'
$target = Join-Path $repo 'contracts'

if (-not (Test-Path $source)) {
    throw "Not found: $source (expected the webservice repository next to this one)"
}

if (Test-Path $target) {
    Remove-Item -Recurse -Force $target
}

New-Item -ItemType Directory -Path $target | Out-Null
# Schemas and the other top-level contract files (example-schemas.json).
Copy-Item (Join-Path $source '*.json') $target
Copy-Item (Join-Path $source 'README.md') $target
Copy-Item -Recurse (Join-Path $source 'examples') $target
$commit = git -C (Split-Path -Parent $source) rev-parse --short HEAD
Set-Content -Path (Join-Path $target 'SOURCE.txt') -Value "Copied from webservice/contracts at commit $commit. Do not edit here; run scripts/sync-contracts.ps1." -Encoding utf8
Write-Host "Contracts copied from $source (webservice $commit)."
