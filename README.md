# Pull History

A standalone Dalamud plugin for recording encounter pulls and reviewing them with an arena radar. Open the list with `/lastpull`. The table shows the time, encounter, outcome, duration and party jobs. Click a job to open playback. The settings button controls retention. The replay has selectable hitboxes, vitals, status tooltips, casts and nine-action player timelines.

## Installation

Add the following custom plugin repository in Dalamud Settings and install **Pull History**. Enable automatic updates for Pull History in Dalamud. Disable the old development copy of Pull History before installing this version. BossMod Reborn can be disabled. Existing Pull History recordings in the previous replay folder are supported.

    https://raw.githubusercontent.com/vivekbharadwaj1999/PullHistory/dalamud/repo.json

Recordings are stored on your computer. New installations use the plugin configuration folder under `replays/PullHistory`. Existing BossMod Reborn replay-folder and anonymization settings are recognized. An interrupted recording remains in the InProgress folder.

## Building and verifying

The standalone source branch is `pull-history-standalone`. The `dalamud` branch contains the install manifest and versioned packages. Use PowerShell 7, .NET 10, and the matching Dalamud development assemblies. From this branch run `tools/Build-Release.ps1` with the .NET executable, a local pull archive containing M10S and M11S, the matching upstream reference DLL, and an empty output folder. Verification includes capture and readback, retention, encounter playback, roster, rotations, rewind, package extraction and a sampled mechanic-geometry comparison against the same upstream release.

## Credits

The encounter modules, radar and replay engine are derived from [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn). See LICENSE and THIRD_PARTY_NOTICES.md. RebornMaterial is shipped as a separate library. The source of the included 7.5.8.4 release is available at [the matching revision](https://github.com/LTS-FFXIV/RebornMaterial/tree/0c74204ecfd6785c27ac53213950c3ad08337e63). Its license texts are in licenses/.
