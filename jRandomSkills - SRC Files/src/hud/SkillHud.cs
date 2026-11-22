using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using src.player;

namespace src.utils
{
    public enum HudState { Auto, Ready, Cooldown, Active, Busy, Progress, Charges, Value, Toggle, Error }

    public enum HudTone { None, Good, Mid, Bad }

    /// <summary>
    /// What a skill shows on the HUD card besides its name and description: the structured twin of
    /// <see cref="jSkill_PlayerInfo.PrintHTML"/>. Skills write it next to PrintHTML through <see cref="SkillHud"/>,
    /// usually every HUD frame, and HeroesHud reads it. A state nobody refreshed for half a second falls back to
    /// <see cref="HudState.Auto"/> (what the skill's kind shows by itself), the same way a stale PrintHTML line
    /// stops mattering once its skill stops writing it.
    /// </summary>
    public sealed class SkillHudState
    {
        /// <summary>Ticks a written state stays good without being written again.</summary>
        public const int StaleTicks = 32;

        public HudState State;
        public int Stamp = int.MinValue;

        /// <summary>Cooldown, Active, Progress and the charge cooldown: when it ends, and how long it is in all.</summary>
        public DateTime EndsAt;
        public float Seconds;

        public string Big = "";
        public string? LabelKey;
        public object[] LabelArgs = [];
        /// <summary>Raw label text (a player name). Wins over <see cref="LabelKey"/>.</summary>
        public string? LabelText;
        public HudTone Tone;

        public int Count, Max;
        /// <summary>A static bar, 0..1 (fuel), or null for none.</summary>
        public float? Bar;
        /// <summary>Pips without a number (Partner's level), or -1.</summary>
        public int Pips = -1, PipsMax;

        public bool ToggleOn;
        public string? ToggleOnKey, ToggleOffKey;

        public string? ErrorKey;
        public object[] ErrorArgs = [];

        /// <summary>A value rolled once for the round (Rambo's health, Rich Boy's money, Glass Cannon).</summary>
        public string? FixedBig, FixedLabelKey;

        /// <summary>The skill an enemy took or switched off (Deactivator, Thief), and which skill did it.</summary>
        public Skills LostSkill, LostTo;

        public bool Fresh => State != HudState.Auto && Server.TickCount - Stamp <= StaleTicks;

        /// <summary>Transient state only: what ResetPrintHTML clears.</summary>
        public void Clear()
        {
            State = HudState.Auto;
            Stamp = int.MinValue;
            LabelKey = null;
            LabelText = null;
            LabelArgs = [];
            ErrorKey = null;
            ErrorArgs = [];
            Bar = null;
            Pips = -1;
            Tone = HudTone.None;
            Big = "";
        }

        /// <summary>Everything, at round start.</summary>
        public void ResetRound()
        {
            Clear();
            FixedBig = null;
            FixedLabelKey = null;
            LostSkill = Skills.None;
            LostTo = Skills.None;
        }

        internal void Set(HudState state)
        {
            Clear();
            State = state;
            Stamp = Server.TickCount;
        }
    }

    /// <summary>
    /// The calls skills make to describe their state to the HUD card, and the HUD-or-legacy switches for the
    /// center alerts, the Coins pickup and the "an enemy used a skill on you" chat lines. Every switch prints
    /// exactly what the plugin printed before when the HUD does not take it (Hud.Enabled = false, the player
    /// has no panel, or the layout failed to load).
    /// </summary>
    public static class SkillHud
    {
        // ------------------------------------------------------------------ card state

        public static void Clear(jSkill_PlayerInfo? info) => info?.Hud.Clear();

