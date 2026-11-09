# Publishes a release's zip and checksum on downloads.pacelaps.com (a Cloudflare R2 bucket), where the website's
# download page finds them. The repository is private, so its GitHub releases are not public.
#
#   ./scripts/upload-downloads.ps1 -Version 0.12.0                       # from artifacts/ (the release workflow)
#   ./scripts/upload-downloads.ps1 -Version 0.12.0 -Released 2026-11-04T18:00:00Z
#   ./scripts/upload-downloads.ps1 -Version 0.12.0 -DryRun               # writes latest.json, prints the uploads
#
# Bucket layout:
#   agent/<version>/pacelaps-agent-<version>-win-x64.zip (+ .sha256)   kept for good, cached for a year
#   agent/latest.zip, agent/latest.zip.sha256, agent/latest.json        the newest release, cached for 5 minutes
#   agent/changelog.json                                                CHANGELOG.md's releases, cached for 5 minutes
# A version with a suffix (1.0.0-rc.1) is uploaded under its own folder only; latest stays where it is.
# The bucket stays in Cloudflare's free tier (10 GB): only the newest -KeepVersions versions stay (about 47 MB each;
# the GitHub releases keep them all), and the script fails if the bucket holds more than -MaxBucketGB.
# The bucket's custom domain is downloads.pacelaps.com (no Worker route may cover it), and its CORS policy allows GET
# from https://pacelaps.com, so the website's download page can read latest.json.
#
# Credentials come from the environment (the release workflow sets them from the repository's secrets):
#   R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY, R2_ENDPOINT (https://<account>.r2.cloudflarestorage.com), R2_BUCKET
# Uses the AWS CLI (installed on GitHub's Windows runners).
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $Released = "",
    [string] $Artifacts = "",
    [string] $PublicBase = "https://downloads.pacelaps.com",
    [int] $KeepVersions = 5,
    [double] $MaxBucketGB = 8,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'downloads-lib.ps1')
$root = Split-Path -Parent $PSScriptRoot
if ($Artifacts -eq '') {
    $Artifacts = Join-Path $root 'artifacts'
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') {
    throw "Version '$Version' is not like 1.2.3 or 1.2.3-rc.1"
}

$name = "pacelaps-agent-$Version-win-x64.zip"
$zip = Join-Path $Artifacts $name
$sum = "$zip.sha256"
foreach ($file in @($zip, $sum)) {
    if (-not (Test-Path $file)) {
        throw "$file not found: run scripts/publish.ps1 -Version $Version first"
    }
}

