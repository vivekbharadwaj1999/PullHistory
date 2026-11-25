#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace PullHistory;

public sealed partial class Plugin
{
    private sealed class Track
    {
        public string Name, Label;
        public bool Enemy;
        public uint JobId;
        public ulong InstanceId;
        public (DateTime,Vector4)[] Positions;
        public (DateTime,bool)[] Deaths;
        public (DateTime,DateTime)[] Exists;
    }

    private object playbackEntry;
    private FileInfo playbackFile;
    private string playbackError;
    private Track[] tracks;
    private (DateTime Time,string Name)[] deaths;
    private DateTime first, last;
    private Vector2 center;
    private float extent, seconds, rate = 1;
    private bool playing, playbackOpen;
    private long clockTick;

    private static T Property<T>(object obj, string name) =>
        (T)obj.GetType().GetProperty(name).GetValue(obj);

    private void LoadTracks(object replay)
    {
        var ops = (IList)Read(replay, "Ops");
        if (ops.Count == 0) throw new InvalidDataException("This replay contains no events.");
        first = (DateTime)Read(ops[0], "Timestamp");
        last = (DateTime)Read(ops[ops.Count - 1], "Timestamp");
        var jobs = new Dictionary<object,object>();
        var bosses = new HashSet<ulong>();
        foreach (var e in (IEnumerable)Read(replay, "Encounters"))
        {
            bosses.Add(Property<ulong>(e,"InstanceID"));
            foreach (var member in (IEnumerable)Read(e,"PartyMembers"))
                jobs[Read(member,"Item1")] = Read(member,"Item2");
        }
        var found = new List<Track>();
        var classes = new Dictionary<ulong,object>();
        foreach (var op in ops)
        {
            if (op.GetType().DeclaringType?.Name == "ActorState" && op.GetType().Name == "OpCreate")
                classes[(ulong)Read(op,"InstanceID")] = Read(op,"Class");
        }
        foreach (var p in (IEnumerable)Read(replay,"Participants"))
        {
            var id = Property<ulong>(p,"InstanceID");
            var type = Read(p,"Type").ToString();
            bool enemy = bosses.Contains(id);
            if (!enemy && type != "Player") continue;
            var positions = ((IEnumerable<(DateTime,Vector4)>)Read(p,"PosRotHistory")).ToArray();
            if (positions.Length == 0) continue;
            var names = (IEnumerable<(DateTime,(string,uint))>)Read(p,"NameHistory");
            string name = names.LastOrDefault().Item2.Item1 ?? (enemy ? "Boss" : "Player");
            var exists = ((IEnumerable)Read(p,"WorldExistence")).Cast<object>().Select(r =>
                (Property<DateTime>(r,"Start"),Property<DateTime>(r,"End"))).ToArray();
            if (!jobs.TryGetValue(p, out var job) || Convert.ToUInt32(job) == 0)
                classes.TryGetValue(id, out job);
            found.Add(new Track { InstanceId=id, Name=name, Label=job != null && !enemy ? job.ToString() : name,
                JobId=enemy ? 0u : Convert.ToUInt32(job), Enemy=enemy, Positions=positions, Exists=exists,
                Deaths=((IEnumerable<(DateTime,bool)>)Read(p,"DeadHistory")).ToArray() });
        }
        var points = found.SelectMany(t => t.Positions).Select(p => new Vector2(p.Item2.X,p.Item2.Z))
            .Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToArray();
        if (points.Length == 0) throw new InvalidDataException("No recorded positions in this pull.");
        var min = points.Aggregate(Vector2.Min);
        var max = points.Aggregate(Vector2.Max);
        center = (max + min) / 2;
        extent = Math.Max(12, Math.Max(max.X-min.X,max.Y-min.Y)/2 + 3);
        deaths = found.Where(t => !t.Enemy).SelectMany(t => t.Deaths.Where(d => d.Item2).Select(d => (d.Item1,t.Name)))
            .OrderBy(d => d.Item1).ToArray();
        tracks = found.ToArray(); LoadRotations(replay); LoadOutput((BossMod.Replay)replay);
    }

