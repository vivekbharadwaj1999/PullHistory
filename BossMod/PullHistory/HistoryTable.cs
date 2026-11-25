#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace PullHistory;
public sealed partial class Plugin
{
    private sealed record Member(uint Job,string Name);
    private sealed record PullSummary(string Duration,Member[] Members,string Outcome="Unknown");
    private readonly Dictionary<string,PullSummary> summaries=new(StringComparer.OrdinalIgnoreCase);
    private Task<PullSummary> summaryTask;
    private string summaryPath;
    private CancellationTokenSource summaryCancel;
    private int editedLimit;
    private int refreshIndex = -1;
    private bool scrollToNewest;

    private static PullSummary Summarize(object replay)
    {
        var roster=new Dictionary<ulong,Member>();
        DateTime firstStart=DateTime.MaxValue,lastEnd=DateTime.MinValue;
        foreach(dynamic encounter in (IEnumerable)Read(replay,"Encounters"))
        {
            DateTime start=encounter.Time.Start,end=encounter.Time.End;
            if(end>=start && start!=DateTime.MinValue && end!=DateTime.MaxValue)
            {
                if(start<firstStart) firstStart=start;
                if(end>lastEnd) lastEnd=end;
            }
            foreach(dynamic entry in encounter.PartyMembers)
            {
                dynamic who=entry.Item1;
                if(who==null || who.Type.ToString()!="Player") continue;
                AddMember(roster,(object)who,Convert.ToUInt32((object)entry.Item2));
            }
        }
        if(roster.Count==0)
        {
            var jobs=new Dictionary<ulong,uint>();
            foreach(var op in (IEnumerable)Read(replay,"Ops"))
                if(op.GetType().Name=="OpCreate" && op.GetType().DeclaringType?.Name=="ActorState")
                    jobs[(ulong)Read(op,"InstanceID")]=Convert.ToUInt32(Read(op,"Class"));
            foreach(dynamic who in (IEnumerable)Read(replay,"Participants"))
                if((string)who.Type.ToString()=="Player" && jobs.TryGetValue((ulong)who.InstanceID,out var job))
                    AddMember(roster,(object)who,job);
        }
        var logData=(BossMod.Replay)replay;
        if (firstStart == DateTime.MaxValue && logData.Ops.Count > 0)
        {
            firstStart=logData.Ops[0].Timestamp;
            lastEnd=logData.Ops[^1].Timestamp;
        }
        string duration=lastEnd>=firstStart ? Stamp((float)(lastEnd-firstStart).TotalSeconds) : "—";
        string outcome="Unknown";
        foreach(dynamic update in (IEnumerable)Read(replay,"DirectorUpdates"))
        {
            if(firstStart!=DateTime.MaxValue && update.Timestamp<firstStart) continue;
            uint id=update.UpdateID;
            if(id==0x40000003 || id==0x40000002) outcome="Success";
            else if(id==0x40000005) outcome="Failure";
        }
        if (outcome == "Unknown" && replay is BossMod.Replay recording && recording.Encounters.Count == 1)
        {
            var fight = recording.Encounters[0];
            var boss = recording.FindParticipant(fight.InstanceID, fight.Time.Start);
            if (boss?.Type == BossMod.ActorType.Enemy && boss.OID == fight.OID && lastEnd >= firstStart
                && boss.HPMPAt(lastEnd) is { CurHP: 0, MaxHP: > 0 } && boss.DeadAt(lastEnd)
                && boss.DeadHistory.Any(change => change.Item2 && change.Item1 >= firstStart
                    && change.Item1 <= lastEnd && (lastEnd - change.Item1).TotalSeconds <= 2))
                outcome = "Success";
        }
        foreach (var marker in logData.UserMarkers)
        {
            if (marker.Text == "PullHistory.Outcome.Success") outcome = "Success";
            if (marker.Text == "PullHistory.Outcome.Failure") outcome = "Failure";
        }
        return new PullSummary(duration,roster.Values.ToArray(),outcome);
    }

    private static void AddMember(Dictionary<ulong,Member> roster,object participant,uint job)
    {
        ulong id=Property<ulong>(participant,"InstanceID");
        if(roster.ContainsKey(id)) return;
        var names=(IEnumerable<(DateTime,(string,uint))>)Read(participant,"NameHistory");
        roster[id]=new Member(job,names.LastOrDefault().Item2.Item1 ?? "Unknown player");
    }

    private PullSummary SummaryFor(FileInfo file)
    {
        if(summaryTask?.IsCompleted==true)
        {
            if(!summaryCancel.IsCancellationRequested && summaryTask.IsCompletedSuccessfully) summaries[summaryPath]=summaryTask.Result;
            else if(summaryTask.IsFaulted)
            {
                log.Error(summaryTask.Exception,"Could not read pull metadata for {Path}",summaryPath);
                summaries[summaryPath]=new PullSummary("Unavailable",Array.Empty<Member>());
            }
            summaryTask=null;
            summaryCancel.Dispose();
            summaryCancel=null;
        }
        if(summaries.TryGetValue(file.FullName,out var cached)) return cached;
        if(summaryTask==null)
        {
            summaryPath=file.FullName;
            summaryCancel=new CancellationTokenSource();
            var token=summaryCancel.Token;
            summaryTask=Task.Run(()=>{float progress=0; return Summarize(BossMod.ReplayParserLog.Parse(file.FullName,ref progress,token));},token);
        }
        return null;
    }

    private int HistoryLimit=>Math.Max(1,playbackSettings?.PullLimit ?? 100);
    private void TrimHistory(DirectoryInfo folder)
    {
        foreach(var old in History(folder).Skip(HistoryLimit)) old.Delete();
    }

    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager folderDialog = new();