        /// <summary>Ready to use. <paramref name="labelText"/> replaces "Ready" (Team Teleport's teammate).</summary>
        public static void Ready(jSkill_PlayerInfo? info, string? labelText = null)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Ready);
            info.Hud.LabelText = labelText;
        }

        /// <summary>A cooldown that started at <paramref name="startedAt"/> and lasts <paramref name="seconds"/>; ready once it is over.</summary>
        public static void Cooldown(jSkill_PlayerInfo? info, DateTime startedAt, float seconds)
        {
            if (info == null) return;
            if (seconds <= 0 || startedAt == DateTime.MinValue)
            {
                Ready(info);
                return;
            }

            var endsAt = startedAt.AddSeconds(seconds);
            if (endsAt <= DateTime.Now)
            {
                Ready(info);
                return;
            }

            info.Hud.Set(HudState.Cooldown);
            info.Hud.EndsAt = endsAt;
            info.Hud.Seconds = seconds;
        }

        /// <summary>A cooldown given as the seconds left (skills that count in ticks).</summary>
        public static void CooldownLeft(jSkill_PlayerInfo? info, float remaining, float seconds)
        {
            if (info == null) return;
            if (remaining <= 0)
            {
                Ready(info);
                return;
            }

            info.Hud.Set(HudState.Cooldown);
            info.Hud.EndsAt = DateTime.Now.AddSeconds(remaining);
            info.Hud.Seconds = Math.Max(seconds, remaining);
        }

        /// <summary>Running out: noclip, a hologram, the rewind marker. The bar drains.</summary>
        public static void Active(jSkill_PlayerInfo? info, float remaining, float seconds, string labelKey = "hud_active")
        {
            if (info == null) return;
            info.Hud.Set(HudState.Active);
            info.Hud.EndsAt = DateTime.Now.AddSeconds(Math.Max(remaining, 0));
            info.Hud.Seconds = Math.Max(seconds, remaining);
            info.Hud.LabelKey = labelKey;
        }

        /// <summary>Doing something with no clock (Grapple pulling).</summary>
        public static void Busy(jSkill_PlayerInfo? info, string labelKey)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Busy);
            info.Hud.LabelKey = labelKey;
        }

        /// <summary>Filling up: planting, psychic defusing. The bar fills.</summary>
        public static void Progress(jSkill_PlayerInfo? info, float remaining, float seconds, string labelKey)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Progress);
            info.Hud.EndsAt = DateTime.Now.AddSeconds(Math.Max(remaining, 0));
            info.Hud.Seconds = Math.Max(seconds, remaining);
            info.Hud.LabelKey = labelKey;
        }

        /// <summary>Charges left (Medic), with the short cooldown between two uses.</summary>
        public static void Charges(jSkill_PlayerInfo? info, int count, int max, DateTime cooldownStart, float cooldown)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Charges);
            info.Hud.Count = Math.Max(count, 0);
            info.Hud.Max = Math.Max(max, 1);
            info.Hud.EndsAt = cooldownStart == DateTime.MinValue ? DateTime.MinValue : cooldownStart.AddSeconds(cooldown);
            info.Hud.Seconds = cooldown;
        }

        /// <summary>A value: fuel (with a bar), the nearest enemy's distance, a partner's level (pips).</summary>
        public static void Value(jSkill_PlayerInfo? info, string big, string? labelKey, string? labelText = null, HudTone tone = HudTone.None, float? bar = null, int pips = -1, int pipsMax = 0)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Value);
            info.Hud.Big = big;
            info.Hud.LabelKey = labelKey;
            info.Hud.LabelText = labelText;
            info.Hud.Tone = tone;
            info.Hud.Bar = bar is { } b ? Math.Clamp(b, 0f, 1f) : null;
            info.Hud.Pips = pips;
            info.Hud.PipsMax = pipsMax;
        }

        public static void Toggle(jSkill_PlayerInfo? info, bool on, string labelKey, string onKey, string offKey)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Toggle);
            info.Hud.ToggleOn = on;
            info.Hud.LabelKey = labelKey;
            info.Hud.ToggleOnKey = onKey;
            info.Hud.ToggleOffKey = offKey;
        }

        /// <summary>Can't use it now: no enemy found, no space, a weapon the skill does not allow.</summary>
        public static void Error(jSkill_PlayerInfo? info, string key, params object[] args)
        {
            if (info == null) return;
            info.Hud.Set(HudState.Error);
            info.Hud.ErrorKey = key;
            info.Hud.ErrorArgs = args;
        }

        /// <summary>A value rolled once at the start of the round. Stays until <see cref="ClearRolled"/> or the next round.</summary>
        public static void Rolled(jSkill_PlayerInfo? info, string big, string labelKey)
        {
            if (info == null) return;
            info.Hud.FixedBig = big;
            info.Hud.FixedLabelKey = labelKey;
        }

        public static void ClearRolled(jSkill_PlayerInfo? info)
        {
            if (info == null) return;
            info.Hud.FixedBig = null;
            info.Hud.FixedLabelKey = null;
        }

        // ------------------------------------------------------------------ alerts, coins, enemy skills

        /// <summary>Free Planter and Short Fuse: "The bomb has been planted / N seconds till detonation".</summary>
        public static void BombPlanted(CCSPlayerController viewer, Skills skill, int seconds)
        {
            if (HeroesHud.BombPlanted(viewer, skill, seconds)) return;
            viewer.PrintToCenterAlert(viewer.GetTranslation("bombplanted", seconds));
        }

        /// <summary>Hot Bomb: "Careful, the bomb is hot!", or "The bomb is no longer hot." when <paramref name="hot"/> is false.</summary>
        public static void HotBomb(CCSPlayerController viewer, bool hot)
        {
            if (HeroesHud.HotBomb(viewer, hot)) return;
            viewer.PrintToCenterAlert(viewer.GetTranslation(hot ? "hotbomb_hint" : "hotbomb_disable_info"));
        }

        /// <summary>Zone Reaper: "This bombsite has been disabled!" while the bomb carrier stands in the closed site.</summary>
        public static void SiteClosed(CCSPlayerController viewer, string? siteLetter)
        {
            if (HeroesHud.SiteClosed(viewer, siteLetter)) return;
            viewer.PrintToCenterAlert(viewer.GetTranslation("areareaper_bombsite_disabled"));
        }

        /// <summary>Illiterate's warning to its enemies. The chat line it also prints stays as it is.</summary>
        public static void IlliterateAlert(CCSPlayerController viewer, string legacyText)
        {
            if (HeroesHud.IlliterateAlert(viewer)) return;
            viewer.PrintToCenterAlert(legacyText);
        }

        /// <summary>A Coins pickup: "+$300" next to the money instead of center text.</summary>
        public static void Coin(CCSPlayerController collector, int gained, int value, int moneyAfter)
        {
            if (HeroesHud.Coin(collector, gained, moneyAfter)) return;
            collector.PrintToCenter($"+${value}");
        }

        /// <summary>
        /// "An enemy used a skill on you": the red chat line, or the banner with Hud.EnemySkillAlerts.
        /// <paramref name="lostFrom"/> is the victim's info when the skill took their own skill away (Deactivator,
        /// Thief): the card then shows that skill switched off. Call it after Skill/SpecialSkill were swapped.
        /// </summary>
        public static void EnemySkill(CCSPlayerController viewer, Skills skill, string key, object[]? args = null, jSkill_PlayerInfo? lostFrom = null)
        {
            args ??= [];
            if (lostFrom != null && lostFrom.Skill == Skills.None && lostFrom.SpecialSkill != Skills.None)
            {
                lostFrom.Hud.LostSkill = lostFrom.SpecialSkill;
                lostFrom.Hud.LostTo = skill;
            }

            if (HeroesHud.EnemySkill(viewer, skill, key, args)) return;
            viewer.PrintToChat($" {ChatColors.Red}" + viewer.GetTranslation(key, args));
        }
    }
}
