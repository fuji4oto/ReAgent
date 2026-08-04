using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReAgent.State;

[Api]
public record FlaskInfo(
    [property: Api] bool Active,
    [property: Api] bool CanBeUsed,
    [property: Api] int Charges,
    [property: Api] int MaxCharges,
    [property: Api] int ChargesPerUse,
    [property: Api] int LifeRecovery,
    [property: Api] int ManaRecovery,
    [property: Api] string ClassName,
    [property: Api] string BaseName,
    [property: Api] string UniqueName,
    [property: Api] float CanBeUsedIn,
    Lazy<List<ItemMod>> ModListSource)
{
    [Api]
    public string Name => !string.IsNullOrEmpty(UniqueName) ? UniqueName : BaseName;

    [property: Api]
    public List<ItemMod> Mods => ModListSource.Value;

    public static FlaskInfo From(
        GameController state,
        List<ServerInventory.InventSlotItem> flaskItems,
        ServerInventory.InventSlotItem flaskItem,
        int index,
        RuleInternalState internalState)
    {
        if (flaskItem?.Address is 0 or null || flaskItem.Item?.Address is null or 0)
        {
            return new FlaskInfo(false, false, 0, 1, 1, 0, 0, "", "", "", 100, new Lazy<List<ItemMod>>([]));
        }

        var active = false;
        var canBeUsedIn = 0f;
        bool canbeUsed = false;
        int chargesUsed = 0;
        int lifeRecovery = 0;
        int manaRecovery = 0; 
        var chargeComponent = flaskItem.Item.GetComponent<Charges>();
        if (state.Player.TryGetComponent<Buffs>(out var playerBuffs))
        {
            if (flaskItem.Item.TryGetComponent<Flask>(out var flask))
            {
                var buffNames = GetFlaskBuffNames(flask);
                active = playerBuffs.BuffsList.Any(b => buffNames.Contains(b.Name) && b.FlaskSlot == index);
                chargesUsed = chargeComponent?.ChargesPerUse ?? 1;
                var flaskStatsC = flaskItem.Item?.GetComponent<LocalStats>();
                if (flaskStatsC?.StatDictionary != null && flaskStatsC.StatDictionary.TryGetValue(GameStat.LocalChargesUsedPct, out var chargesUsedPct)) {
                    chargesUsed = chargesUsed * (100 + chargesUsedPct) / 100;
                }
                canbeUsed = (chargeComponent?.NumCharges ?? 0) >= chargesUsed;
                lifeRecovery = flask?.LifeRecover ?? 0;
                manaRecovery = flask?.ManaRecover ?? 0;
            }

            if (flaskItem.Item.TryGetComponent<Tincture>(out var tincture))
            {
                var buffDisplayName = tincture.TinctureDat.BaseItemType.BaseName;
                if (playerBuffs.BuffsList.FirstOrDefault(x =>
                            x.DisplayName == buffDisplayName &&
                            float.IsInfinity(x.MaxTime) //old instances of the buff sometimes stick around if you spam the tinctures, this is the only way to tell them apart
                    ) is { } buff &&
                    (internalState.TinctureUsageTracker.GetValueOrDefault(index).WasActive ||
                     playerBuffs.BuffsList.Any(x =>
                         x.Name == "tincture_parent_buff" &&
                         x.FlaskSlot == index
                     )))
                {
                    active = true;
                    if (float.IsPositiveInfinity(buff.Timer))
                    {
                        internalState.TinctureUsageTracker[index] = (true, DateTime.UnixEpoch);
                        canBeUsedIn = float.PositiveInfinity;
                    }
                    else
                    {
                        CheckDeactivation(index, internalState);
                        canBeUsedIn = CalculateTinctureCanBeUsedIn(state, flaskItems, internalState);
                    }
                }
                else
                {
                    CheckDeactivation(index, internalState);
                    canBeUsedIn = CalculateTinctureCanBeUsedIn(state, flaskItems, internalState);
                }

                canbeUsed = canBeUsedIn <= 0;
            }
        }

        var className = "";
        var baseName = "";
        if (flaskItem.Item.TryGetComponent<Base>(out var baseC))
        {
            className = baseC.Info?.BaseItemTypeDat?.ClassName ?? "";
            baseName = baseC.Name;
        }

        var uniqueName = "";
        Lazy<List<ItemMod>> modList = null;
        if (flaskItem.Item.TryGetComponent<Mods>(out var modsC))
        {
            uniqueName = modsC.UniqueName;
            modList = new Lazy<List<ItemMod>>(() => modsC.ExplicitMods, LazyThreadSafetyMode.None);
        }

        return new FlaskInfo(active, canbeUsed, chargeComponent?.NumCharges ?? 0, chargeComponent?.ChargesMax ?? 1,
            chargesUsed, lifeRecovery, manaRecovery, className, baseName, uniqueName, canBeUsedIn, modList ?? new Lazy<List<ItemMod>>([]));
    }

    private static float CalculateTinctureCanBeUsedIn(GameController state, List<ServerInventory.InventSlotItem> flaskItems, RuleInternalState internalState)
    {
        return flaskItems.Select((x, i) => CalculateTinctureCanBeUsedIn(state, x, i, internalState)).DefaultIfEmpty(0).Max();
    }

    private static float CalculateTinctureCanBeUsedIn(GameController state, ServerInventory.InventSlotItem flaskItem, int index, RuleInternalState internalState)
    {
        if (flaskItem == null || !flaskItem.Item.TryGetComponent<Tincture>(out var tincture))
        {
            return 0;
        }

        var cdrMultiplier = (100f +
                             (state.Player.GetComponent<Stats>()?.StatDictionary.GetValueOrDefault(GameStat.TinctureCooldownRecoveryPct) ?? 0) +
                             (flaskItem.Item.GetComponent<LocalStats>()?.StatDictionary.GetValueOrDefault(GameStat.LocalTinctureCooldownRecoveryPct) ?? 0)) / 100;
        var tinctureCooldown = tincture.TinctureDat.Cooldown / 1000f / cdrMultiplier;
        var sinceLastActivation = (float)(DateTime.UtcNow - internalState.TinctureUsageTracker.GetValueOrDefault(index).DeactivationTime).TotalSeconds;
        var remainingTime = tinctureCooldown - sinceLastActivation;
        return Math.Max(0, remainingTime);
    }

    private static void CheckDeactivation(int index, RuleInternalState internalState)
    {
        var oldState = internalState.TinctureUsageTracker.GetValueOrDefault(index);
        if (oldState.WasActive)
        {
            internalState.TinctureUsageTracker[index] = (false, DateTime.UtcNow);
        }
    }

    private static readonly string[] LifeFlaskBuffs =
    {
        "flask_effect_life",
        "flask_effect_life_not_removed_when_full",
    };

    private static readonly string[] ManaFlaskBuffs =
    {
        "flask_effect_mana",
        "flask_effect_mana_not_removed_when_full",
        "flask_instant_mana_recovery_at_end_of_effect",
    };

    private static IEnumerable<string> GetFlaskBuffNames(Flask flask)
    {
        var type = flask.M.Read<int>(flask.Address + 0x28, 0x10);
        return type switch
        {
            1 => LifeFlaskBuffs,
            2 => ManaFlaskBuffs,
            3 => LifeFlaskBuffs.Concat(ManaFlaskBuffs),
            4 when flask.M.ReadStringU(flask.M.Read<long>(flask.Address + 0x28, 0x18, 0x0)) is { } s and not "" => new[] { s },
            _ => Enumerable.Empty<string>()
        };
    }
}
