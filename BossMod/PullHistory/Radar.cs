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
    private sealed class ReplayRadar : IDisposable
    {
        private readonly dynamic player, config;
        private readonly Type managerType;
        private readonly object north, origin, east;
        private dynamic manager;
        private BossMod.ZoneModuleManager zones;
        private Action update;
        private DateTime time;
        public float PixelsPerYalm { get; private set; }
        public dynamic World => player.WorldState;
        public dynamic Module => manager.ActiveModule;
        public ReplayRadar(object replay)
        {


            var a=replay.GetType().Assembly;
            player=Activator.CreateInstance(a.GetType("BossMod.ReplayPlayer",true),replay);
            managerType=a.GetType("BossMod.BossModuleManager",true);
            north=Activator.CreateInstance(a.GetType("BossMod.Angle",true));
            config=a.GetType("BossMod.MiniArena",true).GetField("Config").GetValue(null);
            var position=a.GetType("BossMod.WPos",true);
            origin=Activator.CreateInstance(position,0f,0f);
            east=Activator.CreateInstance(position,1f,0f);
            BindManager();
        }
        private void BindManager()
        {
            zones=new BossMod.ZoneModuleManager((BossMod.WorldState)World);
            manager=Activator.CreateInstance(managerType,(object)World);
            update=(Action)managerType.GetMethod("Update").CreateDelegate(typeof(Action),(object)manager);
        }
        public void Seek(DateTime next)
        {
            if(next==time) return;
            if(next<time) { Dispose(); player.Reset(); BindManager(); }
            player.AdvanceTo(next,update);
            time=next;
        }
        public void DrawZone()
        {
            var zone=zones.ActiveModule;
            if(zone?.WantDrawHints()==true) zone.DrawGlobalHints();
            if(zone?.WantDrawExtra()==true) zone.DrawExtra();
        }
        public void Draw(dynamic module,int pov)
        {
            module.Draw((dynamic)north,pov,true,false);
            var available=ImGui.GetContentRegionAvail();
            float side=Math.Max(1,Math.Min(available.X,available.Y)-4);
            ImGui.SetCursorPos(ImGui.GetCursorPos()+Vector2.Max(Vector2.Zero,(available-new Vector2(side))/2));
            float scale=config.ArenaScale, slack=config.SlackForRotations;
            bool resize=config.RadarResize;
            try
            {
                config.RadarResize=false;
                config.SlackForRotations=1f;
                config.ArenaScale=side/340f;
                module.Draw((dynamic)north,pov,false,true);
                Vector2 a=module.Arena.WorldPositionToScreenPosition((dynamic)origin);
                Vector2 b=module.Arena.WorldPositionToScreenPosition((dynamic)east);
                PixelsPerYalm=Vector2.Distance(a,b);
            }
            finally { config.ArenaScale=scale; config.SlackForRotations=slack; config.RadarResize=resize; }
        }
        public void Dispose() { zones.Dispose(); manager.Dispose(); }
    }
    private ReplayRadar radar;
    private float? dragSeek;
    private ulong selectedActor;
    private readonly Dictionary<uint,(string Name,uint Icon,string Description)> statusInfo=new();
    private readonly Dictionary<uint,string> actionNames=new();
    private void ReleaseRadar() { radar?.Dispose(); radar=null; dragSeek=null; selectedActor=0; }
    private static bool IsActiveEnemy(dynamic actor)=>actor.Type.ToString()=="Enemy" && !actor.IsAlly && !actor.IsDead && !actor.IsDestroyed && actor.IsTargetable && actor.InCombat;
    private void DrawEncounter(DateTime at)
    {
        radar.Seek(at);
        dynamic world=radar.World, module=radar.Module;
        ulong boss=module==null ? 0ul : (ulong)module.PrimaryActor.InstanceID;
        var players=new List<object>();
        var enemies=new List<object>();
        foreach(dynamic actor in (IEnumerable)world.Actors)
        {
            if(actor.IsDestroyed) continue;
            if(actor.Type.ToString()=="Player") players.Add((object)actor);
            else if(IsActiveEnemy((object)actor)) enemies.Add((object)actor);
        }
        float height=Math.Max(180,ImGui.GetContentRegionAvail().Y-110);
        if(!ImGui.BeginTable("Replay panels compact",3,ImGuiTableFlags.Resizable|ImGuiTableFlags.BordersInnerV)) return;
        try
        {
            ImGui.TableSetupColumn("Radar",ImGuiTableColumnFlags.WidthStretch,1.42f);
            ImGui.TableSetupColumn("Players",ImGuiTableColumnFlags.WidthStretch,1f);
            ImGui.TableSetupColumn("Enemies",ImGuiTableColumnFlags.WidthStretch,1f);
            ImGui.TableNextRow(); ImGui.TableNextColumn(); DrawPanelHeading("Arena");
            bool child=ImGui.BeginChild("Encounter radar",new Vector2(0,height),false,ImGuiWindowFlags.NoScrollbar);
            try
            {
                if(child)
                {
                    radar.DrawZone();
                    int pov=0;
                    while(pov<8 && world.Party[pov]==null) pov++;
                    if(module!=null && pov<8)
                    {
                        radar.Draw(module,pov);
                        foreach(dynamic actor in enemies) DrawMarker(actor,module.Arena.WorldPositionToScreenPosition(actor.Position),false);
                        DrawPlayers(module,players,at);
                        DrawSelection(module,players,enemies,at);
                    }
                    else ImGui.TextWrapped("No BMR radar at this point in the pull.");
                }
            }
            finally { ImGui.EndChild(); }
            ImGui.TableNextColumn(); DrawActors("Players",players,at,height,0);
            ImGui.TableNextColumn(); DrawActors("Enemies",enemies.OrderByDescending(a=>(ulong)((dynamic)a).InstanceID==boss),at,height,boss);
        }
        finally { ImGui.EndTable(); }
    }
    private Vector2 MarkerCenter(dynamic module,dynamic actor,DateTime at)
    {
        if ((bool)actor.IsDead)
        {
            ulong id=actor.InstanceID;
            foreach(var track in tracks)
            {
                if(track.InstanceId!=id) continue;
                int death=At(track.Deaths,at);
                if(death<0 || !track.Deaths[death].Item2) continue;
                int sample=At(track.Positions,track.Deaths[death].Item1);
                if(sample<0) continue;
                var pos=track.Positions[sample].Item2;
                dynamic point=Activator.CreateInstance(((object)actor.Position).GetType(),pos.X,pos.Z);
                return module.Arena.WorldPositionToScreenPosition(point);
            }
        }
        return module.Arena.WorldPositionToScreenPosition(actor.Position);
    }

    private const float JobIconHalfSize=12.5f;

    private void DrawPlayers(dynamic module,List<object> players,DateTime at)
    {
        var mouse=ImGui.GetMousePos();
        bool hovered=ImGui.IsWindowHovered();
        float nearest=float.PositiveInfinity;
        ulong candidate=0;
        string hoverText=null;
        var deathsOnMap=new List<Vector2>();
        foreach(dynamic actor in players)
        {
            Vector2 xy=MarkerCenter(module,actor,at);
            if(actor.IsDead) deathsOnMap.Add(xy);
            DrawMarker(actor,xy,true);
            var delta=mouse-xy;
            float distance=delta.LengthSquared();
            if(hovered && Math.Abs(delta.X)<=JobIconHalfSize && Math.Abs(delta.Y)<=JobIconHalfSize && distance<=nearest)
            {
                nearest=distance;
                candidate=actor.InstanceID;
                hoverText=$"{(string)actor.Name} · {(float)actor.HitboxRadius:0.##} yalms";
            }
        }
        var draw=ImGui.GetWindowDrawList();
        foreach(var xy in deathsOnMap)
        {
            var diagonal=new Vector2(6,6);
            var opposite=new Vector2(6,-6);
            draw.AddLine(xy-diagonal,xy+diagonal,0xff181122,4);
            draw.AddLine(xy-opposite,xy+opposite,0xff181122,4);
            draw.AddLine(xy-diagonal,xy+diagonal,0xff4343ff,2);
            draw.AddLine(xy-opposite,xy+opposite,0xff4343ff,2);
        }
        if(candidate!=0)
        {
            ImGui.SetTooltip(hoverText);
            if(ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                selectedActor=selectedActor==candidate ? 0 : candidate;
        }
    }

    private void DrawSelection(dynamic module,List<object> players,List<object> enemies,DateTime at)
    {
        if(selectedActor==0) return;
        dynamic selected=radar.World.Actors.Find(selectedActor);
        if(selected==null || (!players.Contains((object)selected) && !enemies.Contains((object)selected))) return;
        float radius=(float)selected.HitboxRadius*radar.PixelsPerYalm;
        if(!float.IsFinite(radius) || radius<=0) return;
        Vector2 center=MarkerCenter(module,selected,at);
        // Draw last so small player hitboxes remain visible even inside stacked icons.
        var draw=ImGui.GetWindowDrawList();
        if(selected.Type.ToString()=="Enemy")
        {
            DrawEnemyRing(center,radius,(float)selected.PosRot.W,(bool)selected.Omnidirectional,true);
            return;
        }
        draw.AddCircle(center,radius,0xff38261a,96,4f);
        draw.AddCircle(center,radius,0xffd5ff46,96,2f);
    }

    private static void DrawEnemyRing(Vector2 center,float radius,float rotation,bool omni,bool selected)
    {
        var draw=ImGui.GetWindowDrawList();
        uint rim=selected ? 0xffd5ff46u : 0xff6582eeu;
        uint innerColor=selected ? 0xff9dc735u : 0xff5275c7u;
        float inner=radius-Math.Min(5f,radius*.22f);
        if(omni)
        {
            draw.AddCircle(center,radius,0xdb211821,96,3.8f);
            draw.AddCircle(center,radius,rim,96,2f);
            draw.AddCircle(center,inner,innerColor,96,1f);
            return;
        }
        // Zero game rotation faces south on the north-up radar.
        float heading=MathF.PI/2-rotation;
        float quarter=MathF.PI/4;
        void Arc(float r,float start,float end,uint color,float thickness)
        {
            draw.PathArcTo(center,r,heading+start,heading+end);
            draw.PathStroke(color,ImDrawFlags.None,thickness);
        }
        Arc(radius,-3*quarter,3*quarter,0xdb211821,3.8f);
        Arc(radius,-3*quarter,3*quarter,rim,2f);
        draw.AddCircle(center,inner,innerColor,96,1f);
        for(int i=-3;i<=3;i+=2)
        {
            float angle=heading+i*quarter;
            var ray=new Vector2(MathF.Cos(angle),MathF.Sin(angle));
            draw.AddLine(center+ray*inner,center+ray*radius,rim,1.5f);
        }
    }

    private void DrawMarker(dynamic actor,Vector2 xy,bool player)
    {
        var draw=ImGui.GetWindowDrawList();
        bool dead=actor.IsDead;
        uint tint=dead ? 0xff888888u : 0xffffffffu;
        uint job=Convert.ToUInt32((object)actor.Class);
        var icon=player && job>0 ? textures.GetFromGameIcon(62100u+job).GetWrapOrDefault() : null;
        float radius=icon==null ? 5 : JobIconHalfSize;
        if(icon!=null) draw.AddImage(icon.Handle,xy-new Vector2(radius),xy+new Vector2(radius),Vector2.Zero,Vector2.One,tint);
        if(!player)
        {
            float hit=(float)actor.HitboxRadius*radar.PixelsPerYalm;
            if(float.IsFinite(hit) && hit>0)
            {
                radius=hit;
                DrawEnemyRing(xy,hit,(float)actor.PosRot.W,(bool)actor.Omnidirectional,false);
            }
        }
        Vector4 pos=actor.PosRot;
        var forward=new Vector2(MathF.Sin(pos.W),MathF.Cos(pos.W));
        var across=new Vector2(-forward.Y,forward.X)*3.5f;
        var edge=xy+forward*radius;
        draw.AddTriangleFilled(edge+forward*7,edge-across,edge+across,dead ? tint : player ? 0xfff0e2b0u : 0xff829dffu);
        if(!player && ImGui.IsWindowHovered() && Vector2.DistanceSquared(ImGui.GetMousePos(),xy)<100) ImGui.SetTooltip((string)actor.Name);
    }
    private static void DrawPanelHeading(string title,int? count=null)
    {
        ImGui.TextUnformatted(title);
        if(count.HasValue)
        {
            ImGui.SameLine(0,5);
            ImGui.TextDisabled(count.Value.ToString());
        }
        ImGui.Separator();
    }

    private void DrawActors(string title,IEnumerable<object> actors,DateTime at,float height,ulong boss)
    {
        var rows=actors as List<object> ?? actors.ToList();
        DrawPanelHeading(title,rows.Count);
        if(!ImGui.BeginTable(title+" compact",1,ImGuiTableFlags.ScrollY,new Vector2(0,height))) return;
        try
        {
            foreach(dynamic actor in rows)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                float width=ImGui.GetContentRegionAvail().X;
                if(ImGui.TableGetRowIndex()>0)
                {
                    float gap=Math.Max(4,MathF.Round(ImGui.GetTextLineHeight()*.32f));
                    var line=ImGui.GetCursorScreenPos()+new Vector2(0,gap*.5f);
                    ImGui.GetWindowDrawList().AddLine(line,line+new Vector2(width,0),0x38ffffff,1);
                    ImGui.Dummy(new Vector2(0,gap));
                }
                var origin=ImGui.GetCursorScreenPos();
                float nameWidth=width*.4f;
                bool player=actor.Type.ToString()=="Player";
                dynamic h=actor.HPMP;
                bool mp=player || ((uint)h.MaxMP>0 && (uint)h.CurMP<(uint)h.MaxMP);
                bool shield=player || (uint)h.Shield>0;
                int bars=1+(mp?1:0)+(shield?1:0);
                float w=Math.Max(4,(width-nameWidth-2*bars)/bars);
                nameWidth=Math.Max(0,nameWidth-w*.1f);
                ImGui.PushClipRect(origin,origin+new Vector2(nameWidth,ImGui.GetTextLineHeight()+2),true);
                try { ImGui.TextUnformatted((string)actor.Name); } finally { ImGui.PopClipRect(); }

                if(ImGui.IsItemHovered())
                {
                    dynamic target=radar.World.Actors.Find((ulong)actor.TargetID);
                    string role=player ? $"{actor.Class} {actor.Level}" : (ulong)actor.InstanceID==boss ? "Boss" : "Enemy";
                    ImGui.SetTooltip($"{(string)actor.Name} · {role}{((bool)actor.IsDead ? " · KO" : "")}\nTarget: {(target==null ? "—" : (string)target.Name)}");
                }
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(origin+new Vector2(nameWidth+2,0));
                Bar("HP",(uint)h.CurHP,(uint)h.MaxHP,player ? 0xff599644u : 0xff4845b8u,w*1.1f);
                if(mp){ImGui.SameLine(0,2); Bar("MP",(uint)h.CurMP,(uint)h.MaxMP,0xff8554c5,w);}
                if(shield){ImGui.SameLine(0,2); Bar("Shield",(uint)h.Shield,(uint)h.MaxHP,0xff285ac8,w);}
                if(playbackSettings.ShowRotationsAndCastBars && !player)
                {
                    if(actor.CastInfo!=null && actor.CastInfo.TotalTime>0 && !actor.CastInfo.EventHappened) DrawCast(actor.CastInfo,width);
                    else DrawBar(0,width,0xff345f94,"","No active cast");
                }
                DrawStatuses((Array)actor.Statuses,at);
                if(playbackSettings.ShowRotationsAndCastBars && player) DrawRotation((ulong)actor.InstanceID,at);
                ulong id=actor.InstanceID;
                var end=new Vector2(origin.X+width,ImGui.GetItemRectMax().Y);
                bool rowHovered=ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin,end);
                if(rowHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    selectedActor=selectedActor==id ? 0 : id;
                if(selectedActor==id) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,0x88554c24);
                else if(rowHovered) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0,0x12ffffff);
            }
        }
        finally { ImGui.EndTable(); }
    }
    private static void DrawBar(float fraction,float width,uint color,string caption,string tip,bool fitText=false)
    {
        var pos=ImGui.GetCursorScreenPos();
        var size=new Vector2(Math.Max(1,width),ImGui.GetTextLineHeight());
        ImGui.Dummy(size);
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos,pos+size,0xff363030,2);
        if(fraction>0) draw.AddRectFilled(pos,pos+new Vector2(size.X*Math.Clamp(fraction,0,1),size.Y),color,2);
        draw.PushClipRect(pos,pos+size,true);
        var measured=ImGui.CalcTextSize(caption);
        float scale=fitText ? Math.Min(1,Math.Max(1,size.X-2)/Math.Max(1,measured.X)) : 1;
        draw.AddText(ImGui.GetFont(),ImGui.GetFontSize()*scale,pos+Vector2.Max(Vector2.Zero,(size-measured*scale)/2),0xffffffff,caption);
        draw.PopClipRect();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }
    private static void Bar(string label,uint value,uint max,uint color,float width)
    {
        float fraction=max==0 ? 0 : (float)value/max;
        bool isShield=label=="Shield";
        string amount=isShield ? (max==0 ? "�" : $"{fraction*100:F0}%") : $"{value:N0}";
        string text=(isShield ? "SH" : label)+" "+amount;
        string caption=ImGui.CalcTextSize(text).X+2<=width ? text : amount;
        if(!isShield && ImGui.CalcTextSize(caption).X+2>width) caption=value.ToString();
        DrawBar(fraction,width,color,caption,$"{label}  {value:N0} / {max:N0}  ({fraction*100:F1}%)",!isShield);
    }
    private void DrawCast(dynamic cast,float width)
    {
        float total=cast.TotalTime;
        if(total<=0 || cast.EventHappened) return;
        float elapsed=Math.Clamp((float)cast.ElapsedTime,0,total);
        uint id=cast.Action.Raw;
        if(!actionNames.TryGetValue(id,out string name)) actionNames[id]=name=cast.Action.Name();
        string caption=$"{name}   {total-elapsed:F1}s";
        DrawBar(elapsed/total,width,0xff345f94,caption,$"{name}  {elapsed:F1} / {total:F1}s");
    }
    private static string TimerLabel(double seconds)
    {
        if(seconds<=0 || seconds>=86400) return "";
        if(seconds>=3600) return $"{Math.Ceiling(seconds/3600)}h";
        if(seconds>=60) return $"{Math.Ceiling(seconds/60)}m";
        return $"{Math.Ceiling(seconds)}";
    }
    private void DrawStatuses(Array statuses,DateTime at)
    {
        int active=0;
        foreach(dynamic entry in statuses) if((uint)entry.ID!=0) active++;
        if(active==0) return;
        float available=ImGui.GetContentRegionAvail().X;
        float oldHeight=Math.Max(1,Math.Min(ImGui.GetTextLineHeight()*1.2f,(available-active+1)/active));
        // Native status art is 24 by 32. The old square cells added side padding to the visible gap.
        const float aspect=24f/32f;
        float gap=(oldHeight*(1-aspect)+1)*.5f;
        float cellWidth=Math.Max(1,Math.Min(oldHeight*1.2f*aspect,(available-gap*(active-1))/active));
        float height=cellWidth/aspect;
        float startX=ImGui.GetCursorScreenPos().X;
        int count=0;
        foreach(dynamic status in statuses)
        {
            uint id=status.ID;
            if(id==0) continue;
            if(!statusInfo.TryGetValue(id,out var info))
            {
                var row=data.GetExcelSheet<Lumina.Excel.Sheets.Status>().GetRowOrDefault(id);
                statusInfo[id]=info=(row?.Name.ExtractText() ?? $"Status {id}",row?.Icon ?? 0u,row?.Description.ExtractText() ?? "");
            }
            if(count>0) ImGui.SameLine(0,0);
            ImGui.SetCursorScreenPos(new Vector2(startX+count*(cellWidth+gap),ImGui.GetCursorScreenPos().Y));
            count++;
            var icon=info.Icon==0 ? null : textures.GetFromGameIcon(info.Icon).GetWrapOrDefault();
            double remaining=((DateTime)status.ExpireAt-at).TotalSeconds;
            string label=TimerLabel(remaining);
            var position=ImGui.GetCursorScreenPos();
            float fontSize=ImGui.GetFontSize()*.81f;
            ImGui.Dummy(new Vector2(cellWidth,height+fontSize));
            var draw=ImGui.GetWindowDrawList();
            if(icon!=null && icon.Width>0 && icon.Height>0)
            {
                var dimensions=new Vector2(icon.Width,icon.Height);
                var fitted=dimensions*Math.Min(cellWidth/icon.Width,height/icon.Height);
                var corner=position+(new Vector2(cellWidth,height)-fitted)*.5f;
                draw.AddImage(icon.Handle,corner,corner+fitted);
            }
            else draw.AddRectFilled(position,position+new Vector2(cellWidth,height),0xff776960,2);
            if(label.Length>0)
            {
                float naturalWidth=ImGui.CalcTextSize(label).X;
                float scale=Math.Min(fontSize/ImGui.GetFontSize(),cellWidth/Math.Max(1,naturalWidth));
                var textPos=position+new Vector2((cellWidth-naturalWidth*scale)*.5f,height);
                draw.AddText(ImGui.GetFont(),ImGui.GetFontSize()*scale,textPos+Vector2.One,0xff141414,label);
                draw.AddText(ImGui.GetFont(),ImGui.GetFontSize()*scale,textPos,0xffffffff,label);
            }
            if(ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize()*28);
                ImGui.TextUnformatted(remaining>0 && remaining<86400 ? $"{info.Name} ({remaining:F1}s)" : info.Name);
                if(info.Description.Length>0) ImGui.TextUnformatted(info.Description);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
    }
}
