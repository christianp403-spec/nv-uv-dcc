param(
    [string] $Version = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot "artifacts"
$buildPropertiesPath = Join-Path $projectRoot "Directory.Build.props"
$productLicensePath = Join-Path $projectRoot "LICENSE"
[xml] $buildProperties = Get-Content -LiteralPath $buildPropertiesPath -Raw
$configuredVersion = [string] $buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $configuredVersion
}
if ([string]::IsNullOrWhiteSpace($Version) -or $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]{0,79}$') {
    throw "Invalid package version: $Version"
}

$productLicense = Get-Content -LiteralPath $productLicensePath -Raw
$forbiddenDccLicensePattern = '(?i)Mozilla Public License|\bMPL(?:-2\.0)?\b|PolyForm|Community Preview Binary Terms'
if ($productLicense -notmatch '(?s)GNU GENERAL PUBLIC LICENSE\s+Version 3, 29 June 2007' -or
    $buildProperties.Project.PropertyGroup.PackageLicenseExpression -ne 'GPL-3.0-only' -or
    $productLicense -match $forbiddenDccLicensePattern -or
    (Get-Content -LiteralPath $buildPropertiesPath -Raw) -match $forbiddenDccLicensePattern) {
    throw "The standalone source and package must use the GPLv3-only product license."
}

$packageName = "DCC-$Version-win-x64-portable"
$packagePath = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $artifactRoot ($packageName + ".zip")

if (Test-Path -LiteralPath $packagePath) {
    throw "Package directory already exists: $packagePath"
}

if (Test-Path -LiteralPath $zipPath) {
    throw "Package archive already exists: $zipPath"
}

$restoreArguments = @(
    "-r", "win-x64",
    "--locked-mode",
    "--ignore-failed-sources"
)

dotnet restore (Join-Path $projectRoot "src\Dcc.App\Dcc.App.csproj") @restoreArguments
if ($LASTEXITCODE -ne 0) { throw "DCC app restore failed." }

dotnet restore (Join-Path $projectRoot "src\Dcc.Helper\Dcc.Helper.csproj") @restoreArguments
if ($LASTEXITCODE -ne 0) { throw "DCC helper restore failed." }

# A self-contained Windows package embeds parts of the .NET and WPF runtime.
# Resolve the exact runtime packs selected by restore and carry their original
# license and third-party notice files without changing their bytes.
function Get-RuntimePackNoticePath {
    param(
        [Parameter(Mandatory = $true)] [string] $AssetsPath,
        [Parameter(Mandatory = $true)] [string] $PackageId,
        [Parameter(Mandatory = $true)] [string] $NoticeFileName
    )

    $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
    $downloadDependencies = @(
        foreach ($framework in $assets.project.frameworks.PSObject.Properties.Value) {
            @($framework.downloadDependencies)
        }
    )
    $versionSpecs = @(
        $downloadDependencies |
            Where-Object { $_.name -eq $PackageId } |
            Select-Object -ExpandProperty version -Unique
    )
    if ($versionSpecs.Count -ne 1 -or
        [string]$versionSpecs[0] -notmatch '^\[([^,]+),\s*([^\]]+)\]$' -or
        $Matches[1] -ne $Matches[2]) {
        throw "Could not resolve one exact version of runtime pack '$PackageId' from $AssetsPath."
    }

    $version = $Matches[1]
    foreach ($packageFolder in $assets.packageFolders.PSObject.Properties.Name) {
        $packagePath = Join-Path $packageFolder $PackageId.ToLowerInvariant()
        $noticePath = Join-Path (Join-Path $packagePath $version) $NoticeFileName
        if (Test-Path -LiteralPath $noticePath -PathType Leaf) {
            return $noticePath
        }
    }

    throw "Required runtime-pack notice is missing: $PackageId $version $NoticeFileName"
}

