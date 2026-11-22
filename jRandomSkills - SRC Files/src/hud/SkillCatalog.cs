using System.Globalization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;
using src.player;

namespace src.utils
{
    /// <summary>The kind of a skill: picks the card's icon and what the card shows when the skill writes nothing.</summary>
    internal enum SkillKind { Passive, Roll, Use, Charges, Fuel, Toggle, Live, Pick, Bomb, Coins }

    /// <summary>
    /// What the HUD knows about every skill that the plugin does not say: its kind (the preview's "Skill colors"
    /// tab), the colour class its configured hex maps to, how a rolled value reads, and the use key.
    /// </summary>
    internal static partial class SkillCatalog
    {
        private static readonly Dictionary<Skills, SkillKind> kinds = Build();

        private static Dictionary<Skills, SkillKind> Build()
        {
            var map = new Dictionary<Skills, SkillKind>();
            void Add(SkillKind kind, params Skills[] skills)
            {
                foreach (var skill in skills) map[skill] = kind;
            }

            Add(SkillKind.Use,
                Skills.AimLock, Skills.Anomaly, Skills.Cypher, Skills.EnemySpawn, Skills.ExplodingBarrel, Skills.FalconEye,
                Skills.FastReload, Skills.Flashlight, Skills.Fortnite, Skills.GodMode, Skills.Grapple, Skills.Iana,
                Skills.Illusionist, Skills.Noclip, Skills.RandomWeapon, Skills.Replicator, Skills.Retreat, Skills.Rewind,
                Skills.SniperElite, Skills.Spectator, Skills.SwapPosition, Skills.TakeAmmo, Skills.TeamTeleport,
                Skills.ThirdEye, Skills.ThrowingKnife, Skills.Tripwire, Skills.WeaponsSwap);
            Add(SkillKind.Charges, Skills.Medic);
            Add(SkillKind.Fuel, Skills.Pilot);
            Add(SkillKind.Toggle, Skills.Jester);
            Add(SkillKind.Live, Skills.Distancer);
            Add(SkillKind.Roll,
                Skills.Armored, Skills.Astronaut, Skills.Behind, Skills.Catapult, Skills.Disarmament, Skills.Dwarf,
                Skills.ExplosiveShot, Skills.Flash, Skills.PawelJumper, Skills.Phoenix, Skills.Pickpocket, Skills.Push,
                Skills.Rambo, Skills.RichBoy, Skills.Shade, Skills.Soldier, Skills.Teleporter);
            Add(SkillKind.Pick,
                Skills.Bankrupt, Skills.Bounty, Skills.Bowlegs, Skills.CarefulBullets, Skills.Darkness, Skills.Deactivator,
                Skills.Deaf, Skills.Duplicator, Skills.ExpensiveAmmo, Skills.Gambler, Skills.Giant, Skills.Glitch,
                Skills.Inheritance, Skills.Jammer, Skills.JetKick, Skills.JumpBan, Skills.JumpCurse, Skills.LifeSwap,
                Skills.Magnifier, Skills.MoneySwap, Skills.Nemesis, Skills.Nightmare, Skills.Partner, Skills.Poison,
                Skills.PrimaryBan, Skills.Thief, Skills.Voodoo, Skills.WildThrow);
            Add(SkillKind.Bomb,
                Skills.AreaReaper, Skills.C4Camouflage, Skills.ChillOut, Skills.FragileBomb, Skills.HotBomb,
                Skills.PsychicDefusing, Skills.Planter, Skills.ShortBomb);
            Add(SkillKind.Coins, Skills.Coins);
            // Everything else (79 skills) is passive.
            return map;
        }

        public static SkillKind KindOf(Skills skill) => kinds.TryGetValue(skill, out var kind) ? kind : SkillKind.Passive;

        public static string KindClass(SkillKind kind) => kind switch
        {
            SkillKind.Roll => "k-roll",
            SkillKind.Use => "k-use",
            SkillKind.Charges => "k-charges",
            SkillKind.Fuel => "k-fuel",
            SkillKind.Toggle => "k-toggle",
            SkillKind.Live => "k-live",
            SkillKind.Pick => "k-pick",
            SkillKind.Bomb => "k-bomb",
            SkillKind.Coins => "k-coins",
            _ => "k-passive",
        };

        /// <summary>Skills used with the skill key: the card shows the key chip.</summary>
        public static bool UsesKey(SkillKind kind) => kind is SkillKind.Use or SkillKind.Charges;

        // ------------------------------------------------------------------ colours

        /// <summary>The 14 classes the layout carries, as hue centres (red appears twice to wrap at 360).</summary>
        private static readonly (string Name, double Hue)[] hues =
        [
            ("red", 0), ("orange", 24), ("amber", 42), ("yellow", 58), ("lime", 84), ("green", 125), ("teal", 163),
            ("cyan", 190), ("blue", 215), ("indigo", 240), ("violet", 272), ("pink", 312), ("red", 360),
        ];

