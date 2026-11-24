#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PullHistory;
public sealed partial class Plugin
{
    private sealed record Skill(uint Id, bool Item, DateTime End, bool Cast);
    private Dictionary<ulong,(DateTime,Skill)[]> rotations;
    private readonly Dictionary<(uint,bool),(string Name,uint Icon)> skillInfo=new();

    private void LoadRotations(object replay)
    {
        var timelines=new Dictionary<ulong,List<(DateTime,Skill)>>();
        var castWindows=new Dictionary<(ulong,uint),List<(DateTime Start,DateTime End)>>();
        foreach(dynamic participant in (IEnumerable)Read(replay,"Participants"))
        {
            if(participant.Type.ToString()!="Player") continue;
            ulong player=participant.InstanceID;
            if(!timelines.TryGetValue(player,out var events)) timelines[player]=events=new();
            foreach(dynamic cast in participant.Casts)
            {
                if(cast.ID.Type.ToString()!="Spell") continue;
                uint id=cast.ID.ID;
                DateTime start=cast.Time.Start, end=cast.Time.End;
                if(end<start || end>last) end=last;
                events.Add((start,new Skill(id,false,end,true)));
                if(!castWindows.TryGetValue((player,id),out var windows)) castWindows[(player,id)]=windows=new();
                windows.Add((start,end));
            }
        }
        foreach(dynamic action in (IEnumerable)Read(replay,"Actions"))
        {
            if(action.Source==null) continue;
            if(!timelines.TryGetValue((ulong)action.Source.InstanceID,out var events)) continue;
            string type=action.ID.Type.ToString();
            uint id=action.ID.ID;
            bool item=type=="Item";
            if(!item && (type!="Spell" || id==7 || id==8)) continue;
            DateTime time=action.Timestamp;
            // A cast can resolve just before the separate cast-end notification.
            if(!item && castWindows.TryGetValue(((ulong)action.Source.InstanceID,id),out var windows))
            {
                int match=windows.FindIndex(w=>w.Start<=time && (time-w.End).TotalSeconds<=0.35);
                if(match>=0){windows.RemoveAt(match); continue;}
            }
            float animationLock=action.AnimationLock;
            double hold=float.IsFinite(animationLock) && animationLock>0 ? animationLock : .35;
            events.Add((time,new Skill(id,item,time.AddSeconds(hold),false)));
        }
        rotations=timelines.ToDictionary(pair=>pair.Key,pair=>pair.Value.OrderBy(e=>e.Item1).ToArray());
    }

    private (string Name,uint Icon) SkillInfo(Skill skill)
    {
        var key=(skill.Id,skill.Item);
        if(skillInfo.TryGetValue(key,out var info)) return info;
        if(skill.Item)
        {
            var row=data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(skill.Id%1000000);
            info=(row?.Name.ExtractText() ?? $"Item {skill.Id}",row?.Icon ?? 0u);
        }
        else
        {
            var row=data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(skill.Id);
            info=(row?.Name.ExtractText() ?? $"Action {skill.Id}",row?.Icon ?? 0u);
        }
        return skillInfo[key]=info;
    }

    private static int CurrentSkill((DateTime,Skill)[] sequence,DateTime at)
    {
        int latest=At(sequence,at);
        // Keep the cast centered even when another action event arrives during its window.
        for(int i=latest;i>=0;i--)
        {
            if(!sequence[i].Item2.Cast) continue;
            if(at<sequence[i].Item2.End) return i;
            break;
        }
        return latest;
    }

    private void DrawRotation(ulong player,DateTime at)
    {
        if(!rotations.TryGetValue(player,out var sequence)) sequence=Array.Empty<(DateTime,Skill)>();
        int latest=CurrentSkill(sequence,at);
        bool active=latest>=0 && at<sequence[latest].Item2.End;
        float width=ImGui.GetContentRegionAvail().X;
        const float currentScale=1.10f;
        float size=Math.Max(1,Math.Min(ImGui.GetTextLineHeight()*1.68f,(width*.72f-16)/(8+currentScale)));
        float rowHeight=size*currentScale;
        var draw=ImGui.GetWindowDrawList();
        for(int slot=-4;slot<=4;slot++)
        {
            if(slot!=-4) ImGui.SameLine(0,2);
            float cellWidth=slot==0 ? rowHeight : size;
            float iconSize=slot==0 && active ? rowHeight : size;
            var cell=new Vector2(cellWidth,rowHeight);
            var pos=ImGui.GetCursorScreenPos()+(cell-new Vector2(iconSize))*.5f;
            var end=pos+new Vector2(iconSize);
            ImGui.Dummy(cell);
            draw.AddRectFilled(pos,end,0xb0302929,2);
            int index=latest+slot+(!active && slot<0 ? 1 : 0);
            if((slot!=0 || active) && index>=0 && index<sequence.Length)
            {
                var (start,skill)=sequence[index];
                var info=SkillInfo(skill);
                var texture=info.Icon==0 ? null : textures.GetFromGameIcon(info.Icon).GetWrapOrDefault();
                if(texture!=null) draw.AddImage(texture.Handle,pos,end,Vector2.Zero,Vector2.One,slot>0 ? 0xb0ffffffu : 0xffffffffu);
                else draw.AddText(pos,0xffffffff,"?");
                if(ImGui.IsItemHovered())
                {
                    string timing=skill.Cast ? $"Recorded cast: {(skill.End-start).TotalSeconds:0.00}s" : "Instant cast: 0s";
                    string next=index+1<sequence.Length ? $"\nNext action after {(sequence[index+1].Item1-start).TotalSeconds:0.00}s" : "";
                    ImGui.SetTooltip($"{info.Name}\nPull time: {(start-first).TotalSeconds:0.00}s\n{timing}{next}");
                }
            }
            if(slot==0) draw.AddRect(pos,end,0xff55dfff,2,ImDrawFlags.None,2);
        }
        ImGui.SameLine(0,4);
        float barWidth=Math.Max(1,ImGui.GetContentRegionAvail().X);
        if(active && sequence[latest].Item2.Cast)
        {
            var (start,skill)=sequence[latest];
            float total=(float)(skill.End-start).TotalSeconds;
            float elapsed=(float)(at-start).TotalSeconds;
            string name=SkillInfo(skill).Name;
            DrawBar(elapsed/total,barWidth,0xff345f94,$"{name}  {total-elapsed:F1}s",$"{name}  {elapsed:F1} / {total:F1}s",true);
        }
        else DrawBar(0,barWidth,0xff345f94,"","No active cast");
    }
}