# The checksum file says "<sha256>  <file name>"; check it against the zip before anything goes out.
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$listed = ((Get-Content $sum -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
if ($listed -ne $hash) {
    throw "$sum says $listed, the zip is $hash"
}

$isLatest = -not $Version.Contains('-')
$releasedAt = if ($Released -ne '') { ([datetime]::Parse($Released)).ToUniversalTime() } else { (Get-Date).ToUniversalTime() }
$key = "agent/$Version/$name"
$latest = [ordered]@{
    v          = 1
    product    = 'agent'
    version    = $Version
    released   = $releasedAt.ToString("yyyy-MM-ddTHH:mm:ssZ")
    prerelease = $Version.StartsWith('0.') -or $Version.Contains('-')
    file       = $name
    url        = "$PublicBase/$key"
    sha256     = $hash
    size       = (Get-Item $zip).Length
}
$json = Join-Path $Artifacts 'latest.json'
[IO.File]::WriteAllText($json, ($latest | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

# The changelog for the download page. A final release must have its entry (a dry run of a build only warns).
$releases = @(ConvertFrom-Changelog -Text (Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding UTF8))
if (-not ($releases | Where-Object { $_.version -eq $Version })) {
    if ($isLatest -and -not $DryRun) {
        throw "CHANGELOG.md has no entry for $Version"
    }
    Write-Warning "CHANGELOG.md has no entry for $Version"
}
$changelog = Join-Path $Artifacts 'changelog.json'
$changelogDoc = [ordered]@{ v = 1; product = 'agent'; releases = $releases }
[IO.File]::WriteAllText($changelog, ($changelogDoc | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))

$forever = 'public, max-age=31536000, immutable'
$short = 'public, max-age=300'
$uploads = @(
    @{ File = $zip; Key = $key; Type = 'application/zip'; Cache = $forever },
    @{ File = $sum; Key = "$key.sha256"; Type = 'text/plain; charset=utf-8'; Cache = $forever }
)
if ($isLatest) {
    $uploads += @(
        @{ File = $zip; Key = 'agent/latest.zip'; Type = 'application/zip'; Cache = $short; Disposition = "attachment; filename=`"$name`"" },
        @{ File = $sum; Key = 'agent/latest.zip.sha256'; Type = 'text/plain; charset=utf-8'; Cache = $short },
        @{ File = $json; Key = 'agent/latest.json'; Type = 'application/json'; Cache = $short }
    )
}
$uploads += @{ File = $changelog; Key = 'agent/changelog.json'; Type = 'application/json'; Cache = $short }

if ($DryRun) {
    foreach ($u in $uploads) {
        Write-Host "would upload $($u.File) -> $($u.Key) ($($u.Type); $($u.Cache))"
    }
    Write-Host (Get-Content $json -Raw)
    return
}

foreach ($variable in 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY', 'R2_ENDPOINT', 'R2_BUCKET') {
    if (-not [Environment]::GetEnvironmentVariable($variable)) {
        throw "$variable is not set"
    }
}
$env:AWS_ACCESS_KEY_ID = $env:R2_ACCESS_KEY_ID
$env:AWS_SECRET_ACCESS_KEY = $env:R2_SECRET_ACCESS_KEY
$env:AWS_DEFAULT_REGION = 'auto'
# R2 does not take the checksums newer AWS CLIs add to every upload by default.
$env:AWS_REQUEST_CHECKSUM_CALCULATION = 'when_required'
$env:AWS_RESPONSE_CHECKSUM_VALIDATION = 'when_required'

if ($env:R2_ENDPOINT -notmatch '^https://[0-9a-f]{32}(\.(eu|fedramp))?\.r2\.cloudflarestorage\.com/?$') {
    throw "R2_ENDPOINT is not the bucket's S3 API address (https://<account id>.r2.cloudflarestorage.com, without the bucket name; R2 > bucket > Settings)"
}

# Reading first tells a wrong bucket or token scope apart from a token that may not write.
$ErrorActionPreference = 'Continue' # Windows PowerShell turns redirected stderr into errors
$probe = & aws s3api list-objects-v2 --bucket $env:R2_BUCKET --endpoint-url $env:R2_ENDPOINT --max-keys 1 --output json 2>&1
$failed = $LASTEXITCODE -ne 0
$ErrorActionPreference = 'Stop'
if ($failed) {
    throw "Reading the bucket failed: $probe`nCheck R2_BUCKET (the bucket's exact name) and that the R2 API token is allowed on this bucket."
}

foreach ($u in $uploads) {
    $arguments = @('s3', 'cp', $u.File, "s3://$($env:R2_BUCKET)/$($u.Key)", '--endpoint-url', $env:R2_ENDPOINT,
        '--content-type', $u.Type, '--cache-control', $u.Cache, '--only-show-errors')
    if ($u.Disposition) {
        $arguments += @('--content-disposition', $u.Disposition)
    }
    & aws @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Upload of $($u.Key) failed. The token can read the bucket; if R2 said Access Denied, it needs Object Read & Write."
    }
    Write-Host "uploaded $($u.Key)"
}

# The public address must serve what was uploaded (custom domain on the bucket).
$check = "$PublicBase/$key.sha256"
try {
    $served = (Invoke-WebRequest -Uri $check -UseBasicParsing).Content
} catch {
    throw "Uploaded, but $check is not served ($($_.Exception.Message.Split("`n")[0])). Connect $PublicBase to the bucket: R2 > bucket > Settings > Custom Domains, status Active."
}
if ($served -is [byte[]]) {
    $served = [Text.Encoding]::ASCII.GetString($served)
}
if (-not $served.StartsWith($hash)) {
    throw "$check does not serve the new checksum"
}
Write-Host "$PublicBase/$key is public"

# Keep the bucket small: delete all but the newest versions, never the one just uploaded or the one latest names.
function Get-BucketObjects {
    $listing = & aws s3api list-objects-v2 --bucket $env:R2_BUCKET --endpoint-url $env:R2_ENDPOINT --output json
    if ($LASTEXITCODE -ne 0) {
        throw 'Listing the bucket failed'
    }
    $parsed = ($listing -join "`n") | ConvertFrom-Json
    return @($parsed.Contents | Where-Object { $_ })
}
$objects = Get-BucketObjects
$versions = Get-KeyVersions -Keys ($objects | ForEach-Object { $_.Key })
$finals = @($versions | Where-Object { -not $_.Contains('-') })
$protect = @($Version) + @(Select-OldVersions -Versions $finals -Keep 0 -Current '' | Select-Object -First 1)
foreach ($old in (Select-OldVersions -Versions $versions -Keep $KeepVersions -Current $Version)) {
    if ($protect -contains $old) {
        continue
    }
    & aws s3 rm "s3://$($env:R2_BUCKET)/agent/$old/" --recursive --endpoint-url $env:R2_ENDPOINT --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw "Deleting agent/$old failed"
    }
    Write-Host "deleted agent/$old"
}

$bytes = (Get-BucketObjects | Measure-Object -Property Size -Sum).Sum
$gb = [math]::Round($bytes / 1GB, 2)
Write-Host "bucket: $gb GB"
if ($gb -gt $MaxBucketGB) {
    throw "The bucket holds $gb GB, more than $MaxBucketGB GB (the free tier is 10 GB): delete old files in Cloudflare's dashboard"
}
