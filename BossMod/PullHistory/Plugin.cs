#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace PullHistory;

public sealed partial class Plugin : IAsyncDalamudPlugin
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly HashSet<object> active = new(ReferenceEqualityComparer.Instance);
    private BossMod.WorldState world;
    private BossMod.WorldStateGameSync gameSync;
    private BossMod.BossModuleManager encounterManager;
    private System.Threading.CancellationTokenSource playbackCancel;
    private DirectoryInfo pullDirectory;
    private bool anonymize;
    private string gameVersion="unknown";
    private IDisposable starts, ends, recording;
    private DirectoryInfo archive;
    private string pendingPath;
    private bool finishing;


    public sealed class PlaybackSettings : Dalamud.Configuration.IPluginConfiguration
    {
        public int Version { get; set; } = 1;
        public string LogFolder { get; set; }
        public int PullLimit { get; set; } = 100;
        public bool ShowRotationsAndCastBars { get; set; }
    }

    private readonly PlaybackSettings playbackSettings;
    private readonly IDalamudPluginInterface pluginInterface;
    private bool listOpen, listDirty = true;
    private (FileInfo File, string Label)[] rows = Array.Empty<(FileInfo, string)>();
    private string listMessage;

    private readonly ITextureProvider textures;
    private readonly IDataManager data;

    public Plugin(IDataManager data, ITextureProvider textures, IDalamudPluginInterface pluginInterface, ICommandManager commands, IFramework framework, IChatGui chat, IPluginLog log)
    {
        this.textures = textures;
        this.data = data;
        this.commands = commands;
        this.framework = framework;
        this.chat = chat;
        this.log = log;
        this.pluginInterface = pluginInterface;
        playbackSettings = pluginInterface.GetPluginConfig() as PlaybackSettings ?? new PlaybackSettings();
        try { pullDirectory=ResolveArchive(pluginInterface,out anonymize); }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.Warning(ex,"Could not read the previous archive configuration.");
            pullDirectory=new DirectoryInfo(Path.Combine(pluginInterface.ConfigDirectory.FullName,"replays","PullHistory"));
            anonymize=true;
        }
        if (!string.IsNullOrWhiteSpace(playbackSettings.LogFolder) && Path.IsPathFullyQualified(playbackSettings.LogFolder))
            pullDirectory = new DirectoryInfo(playbackSettings.LogFolder);
        pluginInterface.Create<BossMod.Service>();
        BossMod.Service.LuminaGameData=data.GameData;
        BossMod.Service.Config.Initialize();
        LoadRadarProfile(pluginInterface.ConfigDirectory.Parent!);
        BossMod.Service.LogHandlerDebug=message=>log.Debug(message);
        BossMod.Service.LogHandlerVerbose=message=>log.Verbose(message);
        var gameFolder=Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName);
        if(gameFolder!=null && File.Exists(Path.Combine(gameFolder,"ffxivgame.ver")))
            gameVersion=File.ReadAllText(Path.Combine(gameFolder,"ffxivgame.ver")).Trim();
        pluginInterface.ConfigDirectory.Create();
        InteropGenerator.Runtime.Resolver.GetInstance.Setup(BossMod.Service.SigScanner.SearchBase,
            gameVersion,new FileInfo(Path.Combine(pluginInterface.ConfigDirectory.FullName,"cs.json")));
        FFXIVClientStructs.Interop.Generated.Addresses.Register();
    }

    public async System.Threading.Tasks.Task LoadAsync(System.Threading.CancellationToken cancellationToken)
    {
        await System.Threading.Tasks.Task.Run(InteropGenerator.Runtime.Resolver.GetInstance.Resolve,cancellationToken);
        await framework.RunOnFrameworkThread(Start);
    }

    private void Start()
    {
        BossMod.Dx11ArenaRenderer.Initialize(pluginInterface.UiBuilder.DeviceHandle);
        try { InitializeRecording(); }
        catch { BossMod.Dx11ArenaRenderer.Shutdown(); throw; }
        commands.AddHandler("/lastpull", new CommandInfo((_, _) => ShowList()) { HelpMessage = "Browse saved boss pulls." });
        pluginInterface.UiBuilder.Draw += DrawList;
        pluginInterface.UiBuilder.Draw += DrawPlayback;
        pluginInterface.UiBuilder.OpenMainUi += ShowList;
        framework.Update += Update;
    }

    private static FieldInfo Field(Type type, string name, BindingFlags flags = Instance) =>
        type.GetField(name, flags) ?? throw new MissingFieldException(type.FullName, name);
    private static object Read(object obj, string name) => Field(obj.GetType(), name).GetValue(obj);

    private void LoadRadarProfile(DirectoryInfo directory)
    {
        var config = BossMod.Service.Config;
        var radarConfig = config.Get<BossMod.BossModuleConfig>();
        radarConfig.ShowCardinals = radarConfig.ShowWaymarks = radarConfig.ShowSigns = true;
        var path = Path.Combine(directory.FullName, "BossModReborn.json");
        if (!File.Exists(path)) return;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var payload = json.RootElement.GetProperty("Payload");
            var options = BossMod.Serialization.BuildSerializationOptions();
            foreach (var (type, node) in config._nodes)
            {
                bool encounter = type.Namespace?.Split('.').ElementAtOrDefault(1) is
                    "RealmReborn" or "Heavensward" or "Stormblood" or "Shadowbringers" or "Endwalker" or "Dawntrail" or "Global";
                if (!(encounter || node is BossMod.BossModuleConfig or BossMod.ZoneModuleConfig or BossMod.ColorConfig or BossMod.PartyRolesConfig)) continue;
                if (!payload.TryGetProperty(type.FullName!, out var settings)) continue;
                try { node.Deserialize(settings, options); }
                catch (Exception ex) { log.Warning(ex, $"Could not read replay settings for {type.Name}."); }
            }
        }
        catch (Exception ex) { log.Warning(ex, "Could not read BMR radar settings."); }
        // Recorded actors belong only in the playback panel.
        radarConfig.ProjectRadarInto3DWorld = false;
    }

    private static DirectoryInfo ResolveArchive(IDalamudPluginInterface pi,out bool anonymize)
    {
        anonymize=false;
        var configs=pi.ConfigDirectory.Parent!;
        var legacy=new FileInfo(Path.Combine(configs.FullName,"BossModReborn.json"));
        if(legacy.Exists)
        {
            using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(legacy.FullName));
            if(json.RootElement.TryGetProperty("Payload",out var payload)
                && payload.TryGetProperty("BossMod.ReplayManagementConfig",out var config))
            {
                if(config.TryGetProperty("Anonymize",out var anon)) anonymize=anon.GetBoolean();
                if(config.TryGetProperty("ReplayFolder",out var folder) && !string.IsNullOrWhiteSpace(folder.GetString()))
                    return new DirectoryInfo(Path.Combine(folder.GetString()!,"PullHistory"));
            }
            var old=new DirectoryInfo(Path.Combine(configs.FullName,"BossModReborn","replays","PullHistory"));
            if(old.Exists) return old;
        }
        return new DirectoryInfo(Path.Combine(pi.ConfigDirectory.FullName,"replays","PullHistory"));
    }

    private unsafe void InitializeRecording()
    {
        var fw=FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance();
        world=new BossMod.WorldState((ulong)fw->PerformanceCounterFrequency,
            gameVersion);
        encounterManager=new BossMod.BossModuleManager(world);
        starts=encounterManager.ModuleActivated.Subscribe(Begin);
        ends=encounterManager.ModuleDeactivated.Subscribe(End);
        try { gameSync=new BossMod.WorldStateGameSync(world); }
        catch { starts.Dispose(); ends.Dispose(); encounterManager.Dispose(); throw; }
    }

    private void Update(IFramework frame)
    {
        if(finishing && active.Count==0) Finish();
        gameSync.Update(frame.UpdateDelta);
        encounterManager.Update();
    }

    private DirectoryInfo PullFolder()=>pullDirectory;

    private void Begin(object module)
    {
        try
        {
            if (Convert.ToUInt32(Read(world, "CurrentCFCID")) == 0) return;
            if (finishing && active.Count == 0) Finish();
            if (!active.Add(module) || active.Count != 1) return;
            archive = PullFolder();
            var pending = archive.CreateSubdirectory("InProgress");
            var type = world.GetType().Assembly.GetType("BossMod.ReplayRecorder", true);
            var ctor = type.GetConstructors().Single(c => c.GetParameters().Length == 6);
            var format = Enum.Parse(ctor.GetParameters()[1].ParameterType, "BinaryCompressed");
            var prefix = $"Pull-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{module.GetType().Name}-{Guid.NewGuid():N}";
            recording = (IDisposable)ctor.Invoke(new object[] { world, format, true, pending, prefix, anonymize });
            pendingPath = (string)Read(recording, "LogPath");
        }
        catch (Exception ex) { log.Error(ex, "Could not start a pull replay."); }
    }

    private void End(object module)
    {
        if (active.Remove(module) && active.Count == 0) finishing = true;
    }

    private static FileInfo[] History(DirectoryInfo folder) => !folder.Exists ? Array.Empty<FileInfo>() :
        folder.GetFiles("Pull-*.log").Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name,
            @"^Pull-\d{8}-\d{6}-\d{7}-\w+-[a-f0-9]{32}_\d{4}_\d{2}_\d{2}_\d{2}_\d{2}_\d{2}\.log$"))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal).ToArray();

    private void Finish()
    {
        finishing = false;
        var writer = recording;
        recording = null;
        var path = pendingPath;
        pendingPath = null;
        if (writer == null) return;
        try
        {
            writer.Dispose();
            var info = new FileInfo(path);
            if (info.Length <= 4) return;
            info.MoveTo(Path.Combine(archive.FullName, info.Name));
            listDirty = true;
            TrimHistory(archive);
        }
        catch (Exception ex) { log.Error(ex, "Could not finish saving the pull replay."); }
    }

    private void ShowList()
    {
        listOpen = true;
        listDirty = true;
    }

    private static string PullLabel(FileInfo file)
    {
        var parts = Path.GetFileNameWithoutExtension(file.Name).Split('-');
        if (!DateTime.TryParseExact(string.Join("-", parts, 1, 3), "yyyyMMdd-HHmmss-fffffff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start))
            return file.Name;
        var encounter = Regex.Replace(parts[4], @"(?<=[a-z0-9])(?=[A-Z][a-z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return $"{start.ToLocalTime():yyyy-MM-dd  HH:mm:ss}    {encounter}";
    }

    private void DrawList()
    {
        folderDialog.Draw();
        if (!listOpen) return;
        if (listDirty)
        {
            listDirty = false;
            try
            {
                rows = History(PullFolder()).Select(f => (f, PullLabel(f))).ToArray();
                fightWidth=ImGui.GetTextLineHeight()*13;
                foreach(var row in rows)
                    fightWidth=Math.Max(fightWidth,ImGui.CalcTextSize(FightName(row.Label)+" (Success)").X+8);
                listMessage = rows.Length == 0 ? "No completed pulls yet." : null;
            }
            catch (Exception ex)
            {
                rows = Array.Empty<(FileInfo, string)>();
                listMessage = "Could not read the replay folder. Reopen this list to retry.";
                log.Error(ex, "Reading pull history failed.");
            }
        }
        if(fightWidth<=0) fightWidth=ImGui.GetTextLineHeight()*13;
        ImGui.SetNextWindowSize(new Vector2(Math.Min(HistoryWidth(),ImGui.GetMainViewport().WorkSize.X),420),ImGuiCond.FirstUseEver);
        var visible = ImGui.Begin("Pull History###PullHistoryOutcomeList", ref listOpen);
        try
        {
            if (!visible) return;

            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"Saved pulls ({rows.Length})");
            ImGui.SameLine();
            float settingsWidth=ImGui.CalcTextSize("Refresh").X+ImGui.CalcTextSize("Settings").X
                +4*ImGui.GetStyle().FramePadding.X+ImGui.GetStyle().ItemSpacing.X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX()+Math.Max(0,ImGui.GetContentRegionAvail().X-settingsWidth));
            if (ImGui.Button("Refresh"))
            {
                summaryCancel?.Cancel();
                summaries.Clear();
                listDirty = true;
                refreshIndex = 0;
                scrollToNewest = true;
            }
            ImGui.SameLine();
            DrawHistorySettings();
            ImGui.Separator();
            if (listMessage != null) ImGui.TextUnformatted(listMessage);
            else DrawHistoryTable();
            if (refreshIndex >= 0 && !listDirty)
            {
                while (refreshIndex < rows.Length && summaries.ContainsKey(rows[refreshIndex].File.FullName)) refreshIndex++;
                if (refreshIndex < rows.Length) SummaryFor(rows[refreshIndex].File);
                else refreshIndex = -1;
            }
        }
        finally { ImGui.End(); }
    }

    private void OpenReplay(FileInfo file)
    {
        try
        {
            playbackCancel?.Cancel();
            playbackCancel?.Dispose();
            playbackCancel=new();
            var token=playbackCancel.Token;
            ReleaseRadar();
            playbackEntry=System.Threading.Tasks.Task.Run(()=>
            {
                float progress=0;
                return BossMod.ReplayParserLog.Parse(file.FullName, ref progress,token);
            },token);
            playbackFile = file;
            playbackOpen = true;
            playbackError = null;
            tracks = null;
            playing = false;
            seconds = 0;
            listOpen = false;
        }
        catch (Exception ex) { listDirty = true; log.Error(ex, "Could not open selected pull."); chat.Print("Pull History: could not read the selected recording."); }
    }

    private void Detach()
    {
        summaryCancel?.Cancel();
        ReleaseRadar();
        playbackEntry = null;
        tracks = null;
        playbackOpen = false;
        starts?.Dispose();
        ends?.Dispose();
        starts = ends = null;
        // An unload mid-pull leaves a closed partial file outside the completed history.
        if (finishing) Finish();
        try { recording?.Dispose(); } catch (Exception ex) { log.Error(ex, "Closing partial pull failed."); }
        recording = null;
        pendingPath = null;
        active.Clear();
        finishing = false;
        playbackCancel?.Cancel();
        playbackCancel?.Dispose();
        gameSync?.Dispose();
        encounterManager?.Dispose();
    }

    public async ValueTask DisposeAsync() => await framework.RunOnFrameworkThread(Stop);

    private void Stop()
    {
        pluginInterface.UiBuilder.Draw -= DrawList;
        pluginInterface.UiBuilder.Draw -= DrawPlayback;
        pluginInterface.UiBuilder.OpenMainUi -= ShowList;
        framework.Update -= Update;
        commands.RemoveHandler("/lastpull");
        Detach();
        BossMod.Dx11ArenaRenderer.Shutdown();
    }
}
