param([string]$ReleaseDirectory = 'dist-ai', [string]$OutputDirectory = 'artifacts')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = [IO.Path]::GetFullPath((Join-Path $root $ReleaseDirectory))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (-not $output.StartsWith($root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be inside project.' }
$version = ([xml](Get-Content "$root/HanekawaPet.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ([Diagnostics.FileVersionInfo]::GetVersionInfo("$release/HanekawaPet.exe").ProductVersion.Split('+')[0] -ne $version) { throw 'Build version does not match project.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = "HanekawaPet-v$version-windows-x64.zip"
Compress-Archive -Path "$release/*" -DestinationPath (Join-Path $output $name) -CompressionLevel Optimal -Force
$hash = (Get-FileHash -LiteralPath (Join-Path $output $name) -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name" | Set-Content -LiteralPath "$output/SHA256SUMS.txt" -Encoding ascii
Write-Output "Release: $output/$name"
