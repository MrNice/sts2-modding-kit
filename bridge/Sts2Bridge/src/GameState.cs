using System.Text.Json.Nodes;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Bridge;

/// <summary>Read-only snapshots of the run and the fight, from this game copy's point of view.</summary>
internal static class GameState
{
    public static JsonObject Snapshot(bool full)
    {
        var o = Screens.Describe();
        var rm = RunManager.Instance;
        var state = Safe.Get(() => rm?.DebugOnlyGetState());
        if (rm is null || !rm.IsInProgress || state is null) return o;

        var me = Safe.Get(() => LocalContext.GetMe(state.Players));
        o["run"] = new JsonObject
        {
            ["seed"] = Safe.Text(() => state.Rng.StringSeed),
            ["act"] = state.CurrentActIndex + 1,
            ["floor"] = state.TotalFloor,
            ["actFloor"] = state.ActFloor,
            ["ascension"] = state.AscensionLevel,
            ["room"] = state.CurrentRoom?.GetType().Name,
            ["gameOver"] = state.IsGameOver,
        };

        var players = new JsonArray();
        int index = 0;
        foreach (var p in state.Players)
        {
            var po = PlayerInfo(p, ReferenceEquals(p, me), full);
            po["index"] = index++;   // play_card's ally index
            players.Add(po);
        }
        o["players"] = players;

        var cm = CombatManager.Instance;
        if (cm is not null && cm.IsInProgress && me is not null)
            o["combat"] = Combat(me, full);
        return o;
    }

    private static JsonObject PlayerInfo(Player p, bool isMe, bool full)
    {
        var o = new JsonObject
        {
            ["netId"] = p.NetId.ToString(),
            ["me"] = isMe ? true : null,
            ["character"] = Safe.Text(() => p.Character.Title.GetFormattedText()),
            ["hp"] = Safe.Get(() => p.Creature.CurrentHp),
            ["maxHp"] = Safe.Get(() => p.Creature.MaxHp),
            ["gold"] = p.Gold,
        };
        var deck = Safe.Get(() => p.Deck.Cards.ToList()) ?? new List<CardModel>();
        o["deckSize"] = deck.Count;
        if (full) o["deck"] = new JsonArray(deck.Select(c => (JsonNode)CardInfo(c, null)).ToArray());
        o["relics"] = new JsonArray(Safe.Get(() => p.Relics.Select(r => (JsonNode)Safe.Text(() => r.Title.GetFormattedText())).ToArray()) ?? []);
        o["potions"] = new JsonArray(Safe.Get(() => p.Potions.Select(x => (JsonNode)Safe.Text(() => x.Title.GetFormattedText())).ToArray()) ?? []);
        return o;
    }

    private static JsonObject Combat(Player me, bool full)
    {
        var o = new JsonObject();
        var cs = Safe.Get(() => me.Creature.CombatState);
        if (cs is not null)
        {
            o["round"] = cs.RoundNumber;
            o["side"] = Safe.Text(() => (cs as CombatState)?.CurrentSide.ToString());
        }

        var pcs = me.PlayerCombatState;
        if (pcs is not null)
        {
            o["energy"] = pcs.Energy;
            o["maxEnergy"] = pcs.MaxEnergy;
            o["block"] = Safe.Get(() => me.Creature.Block);
            var hand = new JsonArray();
            int i = 0;
            foreach (var card in pcs.Hand.Cards) hand.Add(CardInfo(card, i++));
            o["hand"] = hand;
            o["drawPile"] = pcs.DrawPile.Cards.Count;
            o["discardPile"] = pcs.DiscardPile.Cards.Count;
            o["exhaustPile"] = pcs.ExhaustPile.Cards.Count;
            if (full)
            {
                o["drawPileCards"] = new JsonArray(pcs.DrawPile.Cards.Select(c => (JsonNode)Safe.Text(() => c.Title)).ToArray());
                o["discardPileCards"] = new JsonArray(pcs.DiscardPile.Cards.Select(c => (JsonNode)Safe.Text(() => c.Title)).ToArray());
            }
            o["powers"] = Powers(me.Creature);
        }

        if (cs is not null)
        {
            var enemies = new JsonArray();
            var targets = Safe.Get(() => cs.PlayerCreatures) ?? new List<Creature>();
            int index = 0;
            foreach (var e in Enemies(cs))
            {
                var eo = new JsonObject
                {
                    ["index"] = index++,
                    ["name"] = Safe.Text(() => e.Name),
                    ["hp"] = e.CurrentHp,
                    ["maxHp"] = e.MaxHp,
                    ["block"] = e.Block,
                    ["alive"] = e.IsAlive,
                    ["intents"] = Intents(e, targets),
                    ["powers"] = Powers(e),
                };
                enemies.Add(eo);
            }
            o["enemies"] = enemies;
        }
        return o;
    }

