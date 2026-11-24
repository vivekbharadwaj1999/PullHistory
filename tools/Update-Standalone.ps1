param([Parameter(Mandatory)][string]$Dotnet,[Parameter(Mandatory)][string]$Archive)
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$true
$project=(Resolve-Path "$PSScriptRoot/..").Path
$upstream='https://github.com/FFXIV-CombatReborn/BossmodReborn.git'
$fork='https://github.com/vivekbharadwaj1999/PullHistory.git'
$release=Invoke-RestMethod 'https://api.github.com/repos/FFXIV-CombatReborn/BossmodReborn/releases/latest' -ErrorAction Stop
$tag=$release.tag_name
if($release.draft -or $release.prerelease -or $tag -notmatch '^\d+\.\d+\.\d+\.\d+$'){throw 'Invalid stable release'}
git -C $project fetch $fork refs/heads/pull-history-standalone
$head=git -C $project rev-parse FETCH_HEAD
$previous=(git -C $project show "${head}:tools/upstream-version.txt").Trim()
if([version]$tag -le [version]$previous){Write-Output "Up to date at $previous";return}
$base=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('pull-history-update-'+[guid]::NewGuid())))
$source=Join-Path $base 'source'
$feed=Join-Path $base 'feed'
function Commit([string]$where,[string]$message){
    git -C $where -c user.name='Pull History releases' -c user.email='53361252+vivekbharadwaj1999@users.noreply.github.com' commit -m $message
}
try {
    git -C $project worktree add --detach $source $head
    git -C $source fetch $upstream "refs/tags/$tag"
    $candidate=(git -C $source rev-parse 'FETCH_HEAD^{commit}').Trim()
    git -C $source -c user.name='Pull History releases' -c user.email='53361252+vivekbharadwaj1999@users.noreply.github.com' merge --no-edit $candidate
    $manifestPath=Join-Path $source 'PullHistory.json'
    $manifest=Get-Content $manifestPath -Raw|ConvertFrom-Json
    $old=[version]$manifest.AssemblyVersion
    $version=[version]::new($old.Major,$old.Minor,$old.Build,$old.Revision+1).ToString()
    $manifest.AssemblyVersion=$version
    $manifest|ConvertTo-Json -Depth 8|Set-Content $manifestPath -Encoding utf8
    $tag|Set-Content (Join-Path $source 'tools/upstream-version.txt')
    $asset=@($release.assets|Where-Object name -eq 'latest.zip')
    if($asset.Count -ne 1 -or $asset[0].digest -notmatch '^sha256:[0-9a-f]{64}$'){throw 'Upstream archive or checksum missing'}
    $zip=Join-Path $base 'upstream.zip'
    Invoke-WebRequest $asset[0].browser_download_url -OutFile $zip
    if("sha256:"+((Get-FileHash $zip).Hash.ToLowerInvariant()) -ne $asset[0].digest){throw 'Upstream download checksum mismatch'}
    $reference=Join-Path $base 'reference'
    Expand-Archive $zip $reference
    $out=Join-Path $base 'out'
    & "$source/tools/Build-Release.ps1" -Dotnet $Dotnet -Archive $Archive -UpstreamDll "$reference/BossModReborn.dll" -Output $out
    git -C $source add -- PullHistory.json tools/upstream-version.txt
    Commit $source "Verify standalone release $version against BMR $tag"
    git -C $project fetch $fork refs/heads/dalamud
    $feedHead=git -C $project rev-parse FETCH_HEAD
    git -C $project worktree add --detach $feed $feedHead
    & "$source/tools/Write-Feed.ps1" -Package $out -Feed $feed
    git -C $feed add -- repo.json "PullHistory-$version.zip"
    Commit $feed "Publish verified Pull History $version"
    $code=git -C $source rev-parse HEAD
    $distribution=git -C $feed rev-parse HEAD
    git -C $project push --atomic $fork "${code}:refs/heads/pull-history-standalone" "${distribution}:refs/heads/dalamud"
    Write-Output "Published $version with BMR $tag"
}finally{
    # Only worktrees created by this invocation are disposable.
    foreach($path in $feed,$source){
        if(Test-Path (Join-Path $path '.git')){git -C $project worktree remove --force $path}
    }
    if($base.Parent.FullName -eq ([IO.Path]::GetTempPath()).TrimEnd('\') -and $base.Name -like 'pull-history-update-*'){$base.Delete($true)}
}
