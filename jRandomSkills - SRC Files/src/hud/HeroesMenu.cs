using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using src.player;
using src.player.skills;

namespace src.utils
{
    /// <summary>
    /// Drives heroes_menu.xml (hud/panorama, shipped in the workshop addon): a pick skill's "select a player" list
    /// (Thief, Poison, Jammer and the other pick skills, plus Zone Reaper's bombsites and Gambler's skills) as a
    /// mouse menu instead of WASDMenuAPI's center HTML, and the !skills list. With Hud.Enabled or Hud.PickMenu off,
    /// or for a player the panel cannot be opened for, SkillUtils falls back to the WASD menu exactly as before.
    ///
    /// <para><b>The skills do not change.</b> They still call SkillUtils.CreateMenu / UpdateMenu / CloseMenu /
    /// HasMenu with (label, value) pairs; SkillUtils hands them here first. A click runs what the WASD selection
    /// ran: the skill's TypeSkill with the row's value, then the menu closes (unless the row is one that keeps it,
    /// Gambler's reroll). Every close the skill already does for the WASD menu (round end, death, used, the time
    /// limit) closes this one.</para>
    ///
    /// <para><b>All players.</b> A list of living players gets every other player of the same side too, greyed
    /// out with the reason where it is known: dead, already cursed (CurseSkillPerPlayer), immune (Giant on a
    /// Chicken), no skill to take. Each row shows the team, HP and alive state, read again every
    /// <see cref="Every"/>, so a target who dies between the skill's own updates greys out at once.</para>
    ///
    /// <para><b>Closing.</b> The X closes it and gives the mouse back; the pick is still pending, so the skill key
    /// (AlternativeSkillButton, or css_useSkill) opens it again, and pressing it while open closes it. Escape never
    /// reaches the server, so it cannot close the menu. !skills toggles its list the same way.</para>
    /// </summary>
    internal static partial class HeroesMenu
    {
        private const string Layout = "panorama/layout/custom_game/heroes_menu.vxml_c";
        private const string Root = "HeroesMenuRoot";
        private const string Card = "hm";

        /// <summary>The layout's row pool, one page.</summary>
        public const int RowSlots = 6;

        private const int HpSteps = 10;

        /// <summary>How often an open menu reads HP and alive state again.</summary>
        private static readonly TimeSpan Every = TimeSpan.FromSeconds(0.25);

        private static readonly string[] HueClasses =
            ["h-red", "h-orange", "h-amber", "h-yellow", "h-lime", "h-green", "h-teal", "h-cyan", "h-blue", "h-indigo",
             "h-violet", "h-pink", "h-white", "h-grey"];
        private static readonly string[] KindClasses = ["k-pick", "k-bomb", "k-skills"];
        private static readonly string[] PlaceClasses = ["p-left", "p-center"];
        private static readonly string[] TeamClasses = ["tm-0", "tm-2", "tm-3"];
        private static readonly string[] ToneClasses = ["t-hi", "t-mid", "t-lo"];
        private static readonly string[] HpClasses = [.. Enumerable.Range(0, HpSteps + 1).Select(i => $"hp-{i}")];
        private static readonly string[] RowIds = [.. Enumerable.Range(0, RowSlots).Select(i => $"hm_r{i}")];

        private static BasePlugin? plugin;
        private static PanelHandle? panel;
        private static bool failed;
        private static bool closing;
        private static readonly Dictionary<int, Viewer> viewers = [];

        // ------------------------------------------------------------------ what a viewer has

        /// <summary>One choice as the skill passed it. <see cref="Keeps"/>: Gambler's reroll, which leaves the menu open.</summary>
        private sealed record Choice(string Label, string Value, bool Keeps, bool Last);

        private enum Side { Options, Enemies, Teammates, Everyone }

        /// <summary>A skill's pending pick, from CreateMenu until the skill closes it.</summary>
        private sealed class Pick
        {
            public required jSkill_PlayerInfo Info;
            public required Skills Skill;
            public List<Choice> Choices = [];
            public Choice? Last;

            /// <summary>Values whose player was alive when the skill listed them: one dead now is greyed out.</summary>
            public HashSet<string> AliveWhenListed = [];

            /// <summary>The skill picks among the living, so the rest of that side is listed greyed out.</summary>
            public bool WithOthers;