    /// <summary>The fight's enemies in display order; play_card's target index points into this list.</summary>
    public static List<Creature> Enemies(ICombatState cs) => Safe.Get(() => cs.Enemies.ToList()) ?? new List<Creature>();

    private static JsonArray Intents(Creature enemy, IReadOnlyList<Creature> targets)
    {
        var list = new JsonArray();
        var move = Safe.Get(() => enemy.Monster?.NextMove);
        if (move is null) return list;
        foreach (var intent in Safe.Get(() => move.Intents) ?? [])
        {
            var io = new JsonObject { ["type"] = intent.IntentType.ToString() };
            if (intent is AttackIntent attack)
            {
                io["damage"] = Safe.Get(() => attack.GetSingleDamage(targets, enemy));
                io["hits"] = Safe.Get(() => attack.Repeats);
            }
            list.Add(io);
        }
        return list;
    }

    private static JsonArray Powers(Creature c) =>
        new(Safe.Get(() => c.Powers.Select(p => (JsonNode)new JsonObject
        {
            ["name"] = Safe.Text(() => p.Title.GetFormattedText()),
            ["amount"] = p.Amount,
        }).ToArray()) ?? []);

    private static JsonObject CardInfo(CardModel c, int? handIndex)
    {
        var o = new JsonObject();
        if (handIndex is { } i) o["index"] = i;
        o["id"] = Safe.Text(() => c.Id.Entry);
        o["name"] = Safe.Text(() => c.Title);
        if (c.CurrentUpgradeLevel > 0) o["upgrades"] = c.CurrentUpgradeLevel;
        if (handIndex is not null)
        {
            o["cost"] = Safe.Get(() => c.EnergyCost.CostsX) ? "X" : Safe.Get(() => c.EnergyCost.GetResolved()).ToString();
            o["playable"] = Safe.Get(() => c.CanPlay());
            o["target"] = Safe.Text(() => c.TargetType.ToString());
        }
        return o;
    }

    // ---- Actions on the local player's fight ----

    /// <param name="targetIndex">Enemy index from the state's enemies list.</param>
    /// <param name="allyIndex">Player index from the state's players list, for cards that target an ally.</param>
    public static JsonObject PlayCard(int handIndex, int? targetIndex, int? allyIndex)
    {
        var rm = RunManager.Instance;
        var cm = CombatManager.Instance;
        if (rm is null || cm is null || !cm.IsInProgress) throw BridgeError.NotYet("not in a fight");
        var state = rm.DebugOnlyGetState() ?? throw BridgeError.NotYet("no run");
        var me = LocalContext.GetMe(state.Players) ?? throw new BridgeError("local player not found");
        var hand = me.PlayerCombatState?.Hand.Cards ?? throw BridgeError.NotYet("no hand");
        if (handIndex < 0 || handIndex >= hand.Count) throw new BridgeError($"hand index {handIndex} out of range (hand has {hand.Count})");
        var card = hand[handIndex];

        if (targetIndex is not null && allyIndex is not null) throw new BridgeError("pass target (an enemy) or ally (a player), not both");
        Creature? target = null;
        string targetName = "";
        if (targetIndex is { } t)
        {
            var enemies = Enemies(me.Creature.CombatState ?? throw new BridgeError("no combat state"));
            if (t < 0 || t >= enemies.Count) throw new BridgeError($"target index {t} out of range ({enemies.Count} enemies)");
            target = enemies[t];
            targetName = Safe.Text(() => target.Name);
        }
        else if (allyIndex is { } a)
        {
            if (a < 0 || a >= state.Players.Count) throw new BridgeError($"ally index {a} out of range ({state.Players.Count} players)");
            target = state.Players[a].Creature;
            targetName = Safe.Text(() => state.Players[a].Character.Title.GetFormattedText());
        }

        string title = Safe.Text(() => card.Title);
        bool played = card.TryManualPlay(target);
        if (!played)
        {
            string kind = Safe.Text(() => card.TargetType.ToString());
            throw BridgeError.NotYet($"the game refused to play {title} (target type {kind}: not playable now, not enough energy, or a missing or wrong target)");
        }
        GameEvents.Push("card_played", new JsonObject { ["card"] = title, ["target"] = targetName });
        return new JsonObject { ["played"] = title, ["target"] = targetName.Length > 0 ? targetName : null };
    }
}
