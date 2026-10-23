param(
    [string] $Version = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $projectRoot "artifacts"
$buildPropertiesPath = Join-Path $projectRoot "Directory.Build.props"

function Get-SafeRelativePath([string] $Root, [string] $Path) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escaped source root: $fullPath"
    }
    return $fullPath.Substring($rootPath.Length)
}

[xml] $buildProperties = Get-Content -LiteralPath $buildPropertiesPath -Raw
$productLicense = Get-Content -LiteralPath (Join-Path $projectRoot 'LICENSE') -Raw
if ($productLicense -notmatch '(?s)GNU GENERAL PUBLIC LICENSE\s+Version 3, 29 June 2007' -or
    $buildProperties.Project.PropertyGroup.PackageLicenseExpression -ne 'GPL-3.0-only' -or
    $productLicense -match '(?i)Mozilla Public License|\bMPL(?:-2\.0)?\b|PolyForm|Community Preview Binary Terms') {
    throw 'The standalone source snapshot must use the GPLv3-only product license.'
}
$configuredVersion = [string] $buildProperties.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $configuredVersion
}
if ([string]::IsNullOrWhiteSpace($Version) -or $Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]{0,79}$') {
    throw "Invalid source version: $Version"
}

$snapshotName = "DCC-$Version-source"
$stagingPath = Join-Path $artifactRoot $snapshotName
$zipPath = Join-Path $artifactRoot ($snapshotName + ".zip")

if (Test-Path -LiteralPath $stagingPath) {
    throw "Source staging directory already exists: $stagingPath"
}
if (Test-Path -LiteralPath $zipPath) {
    throw "Source archive already exists: $zipPath"
}

$sourceFiles = @(
    ".gitattributes",
    ".github\ISSUE_TEMPLATE\bug-report.yml",
    ".github\ISSUE_TEMPLATE\config.yml",
    ".github\ISSUE_TEMPLATE\test-result.yml",
    "assets\dcc-alpha11.png",
    "COMMUNITY-PREVIEW-TERMS.txt",
    ".gitignore",
    "Dcc.slnx",
    "Directory.Build.props",
    "global.json",
    "README.md",
    "LICENSE",

    "NOTICE.txt",
    "SECURITY.md",
    "THIRD-PARTY-NOTICES.md",
    "scripts\Publish-Portable.ps1",
    "scripts\Publish-SourceSnapshot.ps1",
    "src\Dcc.App\app.manifest",
    "src\Dcc.App\App.xaml",
    "src\Dcc.App\App.xaml.cs",
    "src\Dcc.App\Assets\app.ico",
    "src\Dcc.App\Assets\brushed_silver.png",
    "src\Dcc.App\Assets\tray_active.ico",
    "src\Dcc.App\Assets\tray_off.ico",
    "src\Dcc.App\Assets\tray_recovery.ico",
    "src\Dcc.App\AutoStartService.cs",
    "src\Dcc.App\Data\GameDatabase.json",
    "src\Dcc.App\Dcc.App.csproj",
    "src\Dcc.App\DccLocalization.cs",
    "src\Dcc.App\ElevatedClockCapSession.cs",
    "src\Dcc.App\GamesProfilesWindow.xaml",
    "src\Dcc.App\GamesProfilesWindow.xaml.cs",
    "src\Dcc.App\MainWindow.xaml",
    "src\Dcc.App\MainWindow.xaml.cs",
    "src\Dcc.App\NativeTrayIcon.cs",
    "src\Dcc.App\packages.lock.json",
    "src\Dcc.App\Themes\PlayOledTheme.xaml",
    "src\Dcc.Core\AssemblyInfo.cs",
    "src\Dcc.Core\Dcc.Core.csproj",
    "src\Dcc.Core\DccActiveSessionState.cs",
    "src\Dcc.Core\DccAutomaticClockGovernor.cs",
    "src\Dcc.Core\DccAutoStartTaskDefinition.cs",
    "src\Dcc.Core\DccClockGovernor.cs",
    "src\Dcc.Core\DccCommandText.cs",
    "src\Dcc.Core\DccDiagnosticReport.cs",
    "src\Dcc.Core\DccDriverApplication.cs",
    "src\Dcc.Core\DccFpsRange.cs",
    "src\Dcc.Core\DccFpsSample.cs",
    "src\Dcc.Core\DccFrameLimitCommand.cs",
    "src\Dcc.Core\DccFrameLimiterRecoveryState.cs",
    "src\Dcc.Core\DccGameCatalog.cs",
    "src\Dcc.Core\DccGameDetector.cs",
    "src\Dcc.Core\DccGameDisplayProbe.cs",
    "src\Dcc.Core\DccGameHistoryStore.cs",
    "src\Dcc.Core\DccJpacRecoveryState.cs",
    "src\Dcc.Core\DccJpacVerification.cs",
    "src\Dcc.Core\DccLog.cs",
    "src\Dcc.Core\DccMonotonicClock.cs",
    "src\Dcc.Core\DccPaths.cs",
    "src\Dcc.Core\DccPerformanceLog.cs",
    "src\Dcc.Core\DccProfileStore.cs",
    "src\Dcc.Core\DccRendererBridgeResolver.cs",
    "src\Dcc.Core\DccUserSettings.cs",
    "src\Dcc.Core\DccVsyncRecoveryState.cs",
    "src\Dcc.Core\GameCatalogUpdateService.cs",
    "src\Dcc.Core\NvApiCoreVoltageReader.cs",
    "src\Dcc.Core\NvidiaGpuInfo.cs",
    "src\Dcc.Core\NvidiaGpuSelector.cs",
    "src\Dcc.Core\NvidiaSmiClient.cs",
    "src\Dcc.Core\NvidiaSmiException.cs",
    "src\Dcc.Core\packages.lock.json",
    "src\Dcc.Core\SupportedClockCatalog.cs",
    "src\Dcc.Helper\app.manifest",
    "src\Dcc.Helper\AssemblyInfo.cs",
    "src\Dcc.Helper\Dcc.Helper.csproj",
    "src\Dcc.Helper\DccEmergencyResetCoordinator.cs",
    "src\Dcc.Helper\DccEtwFpsCollector.cs",
    "src\Dcc.Helper\NvApiFrameLimiterClient.cs",
    "src\Dcc.Helper\NvApiJpacClient.cs",
    "src\Dcc.Helper\packages.lock.json",
    "src\Dcc.Helper\Program.cs",
    "tests\Dcc.SelfTest\Dcc.SelfTest.csproj",
    "tests\Dcc.SelfTest\packages.lock.json",
    "tests\Dcc.SelfTest\Program.cs"
)