            /// <summary>Who it picks from, as last seen with anyone listed: an empty refresh keeps the title.</summary>
            public Side Side = Side.Options;
        }

        private sealed class Row
        {
            public string Value = "";
            public bool Enabled = true, Keeps, Player, Dead, Option, Act, IsLast;
            public string Name = "", Note = "", Hp = "";
            public string? Hue;
            public string Team = "tm-0", Tone = "t-hi";
            public int Step = HpSteps;
        }

        private sealed class Viewer
        {
            public Pick? Pick;

            /// <summary>The pick is on screen. False with a pick: closed with the X (or the key), the key opens it again.</summary>
            public bool PickShown;

            /// <summary>!skills is on screen. It parks the pick while it is up.</summary>
            public bool Browse;
            public List<Skills> BrowseSkills = [];

            public int Page;

            /// <summary>As last drawn: what a click on hm_rN means.</summary>
            public List<Row> Rows = [];

            public DateTime NextDraw = DateTime.MinValue;
            public readonly Writer W = new();
        }

        private static Viewer ViewerOf(int slot)
        {
            if (!viewers.TryGetValue(slot, out var v))
                viewers[slot] = v = new Viewer();
            return v;
        }

        private static bool Visible(Viewer v) => v.Browse || (v.Pick != null && v.PickShown);

        // ------------------------------------------------------------------ lifecycle

        public static void Load(BasePlugin owner) => plugin = owner;

