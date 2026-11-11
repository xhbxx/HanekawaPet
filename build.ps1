param([string]$OutputFolder = 'dist-ai', [string]$ArchivePath = 'HanekawaPet-AI-Windows.zip', [switch]$SkipArchive)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectDirectory '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $projectDirectory '.nuget\packages'
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $projectDirectory $OutputFolder))
if (-not $releaseDirectory.StartsWith($projectDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release directory must be inside the project directory.'
}
$archivePath = [IO.Path]::GetFullPath((Join-Path $projectDirectory $ArchivePath))
if (-not $archivePath.StartsWith($projectDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive must be inside the project directory.' }
$installedDotnet = Split-Path (Get-Command dotnet).Source -Parent
$coreRoot = Join-Path $installedDotnet 'shared\Microsoft.NETCore.App'
$desktopRoot = Join-Path $installedDotnet 'shared\Microsoft.WindowsDesktop.App'
$aspnetRoot = Join-Path $installedDotnet 'shared\Microsoft.AspNetCore.App'
$runtimeVersion = Get-ChildItem -LiteralPath $coreRoot -Directory | Where-Object {
    $_.Name -like '9.0.*' -and (Test-Path -LiteralPath (Join-Path $desktopRoot $_.Name)) -and (Test-Path -LiteralPath (Join-Path $aspnetRoot $_.Name))
} | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if (-not $runtimeVersion) { throw 'A matching .NET 9 x64 Windows Desktop runtime is required on the build computer.' }
dotnet publish (Join-Path $projectDirectory 'HanekawaPet.csproj') -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=false -p:AppHostDotNetSearch=AppRelative -p:AppHostRelativeDotNet=runtime `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $releaseDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
# Remove obsolete backend launcher copies from earlier builds; the backend lives in its own folder.
Get-ChildItem -LiteralPath $releaseDirectory -Filter 'HanekawaPet.Backend.*' -File | Remove-Item -Force
dotnet publish (Join-Path $projectDirectory 'backend\HanekawaPet.Backend.csproj') -c Release --self-contained false `
    -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false `
    -o (Join-Path $releaseDirectory 'backend') --nologo
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectDirectory 'backend\README.md') -Destination (Join-Path $releaseDirectory 'backend') -Force
$bundleRuntime = Join-Path $releaseDirectory 'runtime'
foreach ($folder in @('host\fxr','shared\Microsoft.NETCore.App','shared\Microsoft.WindowsDesktop.App','shared\Microsoft.AspNetCore.App')) {
    New-Item -ItemType Directory -Path (Join-Path $bundleRuntime $folder) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $installedDotnet "$folder\$runtimeVersion") -Destination (Join-Path $bundleRuntime $folder) -Recurse -Force
}
foreach ($fileName in @('dotnet.exe','LICENSE.txt','ThirdPartyNotices.txt')) {
    $sourceFile = Join-Path $installedDotnet $fileName
    if (Test-Path -LiteralPath $sourceFile) { Copy-Item -LiteralPath $sourceFile -Destination $bundleRuntime -Force }
}
if (-not $SkipArchive) {
    New-Item -ItemType Directory -Path (Split-Path $archivePath -Parent) -Force | Out-Null
    Compress-Archive -Path (Join-Path $releaseDirectory '*') -DestinationPath $archivePath -Force -CompressionLevel Optimal
}
Write-Output "Executable: $(Join-Path $releaseDirectory 'HanekawaPet.exe')"
if (-not $SkipArchive) { Write-Output "Archive: $archivePath" }
