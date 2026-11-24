param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$Archive,
    [string]$Dalamud="$env:APPDATA/XIVLauncher/addon/Hooks/dev"
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path $Package).Path
foreach($name in 'InteropGenerator.Runtime','FFXIVClientStructs','Lumina','Lumina.Excel','Dalamud','Dalamud.Bindings.ImGui','Dalamud.Bindings.ImPlot','Dalamud.Common','Newtonsoft.Json','TerraFX.Interop.Windows'){
    [void][Reflection.Assembly]::LoadFrom((Join-Path $Dalamud "$name.dll"))
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $root 'RebornMaterial.dll'))
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root 'PullHistory.dll'))
$manifest=Get-Content (Join-Path $root 'PullHistory.json') -Raw|ConvertFrom-Json
if($assembly.GetName().Version.ToString() -ne $manifest.AssemblyVersion){throw 'Version mismatch'}
if($assembly.GetReferencedAssemblies().Name -contains 'BossModReborn'){throw 'External BMR dependency'}
$plugin=$assembly.GetType('PullHistory.Plugin',$true)
if($plugin.GetInterfaces().Name -notcontains 'IAsyncDalamudPlugin' -or $assembly.GetType('BossMod.Plugin')){throw 'Wrong plugin entry point'}
$resources=$assembly.GetManifestResourceNames()
if(!($resources -like 'BossMod.Shaders.Compiled.*') -or !($resources -like 'BossMod.Fonts.Compiled.*')){throw 'Radar assets missing'}
[BossMod.Service]::Config.Initialize()
$static=[Reflection.BindingFlags]'Static,NonPublic'
$instance=[Reflection.BindingFlags]'Instance,NonPublic'
$summary=$plugin.GetMethod('Summarize',$static)
$testFolder=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('pull-history-test-'+[guid]::NewGuid())))
try{
    $owner=[Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($plugin)
    $loadProfile=$plugin.GetMethod('LoadRadarProfile',$instance)
    $loadProfile.Invoke($owner,@($testFolder))
    $display=[BossMod.BossModule]::WindowConfig
    if(!$display.ShowWaymarks -or !$display.ShowSigns -or !$display.ShowCardinals){throw 'Standalone radar defaults failed'}
    [IO.File]::WriteAllText((Join-Path $testFolder.FullName 'BossModReborn.json'),'{"Payload":{"BossMod.BossModuleConfig":{"ShowCardinals":false,"WaymarkFontSize":22,"ShowSigns":true,"ProjectRadarInto3DWorld":true}}}')
    $loadProfile.Invoke($owner,@($testFolder))
    if($display.ShowCardinals -or $display.WaymarkFontSize -ne 22 -or !$display.ShowSigns -or $display.ProjectRadarInto3DWorld){throw 'Radar profile import failed'}
    $ws=[BossMod.WorldState]::new(10000000,'fixture')
    $ws.CurrentCFCID=1
    $plugin.GetField('world',$instance).SetValue($owner,$ws)
    $plugin.GetField('pullDirectory',$instance).SetValue($owner,$testFolder)
    $plugin.GetField('active',$instance).SetValue($owner,[Collections.Generic.HashSet[object]]::new())
    $config=[Activator]::CreateInstance($plugin.GetNestedType('PlaybackSettings'))
    $config.PullLimit=2
    $plugin.GetField('playbackSettings',$instance).SetValue($owner,$config)
    $module=[Runtime.CompilerServices.RuntimeHelpers]::GetUninitializedObject($assembly.GetType('BossMod.Dawntrail.Savage.M10STheXtremes.M10STheXtremes',$true))
    1..3|ForEach-Object{
        $plugin.GetMethod('Begin',$instance).Invoke($owner,@($module))
        $plugin.GetMethod('End',$instance).Invoke($owner,@($module))
        $plugin.GetMethod('Finish',$instance).Invoke($owner,@())
    }
    $saved=$testFolder.GetFiles('Pull-*.log')
    if($saved.Length -ne 2){throw 'Recorder lifecycle or retention failed'}
    $progress=[single]0
    $roundtrip=[BossMod.ReplayParserLog]::Parse($saved[0].FullName,[ref]$progress,[Threading.CancellationToken]::None)
    if($roundtrip.GameVersion -ne 'fixture'){throw 'New recording failed to round-trip'}
}finally{$testFolder.Delete($true)}
foreach($fight in 'M10S','M11S'){
    $file=Get-ChildItem -LiteralPath $Archive -Filter "*$fight*.log"|Sort-Object Length -Descending|Select-Object -First 1
    if(!$file){throw "Missing required local $fight recording"}
    $progress=[single]0
    $rec=[BossMod.ReplayParserLog]::Parse($file.FullName,[ref]$progress,[Threading.CancellationToken]::None)
    $meta=$summary.Invoke($null,@($rec))
    if($meta.Members.Length -ne 8 -or ($meta.Members.Job -contains 0) -or $meta.Duration -eq '—'){throw "$fight roster or duration failed"}
    $plugin.GetMethod('LoadTracks',$instance).Invoke($owner,@($rec))
    $rotations=$plugin.GetField('rotations',$instance).GetValue($owner)
    if($rotations.Count -lt 8){throw "$fight rotation history missing"}
    $radar=$plugin.GetNestedType('ReplayRadar',[Reflection.BindingFlags]::NonPublic).GetConstructors()[0].Invoke(@($rec))
    try{
        $at=$rec.Encounters[0].Time.Start.AddSeconds(15)
        $radar.Seek($at)
        if(!$radar.Module -or $radar.Module.GetType().Name -notlike "$fight*"){throw "$fight encounter module missing"}
        $radar.Seek($at.AddSeconds(-5))
        $radar.Seek($at)
        if(!$radar.Module){throw "$fight seek failed"}
    }finally{$radar.Dispose()}
    "$fight roster, duration, rotation, encounter module and rewind verified."
}
if([AppDomain]::CurrentDomain.GetAssemblies().GetName().Name -contains 'BossModReborn'){throw 'BMR loaded during standalone tests'}
'Independent plugin entry point, assets, capture round-trip and retention verified.'
