# Builds the operator's package: the app as one self-contained exe (no .NET needed on the PC), zipped with the
# operator guide as README.txt, plus a SHA-256 checksum file. Then it runs the exe with --version as a check.
#
#   ./scripts/publish.ps1                  # development build, version 0.1.0+<commit>
#   ./scripts/publish.ps1 -Version 0.5.0 -Server https://app.pacelaps.com
#                                          # a release (the release workflow passes the tag's version and the
#                                          # server; a build with -Server pairs only with that server)
#
#   ./scripts/publish.ps1 -Installer      # also the installer (needs Inno Setup 6: ISCC.exe on the PATH or in its
#                                          # usual folder; installer/pacelaps-agent.iss)
#
# Output: artifacts/pacelaps-agent-<version>-win-x64.zip and .zip.sha256 (with -Installer also
# pacelaps-agent-<version>-setup.exe and .exe.sha256)
param(
    [string] $Version = "",
    [string] $Server = "",
    [switch] $Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'

if ($Version -ne '' -and $Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') {
    throw "Version '$Version' is not like 1.2.3 or 1.2.3-rc.1"
}

if (Test-Path $publish) {
    Remove-Item -Recurse -Force $publish
}

$properties = @(
    '-p:PublishSingleFile=true',
    '-p:SelfContained=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded'
)
if ($Version -ne '') {
    $properties += "-p:Version=$Version"
}
if ($Server -ne '') {
    if ($Server -notmatch '^https://[^/\s]+/?$') {
        throw "Server '$Server' is not an https:// address without a path"
    }
    $properties += "-p:ReleaseServer=$Server"
}

dotnet publish (Join-Path $root 'src/PaceLaps.Agent.App/PaceLaps.Agent.App.csproj') `
    --configuration Release --runtime win-x64 --output $publish @properties
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)"
}

$exe = Join-Path $publish 'pacelaps-agent.exe'
$extra = Get-ChildItem $publish -File | Where-Object { $_.Name -ne 'pacelaps-agent.exe' }
if ($extra) {
    throw "The package should be one exe, but publish also wrote: $($extra.Name -join ', ')"
}

# The check: the packaged exe starts and reports its version.
$out = Join-Path $artifacts 'version.txt'
$process = Start-Process -FilePath $exe -ArgumentList '--version' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
$lines = @(Get-Content $out)
$reported = "$($lines[0])".Trim()
Remove-Item $out
if ($process.ExitCode -ne 0 -or $reported -eq '') {
    throw "The packaged exe did not report its version (exit code $($process.ExitCode))"
}
if ($Version -ne '' -and -not $reported.StartsWith($Version)) {
    throw "The packaged exe reports '$reported', not $Version"
}
$builtServer = ($lines | Where-Object { $_ -like 'server *' } | Select-Object -First 1) -replace '^server ', ''
if ($Server -ne '' -and $builtServer.TrimEnd('/') -ne $Server.TrimEnd('/')) {
    throw "The packaged exe reports server '$builtServer', not $Server"
}

$name = "pacelaps-agent-$($reported.Split('+')[0])-win-x64"
$stage = Join-Path $artifacts $name
if (Test-Path $stage) {
    Remove-Item -Recurse -Force $stage
}
New-Item -ItemType Directory $stage | Out-Null
Copy-Item $exe $stage
Copy-Item (Join-Path $root 'docs/operator-guide.md') (Join-Path $stage 'README.txt')
# The agent is free software under the GPL-3.0 (IRSDKSharper's licence): the licence and the libraries' notices travel with it.
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') (Join-Path $stage 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $root 'licenses/dotnet-runtime-notices.txt') (Join-Path $stage 'dotnet-runtime-notices.txt')
# The fonts' licences (SIL Open Font License): Inter, Barlow Condensed, JetBrains Mono.
Get-ChildItem (Join-Path $root 'licenses') -Filter '*-OFL.txt' | ForEach-Object { Copy-Item $_.FullName (Join-Path $stage $_.Name) }

$zip = Join-Path $artifacts "$name.zip"
if (Test-Path $zip) {
    Remove-Item $zip
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

if ($Installer) {
    # The installer from the same files as the zip (per user, no administrator rights).
    $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
    foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (-not $iscc -and (Test-Path $candidate)) {
            $iscc = $candidate
        }
    }
    if (-not $iscc) {
        throw "-Installer needs Inno Setup 6 (ISCC.exe)"
    }
    $setupVersion = $reported.Split('+')[0]
    & $iscc /Q "/DAppVersion=$setupVersion" "/DSourceDir=$stage" "/DOutputDir=$artifacts" (Join-Path $root 'installer/pacelaps-agent.iss')
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed ($LASTEXITCODE)"
    }
    $setup = Join-Path $artifacts "pacelaps-agent-$setupVersion-setup.exe"
    $setupHash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$setup.sha256" -Value "$setupHash  pacelaps-agent-$setupVersion-setup.exe" -Encoding ascii -NoNewline
    Write-Host "Setup   $setup"
}

Remove-Item -Recurse -Force $stage

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Version $reported"
Write-Host "Server  $(if ($builtServer) { $builtServer } else { 'any (local build)' })"
Write-Host "Package $zip ($size MB)"
Write-Host "SHA-256 $hash"