$appAssetsPath = Join-Path $projectRoot "src\Dcc.App\obj\project.assets.json"
$runtimeNoticeCopies = [ordered]@{
    "DOTNET-RUNTIME-LICENSE.txt" = Get-RuntimePackNoticePath `
        -AssetsPath $appAssetsPath `
        -PackageId "Microsoft.NETCore.App.Runtime.win-x64" `
        -NoticeFileName "LICENSE.TXT"
    "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt" = Get-RuntimePackNoticePath `
        -AssetsPath $appAssetsPath `
        -PackageId "Microsoft.NETCore.App.Runtime.win-x64" `
        -NoticeFileName "THIRD-PARTY-NOTICES.TXT"
    "WINDOWS-DESKTOP-RUNTIME-LICENSE.txt" = Get-RuntimePackNoticePath `
        -AssetsPath $appAssetsPath `
        -PackageId "Microsoft.WindowsDesktop.App.Runtime.win-x64" `
        -NoticeFileName "LICENSE"
}

# Create the artifact only after restore succeeds, so an offline/missing-runtime
# failure cannot leave a directory that looks like a completed release.
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null

$commonArguments = @(
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "--no-restore",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:PublishTrimmed=true",
    "-p:TrimMode=partial",
    # WPF is kept as a trim root by partial mode. The SDK blocks all WPF trim
    # publishes by default, so opt in explicitly and validate both windows below.
    "-p:_SuppressWpfTrimError=true",
    "-p:SatelliteResourceLanguages=en",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:Version=$Version",
    "-o", $packagePath
)

dotnet publish (Join-Path $projectRoot "src\Dcc.App\Dcc.App.csproj") @commonArguments
if ($LASTEXITCODE -ne 0) { throw "DCC app publish failed." }

dotnet publish (Join-Path $projectRoot "src\Dcc.Helper\Dcc.Helper.csproj") @commonArguments
if ($LASTEXITCODE -ne 0) { throw "DCC helper publish failed." }

# The app project's normal build target can leave this development-only file
# beside the two single-file executables. The helper has its runtime config
# embedded, so it is not part of the portable distribution.
$looseHelperRuntimeConfig = Join-Path $packagePath "Dcc.Helper.runtimeconfig.json"
if (Test-Path -LiteralPath $looseHelperRuntimeConfig) {
    Remove-Item -LiteralPath $looseHelperRuntimeConfig -Force
}

Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") `
    -Destination (Join-Path $packagePath "LICENSE.txt")

Copy-Item -LiteralPath (Join-Path $projectRoot "NOTICE.txt") `
    -Destination (Join-Path $packagePath "NOTICE.txt")
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") `
    -Destination (Join-Path $packagePath "THIRD-PARTY-NOTICES.txt")
foreach ($runtimeNoticeCopy in $runtimeNoticeCopies.GetEnumerator()) {
    $destinationPath = Join-Path $packagePath $runtimeNoticeCopy.Key
    Copy-Item -LiteralPath $runtimeNoticeCopy.Value -Destination $destinationPath
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $runtimeNoticeCopy.Value).Hash -ne
        (Get-FileHash -Algorithm SHA256 -LiteralPath $destinationPath).Hash) {
        throw "Runtime notice copy failed byte verification: $($runtimeNoticeCopy.Key)"
    }
}

if ((Get-FileHash -Algorithm SHA256 -LiteralPath $productLicensePath).Hash -ne
    (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $packagePath "LICENSE.txt")).Hash) {
    throw "The portable package must contain the exact GPLv3-only product license."
}

$expectedFiles = @(
    "DCC-by-NV-UV.exe",
    "Dcc.Helper.exe",
    "DOTNET-RUNTIME-LICENSE.txt",
    "DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt",
    "LICENSE.txt",

    "NOTICE.txt",
    "THIRD-PARTY-NOTICES.txt",
    "WINDOWS-DESKTOP-RUNTIME-LICENSE.txt"
)
$actualFiles = @(Get-ChildItem -LiteralPath $packagePath -File | Select-Object -ExpandProperty Name | Sort-Object)
$unexpectedDirectories = @(Get-ChildItem -LiteralPath $packagePath -Directory)
if ($unexpectedDirectories.Count -ne 0 -or
    (Compare-Object ($expectedFiles | Sort-Object) $actualFiles).Count -ne 0) {
    throw "Portable package contents do not match the expected eight files."
}

foreach ($executableName in @("DCC-by-NV-UV.exe", "Dcc.Helper.exe")) {
    $versionInfo = (Get-Item -LiteralPath (Join-Path $packagePath $executableName)).VersionInfo
    if ($versionInfo.ProductVersion -ne $Version) {
        throw "$executableName has ProductVersion '$($versionInfo.ProductVersion)' instead of '$Version'."
    }
}

$previewPath = Join-Path $artifactRoot (".dcc-portable-preview-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $previewPath -Force | Out-Null
    $appPath = Join-Path $packagePath "DCC-by-NV-UV.exe"
    $mainPreview = Join-Path $previewPath "main.png"
    $gamesPreview = Join-Path $previewPath "games.png"
    $mainArguments = @("--render-preview", ('"' + $mainPreview + '"'), "en", "100")
    $gamesArguments = @("--render-games-preview", ('"' + $gamesPreview + '"'), "en", "100")

    $mainProcess = Start-Process -FilePath $appPath -ArgumentList $mainArguments `
        -WindowStyle Hidden -Wait -PassThru
    $gamesProcess = Start-Process -FilePath $appPath -ArgumentList $gamesArguments `
        -WindowStyle Hidden -Wait -PassThru
    if ($mainProcess.ExitCode -ne 0 -or $gamesProcess.ExitCode -ne 0 -or
        -not (Test-Path -LiteralPath $mainPreview) -or
        -not (Test-Path -LiteralPath $gamesPreview)) {
        throw "The trimmed portable failed its WPF preview validation."
    }
}
finally {
    if (Test-Path -LiteralPath $previewPath) {
        $resolvedPreview = (Resolve-Path -LiteralPath $previewPath).Path
        $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
        if (-not $resolvedPreview.StartsWith($resolvedArtifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Portable preview path escaped artifact root: $resolvedPreview"
        }
        Remove-Item -LiteralPath $resolvedPreview -Recurse -Force
    }
}

Compress-Archive -Path (Join-Path $packagePath '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
Write-Host "Package: $zipPath"
Write-Host "SHA256:  $($hash.Hash)"