        /// <summary>Spawned the first time a player needs it, after HeroesHud has initialised PanoramaManager.</summary>
        private static bool EnsurePanel()
        {
            if (panel != null) return true;
            if (failed || plugin == null || !Panorama.IsInitialised) return false;

            try
            {
                panel = Panorama.Spawn(Layout, new LayoutContract
                {
                    RootPanelId = Root,
                    RevealClass = "show",
                    CloseButtonId = "hm_close",
                    RowCount = 0,
                    CaptureInput = true,
                    HideHud = HideHudFlags.None,
                    SharedText = false,
                });
                panel.OnEvent += OnEvent;
                plugin.Logger.LogInformation("Heroes pick menu ready ({Layout})", Layout);
                return true;
            }
            catch (Exception ex)
            {
                failed = true;
                panel = null;
                plugin.Logger.LogWarning("Heroes pick menu failed to start, using the WASD menus instead: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>Plugin unload, before PanoramaManager shuts down.</summary>
        public static void Unload()
        {
            try { panel?.Dispose(); }
            catch (Exception ex) { plugin?.Logger.LogWarning("Heroes pick menu shutdown: {Message}", ex.Message); }

            panel = null;
            viewers.Clear();
        }

        /// <summary>The HUD went off on a config reload: every pending pick goes back to the WASD menu.</summary>
        public static void Disable()
        {
            var pending = viewers
                .Where(kv => kv.Value.Pick != null)
                .Select(kv => (Slot: kv.Key, Pick: kv.Value.Pick!))
                .ToList();

            Unload();

            foreach (var (slot, pick) in pending)
            {
                var player = CounterStrikeSharp.API.Utilities.GetPlayerFromSlot(slot);
                if (player is not { IsValid: true }) continue;

                ConcurrentBag<(string, string)> items = [.. pick.Choices.Select(c => (c.Label, c.Value))];
                (string, string, bool)? last = pick.Last == null ? null : (pick.Last.Label, pick.Last.Value, !pick.Last.Keeps);
                SkillUtils.CreateMenu(player, items, last);
            }
        }

        public static void OnDisconnect(int slot)
        {
            viewers.Remove(slot);
            if (panel == null) return;

            closing = true;
            try { panel.Close(slot); }
            catch { /* the slot is gone either way */ }
            finally { closing = false; }
        }

        /// <summary>Map change: every pick ended with the map.</summary>
        public static void OnMapStart() => viewers.Clear();

        private static bool Usable(CCSPlayerController player) =>
            HeroesHud.Settings.PickMenu && HeroesHud.Draws(player) && EnsurePanel();

        // ------------------------------------------------------------------ what SkillUtils calls

        /// <summary>
        /// A skill opened its pick. True when this menu shows it; false and SkillUtils opens the WASD menu.
        /// A pick already pending is replaced (Gambler's reroll, Inheritance when another teammate falls).
        /// </summary>
        public static bool TryOpen(CCSPlayerController player, jSkill_PlayerInfo info, IEnumerable<(string, string)> items, (string, string, bool)? last)
        {
            if (!Usable(player)) return false;

            var v = ViewerOf(player.Slot);
            var pick = new Pick { Info = info, Skill = info.Skill };
            SetChoices(player, pick, items);
            if (last is { } l)
                pick.Last = new Choice(l.Item1, l.Item2, !l.Item3, true);

            v.Pick = pick;
            v.PickShown = true;
            v.Browse = false;
            v.Page = 0;

            if (Show(player, v)) return true;

            // Not drawn: the WASD menu takes this pick, and any older one goes with it
            v.Pick = null;
            v.PickShown = false;
            return false;
        }

        /// <summary>A pick is pending, on screen or closed with the X.</summary>
        public static bool Has(CCSPlayerController? player) =>
            player is { IsValid: true } && viewers.TryGetValue(player.Slot, out var v) && v.Pick != null;

        /// <summary>The pick is on screen right now.</summary>
        public static bool IsShown(CCSPlayerController? player) =>
            player is { IsValid: true } && panel != null && viewers.TryGetValue(player.Slot, out var v)
            && v.Pick != null && v.PickShown && panel.IsOpenFor(player);

        /// <summary>A pick is pending but closed: the card shows the key that opens it.</summary>
        public static bool IsParked(CCSPlayerController? player) =>
            player is { IsValid: true } && viewers.TryGetValue(player.Slot, out var v) && v.Pick != null && !v.PickShown;

        /// <summary>The skill's own refresh (every half second while its menu is up). False: not ours.</summary>
        public static bool Update(CCSPlayerController player, IEnumerable<(string, string)> items)
        {
            if (!viewers.TryGetValue(player.Slot, out var v) || v.Pick == null) return false;

            SetChoices(player, v.Pick, items);
            if (Visible(v) && !v.Browse && panel != null && panel.IsOpenFor(player))
                Draw(player, v);
            return true;
        }

        /// <summary>The skill closed its pick (used, round end, death, the time limit). False: there was none.</summary>
        public static bool Close(CCSPlayerController? player)
        {
            if (player is not { IsValid: true } || !viewers.TryGetValue(player.Slot, out var v) || v.Pick == null) return false;

            v.Pick = null;
            v.PickShown = false;
            if (!v.Browse) Hide(player);
            return true;
        }

        /// <summary>
        /// The skill key (or css_useSkill) while a pick is pending: opens it again, or closes it when it is open.
        /// False: nothing pending, the key does what it did.
        /// </summary>
        public static bool OnSkillKey(CCSPlayerController player)
        {
            if (!viewers.TryGetValue(player.Slot, out var v) || v.Pick == null) return false;

            if (v.PickShown && !v.Browse && panel != null && panel.IsOpenFor(player))
            {
                v.PickShown = false;
                Hide(player);
                return true;
            }

            if (!Usable(player))
            {
                // The panel can't reach them any more: the pick goes back to the WASD menu
                var pick = v.Pick;
                v.Pick = null;
                ConcurrentBag<(string, string)> items = [.. pick.Choices.Select(c => (c.Label, c.Value))];
                (string, string, bool)? last = pick.Last == null ? null : (pick.Last.Label, pick.Last.Value, !pick.Last.Keeps);
                SkillUtils.CreateMenu(player, items, last);
                return true;
            }

            v.PickShown = true;
            v.Browse = false;
            v.Page = 0;
            if (!Show(player, v)) v.PickShown = false;
            return true;
        }

        /// <summary>!skills: opens the list, or closes it when it is open. False: the HUD can't, the old menu opens.</summary>
        public static bool ToggleSkills(CCSPlayerController player)
        {
            if (player is not { IsValid: true, IsBot: false }) return false;

            if (viewers.TryGetValue(player.Slot, out var open) && open.Browse && panel != null && panel.IsOpenFor(player))
            {
                open.Browse = false;
                Hide(player);
                return true;
            }

            if (!Usable(player)) return false;

            var v = ViewerOf(player.Slot);
            v.Browse = true;
            v.PickShown = false;
            v.Page = 0;
            v.BrowseSkills = [.. SkillData.GetSnapshot()
                .Where(s => s.Skill != Skills.None)
                .Select(s => s.Skill)
                .Distinct()
                .OrderBy(s => player.GetSkillName(s), StringComparer.CurrentCultureIgnoreCase)];

            if (Show(player, v)) return true;
            v.Browse = false;
            return false;
        }

        /// <summary>One HUD frame (HeroesHud.Update): HP and alive state, every <see cref="Every"/>.</summary>
        public static void Frame(CCSPlayerController player, DateTime now)
        {
            if (panel == null || !viewers.TryGetValue(player.Slot, out var v) || !Visible(v)) return;
            if (now < v.NextDraw || !panel.IsOpenFor(player)) return;

            // Every pick needs its holder alive, and a cursor is no use to a dead player. The skill closes the pick
            // itself on death; one that doesn't keeps it pending, as the WASD menu did, just off screen.
            if (!v.Browse && v.Pick != null && !IsAlive(PlayerManager.GetPlayerEvent(player) ?? player))
            {
                v.PickShown = false;
                Hide(player);
                return;
            }

            Draw(player, v);
        }

        // ------------------------------------------------------------------ open, hide, events

        private static bool Show(CCSPlayerController player, Viewer v)
        {
            if (panel == null) return false;

            if (!panel.IsOpenFor(player))
            {
                try
                {
                    panel.Open(player);
                }
                catch (Exception ex)
                {
                    plugin?.Logger.LogWarning("Heroes pick menu could not open for {Player}: {Message}", player.PlayerName, ex.Message);
                }

                if (!panel.IsOpenFor(player)) return false;

                // Open took off every class this handle had turned on: the next draw writes everything
                v.W.Reset();
            }

            Draw(player, v);
            return true;
        }

        private static void Hide(CCSPlayerController player)
        {
            if (panel == null || !panel.IsOpenFor(player)) return;

            closing = true;
            try { panel.Close(player); }
            finally { closing = false; }
        }

        private static void OnEvent(PanelEvent e)
        {
            if (e.Player is not { IsValid: true } player) return;

            switch (e.Action)
            {
                case PanelAction.Close:
                    // Ours (Hide) is already accounted for. Anything else is the X, or the library letting go:
                    // the pick stays pending and the key opens it again.
                    if (closing || !viewers.TryGetValue(player.Slot, out var closed)) return;
                    closed.PickShown = false;
                    closed.Browse = false;
                    return;

                case PanelAction.Restored:
                    if (!viewers.TryGetValue(player.Slot, out var restored) || !Visible(restored))
                    {
                        Hide(player);
                        return;
                    }

                    restored.W.Reset();
                    Draw(player, restored);
                    return;

                case PanelAction.Button:
                    if (viewers.TryGetValue(player.Slot, out var viewer) && Visible(viewer))
                        Click(player, viewer, e.ElementId);
                    return;
            }
        }

        private static void Click(CCSPlayerController player, Viewer v, string id)
        {
            int pages = Pages(v.Browse ? v.BrowseSkills.Count : v.Rows.Count);
            if (id == "hm_prev" || id == "hm_next")
            {
                int page = Math.Clamp(v.Page + (id == "hm_next" ? 1 : -1), 0, pages - 1);
                if (page == v.Page) return;
                v.Page = page;
                Draw(player, v);
                return;
            }

            if (!id.StartsWith("hm_r", StringComparison.Ordinal) || !int.TryParse(id.AsSpan(4), out int slot)) return;
            if (slot < 0 || slot >= RowSlots) return;

            // !skills keeps only the page it drew; a pick keeps every row
            int index = v.Browse ? slot : v.Page * RowSlots + slot;
            if (index >= v.Rows.Count) return;

            var row = v.Rows[index];
            if (!row.Enabled) return;

            if (v.Browse)
            {
                if (!Enum.TryParse<Skills>(row.Value, out var skill)) return;
                // What the old !skills menu printed. The list stays open to read another one.
                SkillUtils.PrintToChat(player, $"{ChatColors.DarkRed}{player.GetSkillName(skill)}{ChatColors.Lime}: {player.GetSkillDescription(skill)}", border: "");
                return;
            }

            var pick = v.Pick;
            if (pick == null) return;

            // The click came from a client: the value has to be one the skill offers right now
            var choice = row.IsLast ? pick.Last : pick.Choices.FirstOrDefault(c => c.Value == row.Value);
            if (choice == null || choice.Value != row.Value) return;

            // What the WASD selection runs: the skill's TypeSkill with the value, then the menu closes
            jRandomSkills.Instance.SkillAction(pick.Info.Skill.ToString(), "TypeSkill", [player, new[] { choice.Value }]);

            if (!choice.Keeps && ReferenceEquals(v.Pick, pick))
                Close(player);
        }

        // ------------------------------------------------------------------ building the rows

        private static void SetChoices(CCSPlayerController player, Pick pick, IEnumerable<(string, string)> items)
        {
            pick.Choices = [.. items.Select(i => new Choice(i.Item1 ?? "", i.Item2 ?? "", false, false))];
            pick.AliveWhenListed.Clear();

            // Nobody left to list (everyone the skill could pick died): keep what the menu was, the rest greyed out
            if (pick.Choices.Count == 0) return;

            int players = 0;
            foreach (var c in pick.Choices)
            {
                if (!uint.TryParse(c.Value, out var key)) continue;
                players++;
                if (PlayerManager.GetController(key) is { } target && IsAlive(target))
                    pick.AliveWhenListed.Add(c.Value);
            }

            // Every choice a player, and all of them alive: the skill picks among the living (not Inheritance,
            // which lists the fallen), so the rest of that side can be shown, greyed out
            pick.WithOthers = players > 0 && players == pick.Choices.Count && pick.AliveWhenListed.Count == players;
        }

        private static bool IsAlive(CCSPlayerController p)
        {
            var pawn = p.PlayerPawn?.Value;
            return pawn != null && pawn.IsValid && pawn.LifeState == (byte)LifeState_t.LIFE_ALIVE && pawn.Health > 0;
        }

        private static CCSPlayerController? Target(string value) =>
            uint.TryParse(value, out var key) ? PlayerManager.GetController(key) : null;

        private static Side SideOf(CCSPlayerController viewer, Pick pick)
        {
            if (pick.Choices.Count == 0) return pick.Side;

            var me = PlayerManager.GetPlayerEvent(viewer) ?? viewer;
            bool enemy = false, mate = false;
            foreach (var c in pick.Choices)
            {
                if (!uint.TryParse(c.Value, out _)) return pick.Side = Side.Options;
                if (Target(c.Value) is not { } t) continue;
                if (t.Team == me.Team) mate = true;
                else enemy = true;
            }

            return pick.Side = enemy && !mate ? Side.Enemies : mate && !enemy ? Side.Teammates : Side.Everyone;
        }

        private static List<Row> PickRows(CCSPlayerController viewer, Pick pick, Side side)
        {
            bool illiterate = Illiterate.CheckIlliterateSkill(viewer);
            var rows = new List<Row>();

            foreach (var c in pick.Choices)
                rows.Add(side == Side.Options ? OptionRow(viewer, c) : ListedRow(viewer, pick, c));

            if (side != Side.Options && pick.WithOthers)
                rows.AddRange(Others(viewer, pick, side));

            // Pickable first, then the rest, each by name: a refresh never shuffles the list
            rows = [.. rows.OrderBy(r => r.Enabled ? 0 : 1).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];

            if (pick.Last != null)
                rows.Add(LastRow(pick.Last));

            if (illiterate)
            {
                foreach (var r in rows)
                {
                    r.Name = Illiterate.GetRandomText(r.Name) ?? r.Name;
                    r.Note = Illiterate.GetRandomText(r.Note) ?? r.Note;
                }
            }

            return rows;
        }

        /// <summary>"‪{name}‬ : {extra}", the way the skills write a name with something after it.</summary>
        private static (string Name, string Note) Split(string label)
        {
            if (label.Length > 0 && label[0] == '‪')
            {
                int end = label.IndexOf('‬');
                if (end > 0)
                {
                    string name = label[1..end];
                    string rest = label[(end + 1)..].Trim();
                    if (rest.StartsWith(':')) rest = rest[1..].Trim();
                    return (name, rest);
                }
            }

            return (label.Replace("‪", "").Replace("‬", ""), "");
        }

        [GeneratedRegex(@"^\d+\s*HP$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex HpNote();

        private static Row ListedRow(CCSPlayerController viewer, Pick pick, Choice c)
        {
            var (name, note) = Split(c.Label);
            // Life Swap writes the HP after the name; the row has its own
            if (HpNote().IsMatch(note)) note = "";

            var row = new Row { Value = c.Value, Player = true, Name = name, Note = note };
            var target = Target(c.Value);
            if (target == null)
            {
                // Left since the skill listed them: TypeSkill would refuse it
                row.Enabled = false;
                row.Dead = true;
                row.Step = 0;
                row.Hp = "-";
                row.Note = T(viewer, "hud_pick_left", "Left the game");
                return row;
            }

            FillPlayer(viewer, row, target);
            // Listed alive and dead now: TypeSkill would refuse it, so it greys out until the skill's next refresh
            if (row.Dead && pick.AliveWhenListed.Contains(c.Value))
                row.Enabled = false;
            return row;
        }

        private static void FillPlayer(CCSPlayerController viewer, Row row, CCSPlayerController target)
        {
            row.Team = target.Team switch { CsTeam.Terrorist => "tm-2", CsTeam.CounterTerrorist => "tm-3", _ => "tm-0" };
            if (!IsAlive(target))
            {
                row.Dead = true;
                row.Step = 0;
                row.Hp = T(viewer, "hud_pick_dead", "Dead");
                return;
            }

            int hp = Math.Max(0, target.PlayerPawn!.Value!.Health);
            int max = Math.Max(1, target.PlayerPawn.Value.MaxHealth);
            double f = Math.Clamp(hp / (double)Math.Max(max, 100), 0, 1);
            row.Hp = hp.ToString(CultureInfo.InvariantCulture);
            row.Step = hp > 0 ? Math.Max(1, (int)Math.Round(f * HpSteps, MidpointRounding.AwayFromZero)) : 0;
            row.Tone = f > 0.6 ? "t-hi" : f > 0.25 ? "t-mid" : "t-lo";
        }

        /// <summary>The rest of the side the skill picks from, greyed out, with the reason where it is known.</summary>
        private static IEnumerable<Row> Others(CCSPlayerController viewer, Pick pick, Side side)
        {
            var me = PlayerManager.GetPlayerEvent(viewer) ?? viewer;
            uint self = me.Key();
            var listed = pick.Choices.Select(c => c.Value).ToHashSet();

            foreach (var raw in PlayerManager.GetTickPlayers())
            {
                var p = PlayerManager.GetPlayerEvent(raw);
                if (p is not { IsValid: true } || p.IsHLTV) continue;
                if (p.Team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist)) continue;

                uint key = p.Key();
                if (key == self || listed.Contains(key.ToString(CultureInfo.InvariantCulture))) continue;
                if (side == Side.Enemies && p.Team == me.Team) continue;
                if (side == Side.Teammates && p.Team != me.Team) continue;

                var row = new Row { Value = key.ToString(CultureInfo.InvariantCulture), Player = true, Enabled = false, Name = p.PlayerName };
                FillPlayer(viewer, row, p);
                row.Note = row.Dead ? "" : WhyNot(viewer, pick.Skill, p, key);
                yield return row;
            }
        }

        private static string WhyNot(CCSPlayerController viewer, Skills skill, CCSPlayerController target, uint key)
        {
            if (SkillUtils.IsCurseSkill(skill) && SkillUtils.CurseLimitEnabled && !SkillUtils.CanCurse(key))
                return T(viewer, "hud_pick_cursed", "Already cursed");

            var info = PlayerManager.GetPlayer(target);
            if (skill == Skills.Giant && info?.Skill == Skills.Chicken)
                return T(viewer, "hud_pick_immune", "Immune");

            if (skill is Skills.Thief or Skills.Deactivator or Skills.Duplicator
                && (info == null || SkillData.GetInfo(info.Skill) == null))
                return T(viewer, "hud_pick_noskill", "No skill to take");

            return T(viewer, "hud_pick_cant", "Can't be picked");
        }

        private static Row OptionRow(CCSPlayerController viewer, Choice c)
        {
            var row = new Row { Value = c.Value, Option = true, Name = Split(c.Label).Name };

            // Gambler offers skills: each in its own colour, with what it does
            if (Enum.TryParse<Skills>(c.Value, true, out var skill) && skill != Skills.None && SkillData.GetInfo(skill) != null)
            {
                row.Hue = SkillCatalog.HueClass(SkillCatalog.ColorOf(skill));
                row.Note = SkillCatalog.Description(viewer, skill);
            }

            return row;
        }

        /// <summary>WASD's last element: Zone Reaper's second site, Gambler's reroll ("#rrggbb|" in front colours it).</summary>
        private static Row LastRow(Choice c)
        {
            string text = c.Label;
            string? hue = null;
            if (text.Length > 8 && text[0] == '#' && text[7] == '|')
            {
                hue = SkillCatalog.HueClass(text[..7]);
                text = text[8..];
            }

            return new Row
            {
                Value = c.Value,
                Option = true,
                IsLast = true,
                Act = c.Keeps,
                Keeps = c.Keeps,
                Name = Split(text).Name,
                Hue = hue,
            };
        }

        private static List<Row> BrowseRows(CCSPlayerController viewer, Viewer v)
        {
            var rows = new List<Row>(RowSlots);
            int from = v.Page * RowSlots;
            for (int i = from; i < Math.Min(from + RowSlots, v.BrowseSkills.Count); i++)
            {
                var skill = v.BrowseSkills[i];
                rows.Add(new Row
                {
                    Value = skill.ToString(),
                    Option = true,
                    Name = viewer.GetSkillName(skill),
                    Note = SkillCatalog.Description(viewer, skill),
                    Hue = SkillCatalog.HueClass(SkillCatalog.ColorOf(skill)),
                });
            }

            return rows;
        }

        private static int Pages(int rows) => Math.Max(1, (rows + RowSlots - 1) / RowSlots);

        // ------------------------------------------------------------------ drawing

        private static void Draw(CCSPlayerController p, Viewer v)
        {
            if (panel == null) return;
            v.NextDraw = DateTime.Now + Every;
            var w = v.W;

            string what, skillName, hint, foot, hue, kind;
            int total;
            List<Row> page;

            if (v.Browse)
            {
                total = v.BrowseSkills.Count;
                v.Page = Math.Clamp(v.Page, 0, Pages(total) - 1);
                page = BrowseRows(p, v);
                v.Rows = page;

                what = T(p, "hud_skills_what", "{0} skills", total);
                skillName = p.GetTranslationWithoutIlliterate("skills_menu");
                hint = T(p, "hud_skills_hint", "Click a skill for its full description in chat.");
                foot = T(p, "hud_skills_foot", "!skills again closes it");
                hue = "h-white";
                kind = "k-skills";
            }
            else
            {
                var pick = v.Pick!;
                var side = SideOf(p, pick);
                v.Rows = PickRows(p, pick, side);
                total = v.Rows.Count;
                v.Page = Math.Clamp(v.Page, 0, Pages(total) - 1);
                page = [.. v.Rows.Skip(v.Page * RowSlots).Take(RowSlots)];

                what = side switch
                {
                    Side.Enemies => T(p, "hud_pick_enemy", "Pick an enemy"),
                    Side.Teammates => T(p, "hud_pick_teammate", "Pick a teammate"),
                    Side.Everyone => T(p, "hud_pick_player", "Pick a player"),
                    _ when pick.Skill == Skills.AreaReaper => T(p, "hud_pick_site", "Pick a bombsite"),
                    _ when v.Rows.Any(r => r.Hue != null && !r.Act) => T(p, "hud_pick_skill", "Pick a skill"),
                    _ => T(p, "hud_pick_one", "Pick one"),
                };
                skillName = p.GetSkillName(pick.Skill);
                hint = SelectInfo(p, pick.Skill);
                string key = SkillCatalog.KeyLabel();
                foot = key.Length > 0 ? T(p, "hud_pick_foot", "Closed it? {0} opens it again", key) : "";
                hue = SkillCatalog.HueClass(SkillCatalog.ColorOf(pick.Skill));
                kind = SkillCatalog.KindOf(pick.Skill) == SkillKind.Bomb ? "k-bomb" : "k-pick";
            }

            int pages = Pages(total);
            bool center = string.Equals(HeroesHud.Settings.MenuPosition, "Center", StringComparison.OrdinalIgnoreCase);

            w.One(p, Card, "h", hue, HueClasses);
            w.One(p, Card, "k", kind, KindClasses);
            w.One(p, Card, "pos", center ? "p-center" : "p-left", PlaceClasses);
            w.Cls(p, Card, "nohint", hint.Length == 0);
            w.Cls(p, Card, "nofoot", foot.Length == 0);
            w.Cls(p, Card, "one", pages <= 1);
            w.Cls(p, Card, "empty", total == 0);

            w.Var(p, "hm_what", what);
            w.Var(p, "hm_skill", skillName);
            w.Var(p, "hm_hint", hint);
            w.Var(p, "hm_foot", foot);
            w.Var(p, "hm_page", $"{v.Page + 1} / {pages}");
            w.Var(p, "hm_empty", total == 0 ? T(p, "hud_pick_nobody", "Nobody can be picked right now.") : "");
            w.Cls(p, "hm_prev", "off", v.Page <= 0);
            w.Cls(p, "hm_next", "off", v.Page >= pages - 1);

            for (int i = 0; i < RowSlots; i++)
            {
                string id = RowIds[i];
                if (i >= page.Count)
                {
                    w.Cls(p, id, "hidden", true);
                    continue;
                }

                var r = page[i];
                w.Var(p, $"{id}_n", r.Name);
                w.Var(p, $"{id}_s", r.Note);
                w.Var(p, $"{id}_hp", r.Hp);
                w.Cls(p, id, "off", !r.Enabled);
                w.Cls(p, id, "opt", r.Option);
                w.Cls(p, id, "act", r.Act);
                w.Cls(p, id, "dead", r.Dead);
                w.Cls(p, id, "nonote", r.Note.Length == 0);
                w.One(p, id, "h", r.Hue, HueClasses);
                w.One(p, $"{id}_tm", "tm", r.Player ? r.Team : "tm-0", TeamClasses);
                w.One(p, $"{id}_bar", "hp", HpClasses[Math.Clamp(r.Step, 0, HpSteps)], HpClasses);
                w.One(p, $"{id}_bar", "tone", r.Tone, ToneClasses);
                w.Cls(p, id, "hidden", false);
            }
        }

        /// <summary>The skill's own "Select ...:" line, without the colon. Scrambled under Illiterate, as it was.</summary>
        private static string SelectInfo(CCSPlayerController p, Skills skill)
        {
            string key = $"{SkillNames.Get(skill).ToLowerInvariant()}_select_info";
            string text = p.GetTranslation(key);
            if (string.IsNullOrWhiteSpace(text) || text == key) return "";
            return text.Trim().TrimEnd(':').TrimEnd();
        }

        /// <summary>A phrase from the language files, or the English fallback when the server's files do not have it yet.</summary>
        private static string T(CCSPlayerController p, string key, string fallback, params object[] args)
        {
            var text = p.GetTranslationWithoutIlliterate(key, args);
            if (text != key) return text;
            try { return args.Length == 0 ? fallback : string.Format(CultureInfo.InvariantCulture, fallback, args); }
            catch (FormatException) { return fallback; }
        }

        // ------------------------------------------------------------------ writes: only what changed

        /// <summary>
        /// Per-viewer write cache. After <see cref="Reset"/> it knows nothing, so the next draw writes every class
        /// (each option of a group, on or off) and every variable: right after an Open, which takes the handle's
        /// classes off, and after a Restored, where a kept entity may still wear the old ones.
        /// </summary>
        private sealed class Writer
        {
            private readonly Dictionary<string, string> vars = [];
            private readonly Dictionary<(string, string), bool> classes = [];
            private readonly Dictionary<(string, string), string?> groups = [];

            public void Reset()
            {
                vars.Clear();
                classes.Clear();
                groups.Clear();
            }

            public void Var(CCSPlayerController p, string name, string value)
            {
                if (vars.TryGetValue(name, out var old) && old == value) return;
                panel!.SetVariableFor(p, name, value);
                vars[name] = value;
            }

            public void Cls(CCSPlayerController p, string id, string cls, bool on)
            {
                var key = (id, cls);
                if (classes.TryGetValue(key, out var old) && old == on) return;
                if (panel!.TrySetClassFor(p, id, cls, on))
                    classes[key] = on;
            }

            /// <summary>One class of a group on (or none). Unknown: every other option is written off first.</summary>
            public void One(CCSPlayerController p, string id, string group, string? cls, IReadOnlyList<string> options)
            {
                var key = (id, group);
                if (groups.TryGetValue(key, out var current))
                {
                    if (current == cls) return;
                    if (current != null) Cls(p, id, current, false);
                }
                else
                {
                    foreach (var option in options)
                        if (option != cls) Cls(p, id, option, false);
                }

                if (cls != null) Cls(p, id, cls, true);
                groups[key] = cls;
            }
        }
    }
}
