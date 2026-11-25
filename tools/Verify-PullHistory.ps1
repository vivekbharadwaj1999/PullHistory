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
    [IO.File]::WriteAllText((Join-Path $testFolder.FullName 'BossModReborn.json'),'{"Payload":{"BossMod.BossModuleConfig":{"MinMaturity":"Contributed","ShowCardinals":false,"WaymarkFontSize":22,"ShowSigns":true,"ProjectRadarInto3DWorld":true}}}')
    $loadProfile.Invoke($owner,@($testFolder))
    if($display.ShowCardinals -or $display.WaymarkFontSize -ne 22 -or !$display.ShowSigns -or $display.ProjectRadarInto3DWorld){throw 'Radar profile import failed'}
    if($display.MinMaturity -ne [BossMod.BossModuleInfo+Maturity]::WIP){throw 'WIP encounters excluded from recording'}
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
    $alternate=$testFolder.CreateSubdirectory("alternate")
    $plugin.GetMethod('Begin',$instance).Invoke($owner,@($module))
    $plugin.GetField('pullDirectory',$instance).SetValue($owner,$alternate)
    $plugin.GetMethod('End',$instance).Invoke($owner,@($module))
    $plugin.GetMethod('Finish',$instance).Invoke($owner,@())
    if($alternate.GetFiles('Pull-*.log').Length -ne 0){throw 'Active pull changed destination'}
    $plugin.GetMethod('Begin',$instance).Invoke($owner,@($module))
    $plugin.GetMethod('End',$instance).Invoke($owner,@($module))
    $plugin.GetMethod('Finish',$instance).Invoke($owner,@())
    $listed=$plugin.GetMethod('History',$static).Invoke($null,@($alternate))
    if($listed.Length -ne 1 -or $testFolder.GetFiles('Pull-*.log').Length -ne 2){throw 'Folder switch or archive listing failed'}
    if($roundtrip.GameVersion -ne 'fixture'){throw 'New recording failed to round-trip'}
    $o1=$testFolder.CreateSubdirectory("deltascape")
    $plugin.GetField('pullDirectory',$instance).SetValue($owner,$o1)
    $ws.CurrentCFCID=252
    $time=[datetime]::Now
    function AdvanceFixture([int]$seconds){
        $frame=[BossMod.FrameState]::new($time.AddSeconds($seconds),[ulong](10000000*(1+$seconds)),[uint32](1+$seconds),1,1,1)
        $ws.Execute([BossMod.WorldState+OpFrameStart]::new([ref]$frame,[timespan]::Zero,[Activator]::CreateInstance([BossMod.ClientState+Gauge]),[BossMod.Angle]::new(0)))
    }
    AdvanceFixture 0
    $hp=[BossMod.ActorHPMP]::new(100,100,0,0,0)
    $pos=[Numerics.Vector4]::Zero
    $mgr=[BossMod.BossModuleManager]::new($ws)
    $on=$mgr.ModuleActivated.Subscribe([Action[BossMod.BossModule]]{param($m) $plugin.GetMethod('Begin',$instance).Invoke($owner,@($m))})
    $off=$mgr.ModuleDeactivated.Subscribe([Action[BossMod.BossModule]]{param($m) $plugin.GetMethod('End',$instance).Invoke($owner,@($m))})
    try{
        $ws.Execute([BossMod.ActorState+OpCreate]::new(101,0,0,0,"Player",0,"Player","PLD",70,$pos,1,$hp,$true,$true,0,0,0))
        $ws.Execute([BossMod.PartyState+OpModify]::new(0,[BossMod.PartyState+Member]::new(1001,101,$false)))
        $ws.Execute([BossMod.ActorState+OpCreate]::new(102,0x1A6F,1,0,"Alte Roite",5629,"Enemy","None",70,$pos,1,$hp,$true,$false,0,0,0))
        $ws.Execute([BossMod.ActorState+OpCombat]::new(102,$true))
        $mgr.Update()
        if(!$plugin.GetField('recording',$instance).GetValue($owner)){throw 'Deltascape activation missed'}
        AdvanceFixture 5
        $ws.Execute([BossMod.ActorState+OpHPMP]::new(102,[BossMod.ActorHPMP]::new(0,100,0,0,0)))
        $ws.Execute([BossMod.ActorState+OpDead]::new(102,$true))
        $mgr.Update()
        if(!$plugin.GetField('finishing',$instance).GetValue($owner)){throw 'Deltascape death missed'}
        $plugin.GetMethod('Finish',$instance).Invoke($owner,@())
        $file=$o1.GetFiles('Pull-*.log')
        if($file.Length -ne 1){throw 'Deltascape recording missing'}
        $check=[BossMod.ReplayParserLog]::Parse($file[0].FullName,[ref]$progress,[Threading.CancellationToken]::None)
        if($summary.Invoke($null,@($check)).Outcome -ne 'Success'){throw 'Deltascape replay outcome wrong'}
        'Simulated Deltascape V1.0 pull, death, archive and Success verified.'
    }finally{$on.Dispose();$off.Dispose();$mgr.Dispose()}
    $fallbackFolder=$testFolder.CreateSubdirectory("module-free")
    $plugin.GetField('pullDirectory',$instance).SetValue($owner,$fallbackFolder)
    $plugin.GetField('dutyCombat',$instance).SetValue($owner,(New-Object object))
    $ws.CurrentCFCID=253
    $ws.Execute([BossMod.ActorState+OpDestroy]::new(102))
    $ws.Execute([BossMod.ActorState+OpCombat]::new(101,$true))
    $updateCombat=$plugin.GetMethod('UpdateDutyCombat',$instance)
    $updateCombat.Invoke($owner,@())
    AdvanceFixture 10
    $plugin.GetMethod('MarkDutyOutcome',$instance).Invoke($owner,@([uint32]253,"Success"))
    if(!$plugin.GetField('finishing',$instance).GetValue($owner)){throw 'Duty clear did not close the generic pull'}
    $plugin.GetMethod('Finish',$instance).Invoke($owner,@())
    $files=$fallbackFolder.GetFiles('Pull-*-Duty253-*.log')
    if($files.Length -ne 1){throw 'Module-free duty recording missing'}
    $check=[BossMod.ReplayParserLog]::Parse($files[0].FullName,[ref]$progress,[Threading.CancellationToken]::None)
    $result=$summary.Invoke($null,@($check))
    if($check.Encounters.Count -ne 0 -or $result.Outcome -ne 'Success' -or $result.Duration -ne '0:05' -or $result.Members.Length -ne 1){throw "Module-free summary failed: $result"}
    $plugin.GetMethod('LoadTracks',$instance).Invoke($owner,@($check))
    'Module-free duty capture, roster, duration, clear and playback tracks verified.'
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
