using CounterStrikeSharp.API;

namespace src.utils
{
    /// <summary>
    /// The in-game button AlternativeSkillButton names ("Use", "Reload", "Inspect"...) and the key it sits on by
    /// default. A one-letter value such as "X" is not a button: players bind css_useSkill to it themselves.
    /// </summary>
    public static class SkillButton
    {
        private static string? parsedFrom;
        private static PlayerButtons? parsed;

        /// <summary>The button that fires the skill, or false when AlternativeSkillButton names none.</summary>
        public static bool TryGet(out PlayerButtons button)
        {
            var value = Config.LoadedConfig.AlternativeSkillButton;
            if (!ReferenceEquals(value, parsedFrom))
            {
                parsed = Parse(value);
                parsedFrom = value;
            }

            button = parsed ?? default;
            return parsed != null;
        }

        /// <summary>The key the configured button sits on by default ("Use" is E), or null when there is none.</summary>
        public static string? DefaultKey()
        {
            if (!TryGet(out var button)) return null;
            return button switch
            {
                PlayerButtons.Use => "E",
                PlayerButtons.Reload => "R",
                PlayerButtons.Inspect => "F",
                PlayerButtons.Attack => "M1",
                PlayerButtons.Attack2 => "M2",
                PlayerButtons.Jump => "SPACE",
                PlayerButtons.Duck => "CTRL",
                PlayerButtons.Speed => "SHIFT",
                PlayerButtons.Scoreboard => "TAB",
                PlayerButtons.Zoom => "Z",
                _ => null,
            };
        }

        private static PlayerButtons? Parse(string? value)
        {
            var name = value?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length < 2) return null;

            // The game calls the inspect button "lookatweapon" (+lookatweapon); the enum calls it Inspect.
            if (name.Equals("LookAtWeapon", StringComparison.OrdinalIgnoreCase)) return PlayerButtons.Inspect;

            return Enum.TryParse<PlayerButtons>(name, ignoreCase: true, out var button) ? button : null;
        }
    }
}
