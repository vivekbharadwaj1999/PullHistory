using System;
using System.Collections.Generic;
using System.Linq;
using BossMod;
using Dalamud.Bindings.ImGui;

namespace PullHistory;
public sealed partial class Plugin
{
    private readonly record struct OutputTotals(long Damage, long Healing);
    private Dictionary<ulong,(DateTime,OutputTotals)[]> outputHistory = new();
    private const string OutputTip = "Recorded action damage and healing per second since pull start. Includes owned pets and overhealing. Shields and unattributed periodic ticks are excluded.";

    private void LoadOutput(Replay replay)
    {
        var series = new Dictionary<ulong,List<(DateTime,OutputTotals)>>();
        var partyChanges = replay.Ops.OfType<PartyState.OpModify>().Where(p => p.Slot >= 0 && p.Slot < 8).ToArray();
        var party = new ulong[8];
        int change = 0;
        foreach (var action in replay.Actions)
        {
            while (change < partyChanges.Length && partyChanges[change].Timestamp <= action.Timestamp)
            {
                var update = partyChanges[change++];
                party[update.Slot] = update.Member.InstanceId;
            }
            foreach (var target in action.Targets)
                foreach (var effect in target.Effects.AsSpan())
                {
                    bool heal = effect.Type == ActionEffectType.Heal;
                    if (!heal && effect.Type is not (ActionEffectType.Damage or ActionEffectType.BlockedDamage or ActionEffectType.ParriedDamage)) continue;
                    var source = effect.FromTarget ? target.Target : action.Source;
                    var recipient = effect.AtSource ? action.Source : target.Target;
                    if (source == null || recipient == null) continue;
                    if (!heal && (recipient.Type == ActorType.Player || recipient.AllyAt(action.Timestamp))) continue;
                    var owner = source.Type == ActorType.Player ? source : replay.FindParticipant(source.OwnerID, action.Timestamp);
                    if (owner?.Type != ActorType.Player) continue;
                    int amount = effect.DamageHealValue;
                    Add(owner.InstanceID, action.Timestamp, heal, amount);
                    if (Array.IndexOf(party, owner.InstanceID) >= 0) Add(0, action.Timestamp, heal, amount);
                }
        }
        outputHistory = series.ToDictionary(p => p.Key, p => p.Value.ToArray());

        void Add(ulong actor, DateTime time, bool heal, int amount)
        {
            if (!series.TryGetValue(actor, out var samples)) series[actor] = samples = new();
            var previous = samples.Count == 0 ? default : samples[^1].Item2;
            var next = new OutputTotals(previous.Damage + (heal ? 0 : amount), previous.Healing + (heal ? amount : 0));
            if (samples.Count > 0 && samples[^1].Item1 == time) samples[^1] = (time,next);
            else samples.Add((time,next));
        }
    }

    private (double DPS,double HPS) OutputAt(ulong id, DateTime at)
    {
        double seconds = (at-first).TotalSeconds;
        if (seconds <= 0 || !outputHistory.TryGetValue(id,out var samples)) return default;
        int i = At(samples,at);
        return i < 0 ? default : (samples[i].Item2.Damage/seconds,samples[i].Item2.Healing/seconds);
    }

    private void DrawOutput(ulong id, DateTime at, bool stacked)
    {
        var (damage,healing) = OutputAt(id,at);
        if (stacked)
        {
            ImGui.BeginGroup();
            ImGui.TextUnformatted($"DPS {damage:N0}");
            ImGui.TextUnformatted($"HPS {healing:N0}");
            ImGui.EndGroup();
        }
        else ImGui.TextUnformatted($"DPS {damage:N0}   HPS {healing:N0}");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(OutputTip);
    }
}
