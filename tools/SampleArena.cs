using System;
using System.Security.Cryptography;
using System.Text;
using BossMod;
using BossMod.Components;

public static class ArenaSample
{
    public static string Digest(Replay replay)
    {
        var player=new ReplayPlayer(replay);
        using var manager=new BossModuleManager(player.WorldState);
        var encounter=replay.Encounters[0];
        var text=new StringBuilder();
        int shapes=0;
        for(var time=encounter.Time.Start.AddSeconds(1);time<encounter.Time.End;time=time.AddSeconds(2))
        {
            player.AdvanceTo(time,manager.Update);
            var module=manager.ActiveModule;
            if(module==null || player.WorldState.Party[0] is not Actor actor) continue;
            text.Append(module.GetType().Name).Append(module.Arena.Bounds.GetType().Name);
            for(var i=Waymark.A;i<Waymark.Count;i++)
                text.Append(i).Append(player.WorldState.Waymarks[i]);
            for(var i=Sign.Attack1;i<Sign.Count;i++)
                text.Append(i).Append(player.WorldState.Waymarks[i]);
            foreach(var hint in module.CalculateGlobalHints(actor)) text.Append(hint);
            foreach(var hint in module.CalculateHintsForRaidMember(0,actor)) text.Append(hint);
            foreach(var component in module.Components)
            {
                text.Append(component.GetType().Name);
                if(component is not GenericAOEs aoes) continue;
                foreach(var aoe in aoes.ActiveAOEs(0,actor))
                {
                    shapes++;
                    text.Append(aoe.Shape.GetType().Name).Append(aoe.Origin).Append(aoe.Rotation.Rad)
                        .Append(aoe.Activation.Ticks).Append(aoe.Color).Append(aoe.Risky);
                    for(int x=-20;x<=20;x+=4)
                        for(int z=-20;z<=20;z+=4)
                            text.Append(aoe.Check(module.Arena.Center+new WDir(x,z))?'1':'0');
                }
            }
        }
        if(shapes==0) throw new Exception("No telegraphs sampled");
        return shapes+" "+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