    private void SelectLogFolder(bool selected, string path)
    {
        if (!selected) return;
        var previous = playbackSettings.LogFolder;
        try
        {
            var destination = new DirectoryInfo(path);
            // Probe the same staging folder the recorder will use.
            var staging = destination.CreateSubdirectory("InProgress");
            using (new FileStream(Path.Combine(staging.FullName, Guid.NewGuid().ToString("N")), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            playbackSettings.LogFolder = destination.FullName;
            pluginInterface.SavePluginConfig(playbackSettings);
            pullDirectory = destination;
            listDirty = true;
        }
        catch (Exception ex)
        {
            playbackSettings.LogFolder = previous;
            log.Warning(ex, "Could not select replay folder {Path}", path);
            chat.Print("Pull History: could not use that folder. Check that you can write to it and try again.");
        }
    }

    private void DrawHistorySettings()
    {
        if(ImGui.Button("Settings"))
        {
            editedLimit=HistoryLimit;
            ImGui.OpenPopup("Pull history settings");
        }
        if(!ImGui.BeginPopup("Pull history settings")) return;
        try
        {
            ImGui.TextUnformatted("Log folder");
            ImGui.TextUnformatted(PullFolder().FullName);
            if (ImGui.Button("Choose log folder..."))
            {
                ImGui.CloseCurrentPopup();
                folderDialog.OpenFolderDialog("Choose Pull History log folder", SelectLogFolder, PullFolder().FullName);
            }
            ImGui.Separator();
            ImGui.SetNextItemWidth(130);
            ImGui.InputInt("Latest pulls to keep",ref editedLimit);
            if(editedLimit<1) ImGui.TextUnformatted("Enter at least 1.");
            if(editedLimit>0 && editedLimit<rows.Length)
                ImGui.TextUnformatted($"Saving will delete the oldest {rows.Length-editedLimit} archived pulls.");
            if(ImGui.Button("Save") && editedLimit>0)
            {
                playbackSettings.PullLimit=editedLimit;
                pluginInterface.SavePluginConfig(playbackSettings);
                listDirty=true;
                try { TrimHistory(PullFolder()); }
                catch(Exception ex){log.Error(ex,"Could not trim pull history.");chat.Print("Pull History: could not remove all older recordings.");}
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if(ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();
        }
        finally { ImGui.EndPopup(); }
    }

    private float fightWidth;
    private static string FightName(string label)
    {
        int split=label.IndexOf("    ",StringComparison.Ordinal);
        return split<0 ? "Unknown fight" : label.Substring(split+4);
    }

    private float HistoryWidth()
    {
        float unit=ImGui.GetTextLineHeight();
        var style=ImGui.GetStyle();
        return Math.Max(760,unit*17+fightWidth+8*(2*JobIconHalfSize+2)+8*style.CellPadding.X
            +2*style.WindowPadding.X+style.ScrollbarSize+8);
    }

    private void DrawHistoryTable()
    {
        var flags=ImGuiTableFlags.Resizable|ImGuiTableFlags.ScrollX|ImGuiTableFlags.ScrollY|ImGuiTableFlags.BordersInnerV;
        if(!ImGui.BeginTable("Pull archive outcomes",4,flags)) return;
        try
        {
            float unit=ImGui.GetTextLineHeight();
            ImGui.TableSetupColumn("Date and time",ImGuiTableColumnFlags.WidthFixed,unit*10);
            ImGui.TableSetupColumn("Fight",ImGuiTableColumnFlags.WidthFixed,fightWidth);
            ImGui.TableSetupColumn("Combat duration",ImGuiTableColumnFlags.WidthFixed,unit*7);
            ImGui.TableSetupColumn("Players",ImGuiTableColumnFlags.WidthFixed,8*(2*JobIconHalfSize+2));
            ImGui.TableSetupScrollFreeze(0,1);
            ImGui.TableHeadersRow();
            if (scrollToNewest) { ImGui.SetScrollY(0); scrollToNewest = false; }
            foreach(var row in rows)
            {
                float side=2*JobIconHalfSize;
                ImGui.TableNextRow(ImGuiTableRowFlags.None,side+4);
                ImGui.TableNextColumn();
                var clip=ImGui.GetWindowDrawList();
                float y=ImGui.GetCursorScreenPos().Y;
                if(y+side<clip.GetClipRectMin().Y || y>clip.GetClipRectMax().Y) continue;
                var labels=row.Label.Split(new[]{"    "},2,StringSplitOptions.None);
                ImGui.PushID(row.File.Name);
                bool open=ImGui.Selectable(labels[0],false,ImGuiSelectableFlags.None,new Vector2(0,side));
                ImGui.TableNextColumn();
                var summary=SummaryFor(row.File);
                string fight=FightName(row.Label);
                if(summary!=null) fight+=" ("+summary.Outcome+")";
                open|=ImGui.Selectable(fight+"###fight",false,ImGuiSelectableFlags.None,new Vector2(0,side));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(summary?.Duration ?? "…");
                ImGui.TableNextColumn();
                if(summary==null) ImGui.TextUnformatted("…");
                else for(int i=0;i<summary.Members.Length;i++)
                {
                    var member=summary.Members[i];
                    if(i>0) ImGui.SameLine(0,2);
                    var icon=member.Job>0 ? textures.GetFromGameIcon(62100u+member.Job).GetWrapOrDefault() : null;
                    if(icon!=null) ImGui.Image(icon.Handle,new Vector2(side));
                    else ImGui.TextUnformatted("?");
                    if(ImGui.IsItemHovered()) ImGui.SetTooltip(member.Name);
                    if(ImGui.IsItemClicked()) open=true;
                }
                ImGui.PopID();
                if(open){OpenReplay(row.File);break;}
            }
        }
        finally {ImGui.EndTable();}
    }
}
