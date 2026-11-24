param(
    [Parameter(Mandatory)][string]$AssemblyFile,
    [Parameter(Mandatory)][string]$Archive,
    [string]$Dalamud="$env:APPDATA/XIVLauncher/addon/Hooks/dev"
)
$ErrorActionPreference='Stop'
$lib=(Resolve-Path $AssemblyFile).Path
foreach($name in 'InteropGenerator.Runtime','FFXIVClientStructs','Lumina','Lumina.Excel','Dalamud','Dalamud.Bindings.ImGui','Dalamud.Bindings.ImPlot','Dalamud.Common','Newtonsoft.Json','TerraFX.Interop.Windows'){
    [void][Reflection.Assembly]::LoadFrom((Join-Path $Dalamud "$name.dll"))
}
[void][Reflection.Assembly]::LoadFrom((Join-Path (Split-Path $lib) 'RebornMaterial.dll'))
[void][Reflection.Assembly]::LoadFrom($lib)
$refs=@($lib)+@(Get-ChildItem "$PSHOME/ref/*.dll"|ForEach-Object FullName)
Add-Type -ReferencedAssemblies $refs -Path (Join-Path $PSScriptRoot 'SampleArena.cs')
[BossMod.Service]::Config.Initialize()
foreach($fight in 'M10S','M11S'){
    $file=Get-ChildItem -LiteralPath $Archive -Filter "*$fight*.log"|Sort-Object Length -Descending|Select-Object -First 1
    if(!$file){throw "Missing $fight recording"}
    $progress=[single]0
    $rec=[BossMod.ReplayParserLog]::Parse($file.FullName,[ref]$progress,[Threading.CancellationToken]::None)
    [ArenaSample]::Digest($rec)
}
