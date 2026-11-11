param([string]$Destination = 'E:\HanekawaPet-GitHub')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Destination already exists. Choose a new empty path.' }
New-Item -ItemType Directory -Path $target -Force | Out-Null
$rootFiles = @('.gitignore','.gitattributes','NuGet.Config','app.manifest','build.ps1','AI接口设置.cmd')
$rootFiles += @(Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Extension -in @('.cs','.xaml','.csproj','.md') } | Select-Object -ExpandProperty Name)
foreach ($file in $rootFiles) { Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target }
foreach ($directory in @('Core','Platform','pets','gacha','scripts','docs','.github')) {
    Copy-Item -LiteralPath (Join-Path $root $directory) -Destination (Join-Path $target $directory) -Recurse
}
New-Item -ItemType Directory -Path "$target/assets","$target/backend","$target/qa/framework/fixtures" -Force | Out-Null
Copy-Item -LiteralPath "$root/assets/pet.ico" -Destination "$target/assets"
Get-ChildItem -LiteralPath "$root/backend" -File | Where-Object { $_.Extension -in @('.cs','.csproj','.md') } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination "$target/backend" }
foreach ($file in @('test_backend.py','test_search_fallback.py')) { Copy-Item -LiteralPath "$root/qa/$file" -Destination "$target/qa" }
Copy-Item -Path "$root/qa/framework/fixtures/*" -Destination "$target/qa/framework/fixtures"
$forbidden = @(Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object { $_.Name -in @('ai-config.json','local-token.txt','settings.json','state.json','last-search.json','runtime-info.json') -or $_.Extension -in @('.dll','.exe','.pdb','.zip','.log') })
if ($forbidden.Count) { throw 'Unexpected local data or binaries found in source export.' }
Write-Output "Clean source: $target"
