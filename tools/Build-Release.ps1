param(
    [Parameter(Mandatory)][string]$Dotnet,
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$UpstreamDll,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path "$PSScriptRoot/..").Path
$manifest=Get-Content "$root/PullHistory.json" -Raw|ConvertFrom-Json
$version=$manifest.AssemblyVersion
$dest=[IO.Directory]::CreateDirectory($Output)
if($dest.GetFileSystemInfos().Length){throw 'Use an empty package output directory'}
& $Dotnet restore "$root/BossMod/BossModReborn.csproj" --locked-mode
if($LASTEXITCODE){throw "Locked restore failed"}
& $Dotnet build "$root/BossMod/BossModReborn.csproj" -c Release --no-restore -p:Version=$version -v:q
if($LASTEXITCODE){throw 'Standalone release build failed'}
$bin="$root/BossMod/bin/Release"
$stage=[IO.Directory]::CreateDirectory((Join-Path $dest 'package'))
foreach($name in 'PullHistory.dll','RebornMaterial.dll'){Copy-Item "$bin/$name" $stage.FullName}
foreach($name in 'PullHistory.json','LICENSE','THIRD_PARTY_NOTICES.md'){Copy-Item "$root/$name" $stage.FullName}
Copy-Item "$root/licenses" $stage.FullName -Recurse
& pwsh -NoProfile -File "$PSScriptRoot/Verify-PullHistory.ps1" -Package $stage.FullName -Archive $Archive
if($LASTEXITCODE){throw 'Standalone verification failed'}
$expected=@(& pwsh -NoProfile -File "$PSScriptRoot/Compare-Arena.ps1" -AssemblyFile $UpstreamDll -Archive $Archive)
if($LASTEXITCODE -or $expected.Count -ne 2){throw 'Upstream reference replay failed'}
$actual=@(& pwsh -NoProfile -File "$PSScriptRoot/Compare-Arena.ps1" -AssemblyFile "$stage/PullHistory.dll" -Archive $Archive)
if($LASTEXITCODE -or (Compare-Object $actual $expected)){throw 'Radar parity failed'}
$zip=Join-Path $dest "PullHistory-$version.zip"
Compress-Archive -Path "$stage/*" -DestinationPath $zip
$verify=[IO.Directory]::CreateDirectory((Join-Path $dest 'extracted'))
Expand-Archive $zip $verify.FullName
& pwsh -NoProfile -File "$PSScriptRoot/Verify-PullHistory.ps1" -Package $verify.FullName -Archive $Archive
if($LASTEXITCODE){throw 'Distributable verification failed'}
"Matched upstream M10S/M11S telegraphs: $actual"