$duplicateSourceFiles = @(
    $sourceFiles |
        Group-Object { $_.Replace('\', '/').ToLowerInvariant() } |
        Where-Object Count -gt 1)
if ($duplicateSourceFiles.Count -ne 0) {
    throw "The source allowlist contains duplicate paths."
}

$scopedProjectFiles = @(
    Get-ChildItem -LiteralPath $projectRoot -File
    foreach ($sourceFolder in @("scripts", "src", "tests")) {
        Get-ChildItem -LiteralPath (Join-Path $projectRoot $sourceFolder) -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
    }
) | ForEach-Object { Get-SafeRelativePath $projectRoot $_.FullName }
$unexpectedProjectFiles = @(
    $scopedProjectFiles |
        Where-Object { $sourceFiles -notcontains $_ } |
        Sort-Object)
if ($unexpectedProjectFiles.Count -ne 0) {
    throw "Files outside the explicit source allowlist were found: $($unexpectedProjectFiles -join ', ')"
}

if (Test-Path -LiteralPath (Join-Path $projectRoot "tools")) {
    throw "The source candidate must not contain a tools directory."
}

$projectRootItem = Get-Item -LiteralPath $projectRoot
if (($projectRootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The source root must not be a reparse point: $projectRoot"
}

$verifiedSourceFiles = foreach ($relativePath in $sourceFiles) {
    if ([IO.Path]::IsPathRooted($relativePath)) {
        throw "The source allowlist contains a rooted path: $relativePath"
    }

    $sourcePath = [IO.Path]::GetFullPath((Join-Path $projectRoot $relativePath))
    $verifiedRelativePath = Get-SafeRelativePath $projectRoot $sourcePath
    if (-not $verifiedRelativePath.Equals($relativePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The source allowlist path is not canonical: $relativePath"
    }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Required source file is missing: $relativePath"
    }

    $sourceItem = Get-Item -LiteralPath $sourcePath
    if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Source allowlist file must not be a reparse point: $relativePath"
    }
    $sourceParent = $sourceItem.Directory
    while ($sourceParent -and
           -not $sourceParent.FullName.Equals($projectRootItem.FullName, [StringComparison]::OrdinalIgnoreCase)) {
        if (($sourceParent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source allowlist parent must not be a reparse point: $relativePath"
        }
        $sourceParent = $sourceParent.Parent
    }
    if ($null -eq $sourceParent) {
        throw "Source allowlist path escaped the project root: $relativePath"
    }

    [pscustomobject]@{
        RelativePath = $relativePath
        FullPath = $sourcePath
    }
}

try {
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

    foreach ($sourceFile in $verifiedSourceFiles) {
        $destination = Join-Path $stagingPath $sourceFile.RelativePath
        $destinationDirectory = Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullPath -Destination $destination
    }

    $manifestLines = Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = (Get-SafeRelativePath $stagingPath $_.FullName).Replace('\', '/')
            $fileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
            "$fileHash  $relativePath"
        }
    [IO.File]::WriteAllLines(
        (Join-Path $stagingPath "SOURCE-MANIFEST.sha256"),
        $manifestLines,
        [Text.UTF8Encoding]::new($false))

    $expectedStagedFiles = @(
        $sourceFiles |
            ForEach-Object { $_.Replace('\', '/') }
        "SOURCE-MANIFEST.sha256"
    ) | Sort-Object
    $actualStagedFiles = @(
        Get-ChildItem -LiteralPath $stagingPath -Recurse -File |
            ForEach-Object { (Get-SafeRelativePath $stagingPath $_.FullName).Replace('\', '/') }
    ) | Sort-Object
    if ((Compare-Object $expectedStagedFiles $actualStagedFiles).Count -ne 0) {
        throw "Source snapshot contents do not match the explicit allowlist."
    }

    Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        $resolvedStaging = (Resolve-Path -LiteralPath $stagingPath).Path
        $resolvedArtifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path
        if (-not $resolvedStaging.StartsWith($resolvedArtifactRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Source staging path escaped artifact root: $resolvedStaging"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath
Write-Host "Source:  $zipPath"
Write-Host "SHA256:  $($hash.Hash)"
