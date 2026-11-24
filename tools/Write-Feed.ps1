param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Feed)
$ErrorActionPreference='Stop'
$manifest=Get-Content "$Package/extracted/PullHistory.json" -Raw|ConvertFrom-Json
$version=$manifest.AssemblyVersion
if($manifest.InternalName -ne 'PullHistory' -or $version -notmatch '^\d+\.\d+\.\d+\.\d+$'){throw 'Invalid manifest'}
$name="PullHistory-$version.zip"
if(Test-Path (Join-Path $Feed $name)){throw 'Version already published'}
Copy-Item (Join-Path $Package $name) $Feed
$url="https://raw.githubusercontent.com/vivekbharadwaj1999/PullHistory/dalamud/$name"
foreach($key in 'DownloadLinkInstall','DownloadLinkUpdate'){$manifest|Add-Member -NotePropertyName $key -NotePropertyValue $url}
$manifest|Add-Member -NotePropertyName LastUpdate -NotePropertyValue ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
ConvertTo-Json -InputObject @($manifest) -Depth 8 | Set-Content (Join-Path $Feed 'repo.json') -Encoding utf8