    private static int At<T>((DateTime,T)[] events, DateTime time)
    {
        int lo=0, hi=events.Length;
        while (lo<hi)
        {
            int mid=lo+(hi-lo)/2;
            if (events[mid].Item1<=time) lo=mid+1; else hi=mid;
        }
        return lo-1;
    }

    private void DrawPlayback()
    {
        if (!playbackOpen || playbackEntry == null) { ReleaseRadar(); return; }
        ImGui.SetNextWindowSize(new Vector2(1460, 570), ImGuiCond.FirstUseEver);
        var visible = ImGui.Begin("Pull playback###PullRadarPlayback", ref playbackOpen);
        try
        {
            if (!visible) { clockTick=Stopwatch.GetTimestamp(); return; }
            if (ImGui.Button("Pull list"))
            {
                playbackOpen=false;
                ShowList();
                return;
            }
            ImGui.SameLine();
            ImGui.TextUnformatted(PullLabel(playbackFile));
            ImGui.SameLine();
            bool showRotations = playbackSettings.ShowRotationsAndCastBars;
            if (ImGui.Checkbox("Show rotations and cast bars", ref showRotations))
            {
                playbackSettings.ShowRotationsAndCastBars = showRotations;
                pluginInterface.SavePluginConfig(playbackSettings);
            }
            if (tracks == null && playbackError == null)
            {
                var task=(Task<BossMod.Replay>)playbackEntry;
                if (!task.IsCompleted) { ImGui.TextUnformatted("Loading pull…"); return; }
                if (!task.IsCompletedSuccessfully) throw new InvalidDataException("BMR could not read this recording.");
                var replay = task.Result;
                LoadTracks(replay);
                radar = new ReplayRadar(replay);
                clockTick=Stopwatch.GetTimestamp();
            }
            if (playbackError == null)
            {
                float duration=(float)(last-first).TotalSeconds;
                long now=Stopwatch.GetTimestamp();
                if (playing) seconds=Math.Min(duration,seconds+(float)((now-clockTick)/(double)Stopwatch.Frequency)*rate);
                if (seconds>=duration) playing=false;
                clockTick=now;
                DrawEncounter(first.AddSeconds(seconds));
                float slider=seconds;
                ImGui.SetNextItemWidth(-1);
                if (dragSeek.HasValue) slider=dragSeek.Value;
                if (ImGui.SliderFloat("##seek",ref slider,0,Math.Max(.001f,duration),"%.1f s"))
                    dragSeek=Math.Clamp(slider,0,duration);
                if (dragSeek.HasValue && !ImGui.IsItemActive()) { seconds=dragSeek.Value; dragSeek=null; }
                if (ImGui.Button(playing ? "Pause" : "Play")) { if(seconds>=duration) seconds=0; playing=!playing; }
                ImGui.SameLine();
                if (ImGui.Button("-5s")) seconds=Math.Max(0,seconds-5);
                ImGui.SameLine();
                if (ImGui.Button("+5s")) seconds=Math.Min(duration,seconds+5);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(76);
                if (ImGui.BeginCombo("##speed",$"{rate:0.##}x"))
                {
                    foreach(float speed in new[]{.25f,.5f,1f,2f,4f})
                        if(ImGui.Selectable($"{speed:0.##}x",speed==rate)) rate=speed;
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{Stamp(seconds)} / {Stamp(duration)}");
                if (deaths.Length>0 && ImGui.BeginCombo("##deaths","Jump to a death"))
                {
                    for(int i=0;i<deaths.Length;i++)
                    {
                        float at=Math.Clamp((float)(deaths[i].Time-first).TotalSeconds,0,duration);
                        if(ImGui.Selectable($"{Stamp(at)}   {deaths[i].Name}##{i}")) { seconds=at; playing=false; }
                    }
                    ImGui.EndCombo();
                }
            }
            else ImGui.TextUnformatted(playbackError);
        }
        catch(Exception ex)
        {
            if(playbackError==null) log.Error(ex,"Could not display pull playback.");
            playbackError="Playback unavailable for this recording.";
            playing=false;
        }
        finally { ImGui.End(); }
    }

    private static string Stamp(float sec) => $"{(int)sec/60}:{(int)sec%60:D2}";
}