        private static readonly Dictionary<string, string> hueCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The colour class for a skill's configured hex: the nearest of 12 hues, or white / grey for colours with
        /// almost no saturation or light. The same rule as the preview, so 164 skills read on the dark card.
        /// </summary>
        public static string HueClass(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return "h-white";
            if (hueCache.TryGetValue(hex, out var cached)) return cached;

            string result = "h-white";
            var s = hex.Trim().TrimStart('#');
            if (s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int n))
            {
                double r = (n >> 16 & 255) / 255.0, g = (n >> 8 & 255) / 255.0, b = (n & 255) / 255.0;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double l = (max + min) / 2, d = max - min;
                if (d == 0)
                    result = l > .62 ? "h-white" : "h-grey";
                else
                {
                    double sat = d / (1 - Math.Abs(2 * l - 1));
                    double h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
                    h = (h * 60 + 360) % 360;

                    if (sat < .2 || l < .1)
                        result = l > .62 ? "h-white" : "h-grey";
                    else
                    {
                        var best = hues[0];
                        foreach (var x in hues)
                            if (Math.Abs(x.Hue - h) < Math.Abs(best.Hue - h)) best = x;
                        result = "h-" + best.Name;
                    }
                }
            }

            hueCache[hex] = result;
            return result;
        }

        public static string ColorOf(Skills skill) =>
            SkillData.GetInfo(skill)?.Color ?? SkillsInfo.GetValue<string>(skill, "color") ?? "#FFFFFF";

        // ------------------------------------------------------------------ rarity, team

        /// <summary>0 Common (no chip) .. 4 Legendary.</summary>
        public static int RarityOf(Skills skill)
        {
            var rarity = SkillsInfo.GetSkillConfig(skill)?.Rarity;
            return Enum.TryParse<Rarity>(rarity, true, out var r) ? (int)r : 0;
        }

        /// <summary>2 T only, 3 CT only, 0 both.</summary>
        public static int TeamOf(Skills skill)
        {
            int team = SkillsInfo.GetSkillConfig(skill)?.OnlyTeam ?? 0;
            return team is 2 or 3 ? team : 0;
        }

        // ------------------------------------------------------------------ rolled values

        private enum RollFormat { Times, Percent, Count }

        private static readonly Dictionary<Skills, RollFormat> rolls = new()
        {
            [Skills.Armored] = RollFormat.Times,
            [Skills.Astronaut] = RollFormat.Times,
            [Skills.Dwarf] = RollFormat.Times,
            [Skills.Flash] = RollFormat.Times,
            [Skills.Soldier] = RollFormat.Times,
            [Skills.Behind] = RollFormat.Percent,
            [Skills.Catapult] = RollFormat.Percent,
            [Skills.Disarmament] = RollFormat.Percent,
            [Skills.ExplosiveShot] = RollFormat.Percent,
            [Skills.Phoenix] = RollFormat.Percent,
            [Skills.Pickpocket] = RollFormat.Percent,
            [Skills.Push] = RollFormat.Percent,
            [Skills.Shade] = RollFormat.Percent,
            [Skills.Teleporter] = RollFormat.Percent,
            [Skills.PawelJumper] = RollFormat.Count,
        };

        /// <summary>The value a skill rolled this round (SkillChance), as the card shows it: x1.27, 35%, 3.</summary>
        public static bool TryRolled(Skills skill, float? chance, out string big, out string labelKey)
        {
            big = "";
            labelKey = "hud_roll_" + SkillNames.Get(skill).ToLowerInvariant();
            if (chance is not { } value || !rolls.TryGetValue(skill, out var format)) return false;

            big = format switch
            {
                RollFormat.Times => "x" + Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture),
                RollFormat.Percent => Math.Round(value * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                _ => Math.Round(value).ToString("0", CultureInfo.InvariantCulture),
            };
            return true;
        }

        // ------------------------------------------------------------------ the use key and the description

        /// <summary>
        /// The key chip: Hud.KeyLabel, else what AlternativeSkillButton names. "X" stays "X" (players bind
        /// css_useSkill to it); a button name reads as the key it usually sits on ("Use" is E). Empty: no chip.
        /// </summary>
        public static string KeyLabel()
        {
            var configured = Config.LoadedConfig.Hud?.KeyLabel;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            var button = Config.LoadedConfig.AlternativeSkillButton?.Trim();
            if (string.IsNullOrEmpty(button)) return "";

            return SkillButton.DefaultKey() ?? button.ToUpperInvariant();
        }

        // "[css_useSkill]", "[css_useSkill/X]" or the key the translations put there ("[E]").
        [GeneratedRegex(@"^\s*(?:click|press|use)\s+\[[^\]]+\]\s+(?:to\s+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex UseKeyLead();

        /// <summary>
        /// The description without "Click [E] to" (or "[css_useSkill/X]"): the key chip says it. English only; any other
        /// wording is left as it is.
        /// </summary>
        public static string Description(CCSPlayerController viewer, Skills skill)
        {
            var text = viewer.GetSkillDescription(skill);
            if (descCache.TryGetValue(text, out var cached)) return cached;

            var trimmed = UseKeyLead().Replace(text, "", 1);
            var result = trimmed.Length == text.Length || trimmed.Length == 0
                ? text
                : char.ToUpperInvariant(trimmed[0]) + trimmed[1..];

            if (descCache.Count < 2048) descCache[text] = result;
            return result;
        }

        private static readonly Dictionary<string, string> descCache = new(StringComparer.Ordinal);
    }
}
