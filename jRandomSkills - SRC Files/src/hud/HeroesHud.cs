using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using PanoramaManager;
using src.player;
using src.player.skills;

namespace src.utils
{
    /// <summary>
    /// Drives heroes_hud.xml (hud/panorama in this repo, shipped in the workshop addon): the skill card, the
    /// spectator line, the alert banner and the Coins line. Replaces the center HTML the plugin printed every
    /// HUD frame, its center alerts and the Coins center text. With Hud.Enabled = false, or for a player the
    /// panel cannot be opened for, every one of those comes back exactly as it was.
    ///
    /// <para><b>One layout, opened once per player and left open.</b> Each panel inside has its own "show", and
    /// "hidden" (collapse) while not in use; it is un-collapsed a moment before it shows so it always slides in
    /// from its resting place.</para>
    ///
    /// <para><b>Everything is drawn from state, every HUD frame, and only differences are written.</b> The card
    /// reads the skill player and its <see cref="SkillHudState"/>; the per-viewer cache below remembers what the
    /// client has, so a frame that changes nothing writes nothing. PanoramaManager rebuilds the panel after a
    /// round restart or a respawn (PanelAction.Restored) from the layout as authored, so the cache goes back to
    /// the authored classes and the next frame redraws everything.</para>
    ///
    /// <para><b>Every bar is a clip ladder with one fixed glide.</b> heroes_hud.css carries at-0..at-50 (2% a step)
    /// on a clip with a 0.3s linear transition that no class changes. A value (fuel) names its step; a timed run
    /// (cooldowns, the draw, planting, the alert) gets the step for where it will be one glide from now, every
    /// <see cref="BarEvery"/>. That replaced runs as one long transition (snap, then a dur-* class and run-fill /
    /// run-drain 0.35s later), which did not draw in game: nothing has shown a class-swapped transition-duration
    /// working there.</para>
    /// </summary>
    internal static class HeroesHud
    {
        private const string Layout = "panorama/layout/custom_game/heroes_hud.vxml_c";
        private const string Root = "HeroesRoot";

        private const string CardId = "hr_card";
        private const string SpecId = "hr_spec";
        private const string AlertId = "hr_alert";
        private const string CoinsId = "hr_coins";

        /// <summary>Un-collapse first, then "show": a transition needs the client to have seen the panel at rest.</summary>
        private static readonly TimeSpan Entry = TimeSpan.FromSeconds(0.06);

        /// <summary>The exit transition, before a panel collapses.</summary>
        private static readonly TimeSpan Exit = TimeSpan.FromSeconds(0.35);

        /// <summary>How long an emptied alert bar takes to be full again before the next alert shows: a little over the glide.</summary>
        private static readonly TimeSpan BarWait = TimeSpan.FromSeconds(0.35);

        /// <summary>
        /// The .hr-bar and .al-bar transition-duration in heroes_hud.css. A run's every step is aimed this far ahead
        /// of the clock, so the fill arrives where the run is instead of trailing it, and ends as the run does.
        /// </summary>
        private static readonly TimeSpan Glide = TimeSpan.FromSeconds(0.3);

        /// <summary>How often a running bar gets its next step. Under <see cref="Glide"/>, so the fill never stops
        /// between steps; the HUD frame is every 4 ticks.</summary>
        private static readonly TimeSpan BarEvery = TimeSpan.FromSeconds(0.2);

        /// <summary>The bars, for the scrub on a redraw.</summary>
        private static readonly string[] BarIds = ["hs_bar", "sp_bar", "al_bar"];

        private const double AlertSeconds = 5.0;
        private const double CoinSeconds = 2.6;
        private const double CoinMergeSeconds = 2.0;
        private const double RepeatAlertSeconds = 4.0;
        private const int BarSteps = 50;
        private const int PipSlots = 5;

        private static BasePlugin? plugin;
        private static PanelHandle? panel;
        private static bool failed;
        private static readonly Dictionary<int, View> views = [];
        private static readonly Dictionary<int, DateTime> nextOpenTry = [];
        private static readonly Config.HudSettings defaults = new();

        public static Config.HudSettings Settings => Config.LoadedConfig.Hud ?? defaults;

        public static bool Enabled => Settings.Enabled && !failed;

        // ------------------------------------------------------------------ lifecycle

        public static void Load(BasePlugin owner)
        {
            plugin = owner;
            if (Settings.Enabled)
                EnsurePanel();
        }

        public static void Unload()
        {
            try
            {
                panel?.Dispose();
                if (Panorama.IsInitialised)
                    Panorama.Shutdown();
            }
            catch (Exception ex)
            {
                plugin?.Logger.LogWarning("Heroes HUD shutdown: {Message}", ex.Message);
            }

            panel = null;
            views.Clear();
            nextOpenTry.Clear();
        }

        /// <summary>Spawns the layout once. A second Spawn would orphan the first entity, so this is guarded.</summary>
        private static bool EnsurePanel()
        {
            if (panel != null) return true;
            if (failed || plugin == null) return false;

            try
            {
                Panorama.Init(plugin);
                panel = Panorama.Spawn(Layout, new LayoutContract
                {
                    // Unique server-wide: dialog variables are keyed by panel id
                    RootPanelId = Root,
                    RevealClass = "show",
                    RowCount = 0,
                    CaptureInput = false,
                    HideHud = HideHudFlags.None,
                    SharedText = false,
                    SpectatingMessage = null,
                });
                panel.OnEvent += OnPanelEvent;
                plugin.Logger.LogInformation("Heroes HUD ready ({Layout})", Layout);
                return true;
            }
            catch (Exception ex)
            {
                // The plugin works without its HUD: everything falls back to the center HTML and alerts
                failed = true;
                panel = null;
                plugin.Logger.LogWarning("Heroes HUD failed to start, using center HTML instead: {Message}", ex.Message);
                return false;
            }
        }

        private static void OnPanelEvent(PanelEvent e)
        {
            if (e.Player is not { IsValid: true }) return;

            if (e.Action == PanelAction.Restored)
            {
                // A kept entity still wears the bar steps the old view turned on, and the new view does not know
                // them: take them off, or the stylesheet paints the higher of two steps. On a rebuilt one, a no-op.
                if (panel != null && views.TryGetValue(e.Player.Slot, out var old))
                {
                    foreach (var id in BarIds)
                    {
                        if (old.Groups.TryGetValue((id, "at"), out var step) && step != null)
                            panel.SetClassFor(e.Player, id, step, false);
                    }
                }

                views[e.Player.Slot] = new View();
            }
            else if (e.Action == PanelAction.Close)
                views.Remove(e.Player.Slot);
        }

        public static void OnDisconnect(int slot)
        {
            views.Remove(slot);
            nextOpenTry.Remove(slot);
            try { panel?.Close(slot); }
            catch { /* the slot is gone either way */ }
        }

        /// <summary>Hud.Enabled went false on a config reload: the panels go, the center HTML comes back.</summary>
        private static void CloseAll()
        {
            try
            {
                panel?.Dispose();
            }
            catch (Exception ex)
            {
                plugin?.Logger.LogWarning("Heroes HUD close: {Message}", ex.Message);
            }

            panel = null;
            views.Clear();
            nextOpenTry.Clear();
        }

        /// <summary>Map change: every stored time is meaningless on the next map.</summary>
        public static void OnMapStart()
        {
            views.Clear();
            nextOpenTry.Clear();
        }

        private static bool IsHuman(CCSPlayerController? player) =>
            player is { IsValid: true, IsBot: false, IsHLTV: false } && player.Connected == PlayerConnectedState.Connected;

        /// <summary>
        /// True when the HUD draws for this player, opening their panel first if it is not open yet. False means
        /// the caller prints what the plugin printed before.
        /// </summary>
        public static bool Draws(CCSPlayerController? player)
        {
            if (!Settings.Enabled && panel != null)
                CloseAll();

            if (!Enabled || player == null || !IsHuman(player) || !EnsurePanel()) return false;
            if (panel!.IsOpenFor(player)) return true;

            var now = DateTime.Now;
            if (nextOpenTry.TryGetValue(player.Slot, out var at) && now < at) return false;
            nextOpenTry[player.Slot] = now.AddSeconds(5);

            try
            {
                panel.Open(player);
            }
            catch (Exception ex)
            {
                plugin?.Logger.LogWarning("Heroes HUD could not open for {Player}: {Message}", player.PlayerName, ex.Message);
            }

            if (!panel.IsOpenFor(player)) return false;
            views[player.Slot] = new View();
            return true;
        }

        /// <summary>The panel is open for this player right now. No side effects: safe in a user message hook.</summary>
        public static bool IsDrawing(CCSPlayerController? player) =>
            Enabled && panel != null && player is { IsValid: true } && panel.IsOpenFor(player);

        /// <summary>The round's "Your skill" chat line, unless the card shows it (Hud.SkillChatLine keeps it).</summary>
        public static bool ReplacesChatLine(CCSPlayerController? player) => !Settings.SkillChatLine && IsDrawing(player);

        private static View ViewOf(CCSPlayerController player)
        {
            if (!views.TryGetValue(player.Slot, out var view))
                views[player.Slot] = view = new View();
            return view;
        }

        private static bool Cross => string.Equals(Settings.CardPosition, "Crosshair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Settings.CardPosition, "Cross", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ the frame

        /// <summary>One HUD frame for one player (PlayerOnTick, every 4 ticks), after <see cref="Draws"/> said yes.</summary>
        public static void Update(CCSPlayerController player, DateTime now)
        {
            if (panel == null) return;
            var v = ViewOf(player);

            var info = PlayerManager.GetPlayer(PlayerManager.GetPlayerEvent(player) ?? player);
            var rules = jRandomSkills.Instance?.GameRules;
            bool round = rules != null && !rules.WarmupPeriod && rules.GamePhase < 5;

            // The WASD menus are still center HTML and still step aside for other plugins' center HTML
            // (PlayerEvents.GetPrintToCenterHtml); this un-pauses them once that is over, as before.
            if (info != null && info.HideHUD < Server.TickCount && SkillUtils.HasMenu(player))
                SkillUtils.SetMenuPaused(player, false);

            bool card = false, spec = false;
            if (round && info != null && info.HideHUD != int.MaxValue && info.HudSuppressedUntil <= now && SkillData.GetSnapshot().Length > 0)
            {
                if (player.PawnIsAlive)
                    card = DrawCard(player, v, info, now);
                else
                    spec = DrawSpec(player, v, info, now);
            }

            Drive(player, v, CardId, v.CardSlot, card, now);
            Drive(player, v, SpecId, v.SpecSlot, spec, now);

            if (v.CardSlot.Collapsed) ParkBar(player, v, "hs_bar", v.CardBar);
            if (v.SpecSlot.Collapsed) ParkBar(player, v, "sp_bar", v.SpecBar);
            if (!v.CardSlot.Shown) v.LastWasDrawing = false;

            DriveAlert(player, v, now);
            DriveCoins(player, v, now);
        }

        // ------------------------------------------------------------------ the skill card

        private static bool DrawCard(CCSPlayerController p, View v, jSkill_PlayerInfo info, DateTime now)
        {
            bool cross = Cross;

            // A WASD menu is center HTML, right where the card sits under the crosshair.
            if (cross && SkillUtils.HasMenu(p)) return false;

            One(p, v, CardId, "pos", cross ? "p-cross" : "p-bottom");

            if (info.IsDrawing)
            {
                DrawRoll(p, v, now);
                return true;
            }

            var (skill, via, off) = Display(info);
            bool live = info.Hud.Fresh || !string.IsNullOrEmpty(info.PrintHTML);
            if (info.SkillHudExpired < now && !live && !off) return false;

            var kind = SkillCatalog.KindOf(skill);
            string key = SkillCatalog.KeyLabel();
            bool showKey = SkillCatalog.UsesKey(kind) && key.Length > 0;
            bool illiterate = Illiterate.CheckIlliterateSkill(p);

            var m = Resolve(p, info, skill, kind, now);
            string name = p.GetSkillName(skill);
            string desc = SkillCatalog.Description(p, skill);
            string err = m.Err;

            if (off)
            {
                bool stolen = info.Hud.LostTo == Skills.Thief;
                m = new StateModel
                {
                    Cls = "s-err",
                    Label = T(p, stolen ? "hud_stolen" : "hud_disabled", stolen ? "Stolen" : "Disabled"),
                };
                err = p.GetTranslationWithoutIlliterate(stolen ? "thief_enemy_info" : "deactivator_enemy_info");
            }

            if (illiterate)
            {
                name = Illiterate.GetRandomText(name) ?? name;
                desc = Illiterate.GetRandomText(desc) ?? desc;
                err = Illiterate.GetRandomText(err) ?? err;
            }

            One(p, v, CardId, "h", SkillCatalog.HueClass(SkillCatalog.ColorOf(skill)));
            One(p, v, CardId, "k", SkillCatalog.KindClass(kind));
            One(p, v, CardId, "s", m.Cls);
            Cls(p, v, CardId, "off", off);
            Cls(p, v, CardId, "drawing", false);
            Cls(p, v, CardId, "fold", string.Equals(Settings.Description, "Fold", StringComparison.OrdinalIgnoreCase) && info.SkillDescriptionHudExpired < now);
            Cls(p, v, CardId, "track", m.Bar.Mode != BarMode.None && !off);
            Cls(p, v, CardId, "nokey", !showKey);
            Cls(p, v, CardId, "has-pips", m.PipsOnly);

            Var(p, v, "hs_eyebrow", T(p, "hud_your_skill", "Your skill"));
            Var(p, v, "hs_name", name);
            Var(p, v, "hs_desc", desc);
            Var(p, v, "hs_err", err);
            Chips(p, v, "hs_rar", "hs_team", "hs_via", skill, via);

            Var(p, v, "hs_key", key);
            Cls(p, v, "hs_key", "dim", m.KeyDim);
            Var(p, v, "hs_big", m.Big);
            Var(p, v, "hs_unit", m.Unit);
            One(p, v, "hs_big", "v", m.Tone);
            Var(p, v, "hs_tog", m.Tog);
            Cls(p, v, "hs_tog", "t-off", m.TogOff);
            Var(p, v, "hs_lbl", m.Label);
            Pips(p, v, m.PipsOn, m.PipsMax);
            DriveBar(p, v, "hs_bar", v.CardBar, off ? BarSpec.None : m.Bar, now);

            // Motion, only on a card already on screen: its entry is the slide.
            if (v.CardSlot.Shown)
            {
                if (skill != v.LastSkill || v.LastWasDrawing)
                    Kick(p, v, CardId, "land");
                else if (!off)
                {
                    bool pressed = v.LastState == HudState.Ready && m.State is HudState.Cooldown or HudState.Active or HudState.Busy or HudState.Progress or HudState.Error;
                    bool used = m.Count >= 0 && v.LastCount >= 0 && m.Count < v.LastCount;

                    if (showKey && (pressed || used))
                        Kick(p, v, "hs_key", "press");
                    else if (showKey && v.LastState == HudState.Cooldown && m.State == HudState.Ready)
                        Kick(p, v, "hs_key", "pop");

                    if (m.Count >= 0 && v.LastCount >= 0 && m.Count != v.LastCount)
                        Kick(p, v, "hs_big", "pop");

                    if (m.State == HudState.Toggle && v.LastState == HudState.Toggle && m.ToggleOn != v.LastToggle)
                        Kick(p, v, "hs_tog", "pop");
                }
            }

            v.LastSkill = skill;
            v.LastWasDrawing = false;
            v.LastState = off ? HudState.Auto : m.State;
            v.LastCount = m.Count;
            v.LastToggle = m.ToggleOn;
            return true;
        }

        /// <summary>The draw: names roll about ten a second and slow down until the skill lands.</summary>
        private static void DrawRoll(CCSPlayerController p, View v, DateTime now)
        {
            if (!v.LastWasDrawing)
            {
                v.RollStepMs = 95;
                v.NextRollAt = now;
                v.RollSkill = null;
            }

            var skills = SkillData.GetSnapshot();
            if (now >= v.NextRollAt && skills.Length > 0)
            {
                v.RollSkill = skills[Random.Shared.Next(skills.Length)];
                v.NextRollAt = now.AddMilliseconds(v.RollStepMs);
                v.RollStepMs = Math.Min(v.RollStepMs * 1.085, 900);
            }

            var roll = v.RollSkill?.Skill ?? Skills.None;
            string name = p.GetSkillName(roll);
            if (Illiterate.CheckIlliterateSkill(p))
                name = Illiterate.GetRandomText(name) ?? name;

            var start = Event.DrawStartedAt;
            var end = Event.DrawEndsAt;
            bool timed = end != DateTime.MaxValue && end > start && end > now;
            double left = timed ? (end - now).TotalSeconds : 0;

            One(p, v, CardId, "h", SkillCatalog.HueClass(v.RollSkill?.Color));
            One(p, v, CardId, "k", "k-draw");
            One(p, v, CardId, "s", "s-draw");
            Cls(p, v, CardId, "off", false);
            Cls(p, v, CardId, "drawing", true);
            Cls(p, v, CardId, "fold", false);
            Cls(p, v, CardId, "track", timed);
            Cls(p, v, CardId, "nokey", true);
            Cls(p, v, CardId, "has-pips", false);

            Var(p, v, "hs_eyebrow", T(p, "hud_drawing", "Drawing a skill"));
            Var(p, v, "hs_name", name);
            Var(p, v, "hs_desc", T(p, "hud_draw_desc", "Your skill for this round lands when the count hits zero."));
            Var(p, v, "hs_err", "");
            Var(p, v, "hs_big", timed ? Ceil(left) : "");
            Var(p, v, "hs_unit", timed ? "s" : "");
            One(p, v, "hs_big", "v", null);
            Var(p, v, "hs_lbl", T(p, "hud_drawing_lbl", "Drawing"));
            DriveBar(p, v, "hs_bar", v.CardBar, timed ? BarSpec.Run(start, end, true, "b-draw") : BarSpec.None, now);

            v.LastWasDrawing = true;
            v.LastSkill = Skills.None;
            v.LastState = HudState.Auto;
            v.LastCount = -1;
        }

        /// <summary>
        /// Which skill a card is about: the player's own, a copied one (Thief, Duplicator: via that skill), one an
        /// enemy took or switched off (struck through), or a pick skill already spent (Deactivator after use).
        /// </summary>
        private static (Skills Skill, Skills Via, bool Off) Display(jSkill_PlayerInfo info)
        {
            if (info.Skill == Skills.None)
            {
                if (info.Hud.LostSkill != Skills.None) return (info.Hud.LostSkill, Skills.None, true);
                return (info.SpecialSkill, Skills.None, false);
            }

            return (info.Skill, info.SpecialSkill == info.Skill ? Skills.None : info.SpecialSkill, false);
        }

        private static void Chips(CCSPlayerController p, View v, string rarId, string teamId, string? viaId, Skills skill, Skills via)
        {
            int rarity = skill == Skills.None ? 0 : SkillCatalog.RarityOf(skill);
            One(p, v, rarId, "rar", $"rar-{rarity}");
            Var(p, v, rarId, rarity switch
            {
                1 => T(p, "hud_rarity_uncommon", "Uncommon"),
                2 => T(p, "hud_rarity_rare", "Rare"),
                3 => T(p, "hud_rarity_epic", "Epic"),
                4 => T(p, "hud_rarity_legendary", "Legendary"),
                _ => "",
            });

            if (teamId.Length > 0)
            {
                int team = skill == Skills.None ? 0 : SkillCatalog.TeamOf(skill);
                One(p, v, teamId, "tm", $"tm-{team}");
                Var(p, v, teamId, team == 2 ? T(p, "hud_t_only", "T only") : team == 3 ? T(p, "hud_ct_only", "CT only") : "");
            }

            if (viaId != null)
            {
                Cls(p, v, viaId, "none", via == Skills.None);
                Var(p, v, viaId, via == Skills.None ? "" : T(p, "hud_via", "via {0}", p.GetSkillName(via)));
            }
        }

        private static void Pips(CCSPlayerController p, View v, int on, int max)
        {
            if (max > PipSlots) max = 0;
            for (int i = 0; i < PipSlots; i++)
            {
                string id = PipIds[i];
                Cls(p, v, id, "on", i < on);
                Cls(p, v, id, "cut", i >= max);
            }
        }

        private static readonly string[] PipIds = [.. Enumerable.Range(0, PipSlots).Select(i => $"hs_pip{i}")];

        // ------------------------------------------------------------------ state: what the right-hand block shows

        private sealed class StateModel
        {
            public HudState State = HudState.Auto;
            public string Cls = "s-none";
            public string Big = "", Unit = "", Label = "", Tog = "", Err = "";
            public string? Tone;
            public bool KeyDim, TogOff, ToggleOn, PipsOnly;
            public int PipsOn, PipsMax, Count = -1;
            public BarSpec Bar = BarSpec.None;
        }

        private static StateModel Resolve(CCSPlayerController viewer, jSkill_PlayerInfo info, Skills skill, SkillKind kind, DateTime now)
        {
            var m = new StateModel();
            var h = info.Hud;
            var state = h.Fresh && info.Skill == skill ? h.State : HudState.Auto;

            switch (state)
            {
                case HudState.Cooldown:
                {
                    double left = (h.EndsAt - now).TotalSeconds;
                    if (left <= 0) goto case HudState.Ready;
                    m.State = state;
                    m.Cls = "s-cool";
                    m.Big = Ceil(left);
                    m.Unit = "s";
                    m.Label = T(viewer, "hud_cooldown", "Cooldown");
                    m.Bar = BarSpec.Run(h.EndsAt.AddSeconds(-h.Seconds), h.EndsAt, true, null);
                    return m;
                }

                case HudState.Active:
                case HudState.Progress:
                {
                    double left = (h.EndsAt - now).TotalSeconds;
                    if (left <= 0) goto case HudState.Auto;
                    m.State = state;
                    m.Cls = "s-on";
                    m.Big = left.ToString("0.0", CultureInfo.InvariantCulture);
                    m.Unit = "s";
                    m.Label = Label(viewer, h, "hud_active", "Active");
                    m.Tone = "v-good";
                    m.Bar = BarSpec.Run(h.EndsAt.AddSeconds(-h.Seconds), h.EndsAt, state == HudState.Progress, "b-good");
                    return m;
                }

                case HudState.Busy:
                    m.State = state;
                    m.Cls = "s-on";
                    m.Label = Label(viewer, h, "hud_active", "Active");
                    m.Tone = "v-good";
                    return m;

                case HudState.Charges:
                {
                    m.State = state;
                    m.Count = h.Count;
                    m.Big = $"{h.Count}/{h.Max}";
                    if (h.Count <= 0)
                    {
                        m.Cls = "s-value";
                        m.Label = T(viewer, "hud_empty", "Empty");
                        m.Tone = "v-bad";
                        return m;
                    }

                    m.Cls = "s-charges";
                    m.PipsOn = h.Count;
                    m.PipsMax = h.Max;
                    if (h.EndsAt > now && h.Seconds > 0)
                    {
                        m.KeyDim = true;
                        m.Bar = BarSpec.Run(h.EndsAt.AddSeconds(-h.Seconds), h.EndsAt, true, null);
                    }
                    return m;
                }

                case HudState.Value:
                    m.State = state;
                    m.Cls = "s-value";
                    m.Big = h.Big;
                    m.Label = Label(viewer, h, "", "");
                    m.Tone = ToneClass(h.Tone);
                    if (h.Bar is { } bar)
                        m.Bar = BarSpec.Static(bar, h.Tone == HudTone.None ? null : "b-" + h.Tone.ToString().ToLowerInvariant());
                    if (h.Pips >= 0)
                    {
                        m.PipsOnly = true;
                        m.PipsOn = h.Pips;
                        m.PipsMax = h.PipsMax;
                    }
                    return m;

                case HudState.Toggle:
                    m.State = state;
                    m.Cls = "s-toggle";
                    m.ToggleOn = h.ToggleOn;
                    m.Tog = T(viewer, (h.ToggleOn ? h.ToggleOnKey : h.ToggleOffKey) ?? "", h.ToggleOn ? "ON" : "OFF");
                    m.TogOff = !h.ToggleOn;
                    m.Label = Label(viewer, h, "", "");
                    return m;

                case HudState.Error:
                    m.State = state;
                    m.Cls = "s-err";
                    m.Err = h.ErrorKey == null ? "" : viewer.GetTranslationWithoutIlliterate(h.ErrorKey, h.ErrorArgs);
                    return m;

                case HudState.Ready:
                    if (!SkillCatalog.UsesKey(kind)) goto case HudState.Auto;
                    m.State = HudState.Ready;
                    m.Cls = "s-ready";
                    if (SkillsInfo.GetValue<bool>(skill, "disableOnFreezeTime") && SkillUtils.IsFreezeTime())
                    {
                        m.KeyDim = true;
                        m.Label = T(viewer, "hud_freeze", "Freeze time");
                    }
                    else
                        m.Label = h.Fresh && h.State == HudState.Ready && h.LabelText != null ? h.LabelText : T(viewer, "hud_ready", "Ready");
                    return m;

                case HudState.Auto:
                default:
                {
                    if (SkillCatalog.UsesKey(kind) && skill == info.Skill)
                    {
                        state = HudState.Ready;
                        goto case HudState.Ready;
                    }

                    string? big = null, labelKey = null;
                    if (kind == SkillKind.Roll && SkillCatalog.TryRolled(skill, info.SkillChance, out var rolled, out var rollKey))
                    {
                        big = rolled;
                        labelKey = rollKey;
                    }
                    else if (h.FixedBig != null && skill == info.Skill)
                    {
                        big = h.FixedBig;
                        labelKey = h.FixedLabelKey;
                    }

                    if (big != null)
                    {
                        m.State = HudState.Value;
                        m.Cls = "s-value";
                        m.Big = big;
                        m.Label = labelKey == null ? "" : T(viewer, labelKey, "");
                    }
                    return m;
                }
            }
        }

        private static string Label(CCSPlayerController viewer, SkillHudState h, string fallbackKey, string fallback)
        {
            if (h.LabelText != null) return h.LabelText;
            var key = string.IsNullOrEmpty(h.LabelKey) ? fallbackKey : h.LabelKey;
            if (string.IsNullOrEmpty(key)) return fallback;
            return T(viewer, key, fallback, h.LabelArgs);
        }

        private static string? ToneClass(HudTone tone) => tone switch
        {
            HudTone.Good => "v-good",
            HudTone.Mid => "v-mid",
            HudTone.Bad => "v-bad",
            _ => null,
        };

        private static string Ceil(double seconds) =>
            Math.Max(0, (int)Math.Ceiling(seconds - 0.05)).ToString(CultureInfo.InvariantCulture);

        // ------------------------------------------------------------------ spectating

        private static bool DrawSpec(CCSPlayerController p, View v, jSkill_PlayerInfo viewerInfo, DateTime now)
        {
            if (p.Team is CsTeam.Spectator or CsTeam.None && Config.LoadedConfig.DisableSpectateHUD) return false;

            viewerInfo.HudOnDeathBlocked ??= AdminManager.PlayerHasPermissions(p, Config.LoadedConfig.DisableHUDOnDeathPermission);
            if (viewerInfo.HudOnDeathBlocked == true) return false;

            var target = p.Pawn.Value?.ObserverServices?.ObserverTarget?.Value;
            if (target == null || !target.IsValid) return false;

            var observed = PlayerManager.GetControllerByPawn(target.Handle);
            if (observed == null) return false;

            var observedEvent = PlayerManager.GetPlayerEvent(observed);
            if (observedEvent == null || !observedEvent.IsValid) return false;

            var info = PlayerManager.GetPlayer(observedEvent);
            if (info == null || info.IsDrawing) return false;

            var (skill, via, off) = Display(info);
            var kind = SkillCatalog.KindOf(skill);
            var m = Resolve(p, info, skill, kind, now);

            string who = info.PlayerName;
            if (who.Length > 18) who = $"{who[..17]}...";

            string big = "", label = "";
            string? tone = null;
            var bar = BarSpec.None;
            if (off)
            {
                big = T(p, info.Hud.LostTo == Skills.Thief ? "hud_stolen" : "hud_disabled", "Disabled");
                tone = "v-bad";
            }
            else
            {
                switch (m.Cls)
                {
                    case "s-ready":
                        big = T(p, "hud_ready", "Ready");
                        tone = "v-good";
                        break;
                    case "s-cool":
                        big = m.Big + m.Unit;
                        label = m.Label;
                        bar = m.Bar;
                        break;
                    case "s-on":
                    case "s-value":
                        big = m.Big + m.Unit;
                        label = m.Label;
                        tone = m.Tone;
                        bar = m.Bar;
                        break;
                    case "s-charges":
                        big = m.Big;
                        label = T(p, "hud_charges", "Charges");
                        bar = m.Bar;
                        break;
                    case "s-toggle":
                        big = m.Tog;
                        label = m.Label;
                        tone = m.ToggleOn ? "v-good" : "v-bad";
                        break;
                }
            }

            uint targetKey = info.PlayerKey;
            if (v.SpecSlot.Shown && targetKey != v.SpecTarget)
                Kick(p, v, SpecId, "swap");
            v.SpecTarget = targetKey;

            One(p, v, SpecId, "h", SkillCatalog.HueClass(SkillCatalog.ColorOf(skill)));
            One(p, v, SpecId, "k", SkillCatalog.KindClass(kind));
            Cls(p, v, SpecId, "track", bar.Mode != BarMode.None);

            Var(p, v, "sp_who", T(p, "hud_spec_who", "{0}'s skill", who));
            Var(p, v, "sp_name", p.GetSkillName(skill));
            Chips(p, v, "sp_rar", "", "sp_via", skill, via);
            Var(p, v, "sp_desc", SkillCatalog.Description(p, skill));

            if (big.Length == 0 && label.Length > 0)
            {
                big = label;
                label = "";
            }
            Cls(p, v, "sp_state", "none", big.Length == 0);
            if (big.Length > 0 && v.SpecBig.Length > 0 && big != v.SpecBig && m.Cls is "s-charges" or "s-toggle")
                Kick(p, v, "sp_big", "pop");
            v.SpecBig = big;
            Var(p, v, "sp_big", big);
            Var(p, v, "sp_lbl", label);
            One(p, v, "sp_big", "v", tone);
            DriveBar(p, v, "sp_bar", v.SpecBar, bar, now);
            return true;
        }

        // ------------------------------------------------------------------ alerts

        private sealed class AlertModel
        {
            public required string Key;
            public required string Accent;
            public required string Icon;
            public required string Eyebrow;
            public required string Title;
            public required string Sub;
            public string? Chip;
            public string? ChipHue;
            public int Num = -1;
            public string Unit = "";
            /// <summary>The number counts down to this moment (a bomb fuse), or MinValue.</summary>
            public DateTime CountTo = DateTime.MinValue;
        }

        private static bool Raise(CCSPlayerController viewer, Func<AlertModel> build)
        {
            if (!Draws(viewer)) return false;

            var v = ViewOf(viewer);
            var now = DateTime.Now;
            var model = build();

            // Already up, or waiting to come up: no restart. Zone Reaper re-sends every quarter second.
            if (v.AlertShown?.Key == model.Key && v.AlertPending == null && now < v.AlertUntil) return true;
            if (v.AlertPending?.Key == model.Key) return true;
            if (v.LastAlertKey == model.Key && (now - v.LastAlertEnd).TotalSeconds < RepeatAlertSeconds) return true;

            v.AlertPending = model;
            return true;
        }

        private static string SkillChip(CCSPlayerController viewer, Skills skill) => viewer.GetSkillName(skill);

        private static string SkillHue(Skills skill) => SkillCatalog.HueClass(SkillCatalog.ColorOf(skill));

        public static bool BombPlanted(CCSPlayerController viewer, Skills skill, int seconds) => Raise(viewer, () => new AlertModel
        {
            Key = "bomb",
            Accent = "a-bomb",
            Icon = "i-planted",
            Eyebrow = T(viewer, "hud_alert_bomb", "Bomb planted"),
            Chip = SkillChip(viewer, skill),
            ChipHue = SkillHue(skill),
            Title = T(viewer, "hud_alert_bomb_title", "{0} seconds to detonation", seconds),
            Sub = T(viewer, "hud_alert_bomb_sub", "Planted with {0}: a {1} second fuse.", SkillChip(viewer, skill), seconds),
            Num = seconds,
            CountTo = DateTime.Now.AddSeconds(seconds),
            Unit = T(viewer, "hud_unit_sec", "SEC"),
        });

        public static bool HotBomb(CCSPlayerController viewer, bool hot)
        {
            if (!hot)
                return Raise(viewer, () => new AlertModel
                {
                    Key = "cooled",
                    Accent = "a-ok",
                    Icon = "i-check",
                    Eyebrow = T(viewer, "hud_alert_hot", "Hot bomb"),
                    Chip = SkillChip(viewer, Skills.HotBomb),
                    ChipHue = SkillHue(Skills.HotBomb),
                    Title = T(viewer, "hud_alert_cooled_title", "The bomb is no longer hot"),
                    Sub = T(viewer, "hud_alert_cooled_sub", "The CT with Hot Bomb is dead."),
                });

            float every = Math.Max(SkillsInfo.GetValue<float>(Skills.HotBomb, "cooldown"), 0.05f);
            int perSecond = (int)Math.Round(SkillsInfo.GetValue<int>(Skills.HotBomb, "damage") / every);
            return Raise(viewer, () => new AlertModel
            {
                Key = "hot",
                Accent = "a-hot",
                Icon = "i-c4",
                Eyebrow = T(viewer, "hud_alert_hot", "Hot bomb"),
                Chip = SkillChip(viewer, Skills.HotBomb),
                ChipHue = SkillHue(Skills.HotBomb),
                Title = T(viewer, "hud_alert_hot_title", "Careful, the bomb is hot"),
                Sub = T(viewer, "hud_alert_hot_sub", "It burns you while you carry it, {0} HP a second.", perSecond),
                Num = perSecond,
                Unit = T(viewer, "hud_unit_hps", "HP/S"),
            });
        }

        public static bool SiteClosed(CCSPlayerController viewer, string? letter) => Raise(viewer, () => new AlertModel
        {
            Key = "site",
            Accent = "a-site",
            Icon = letter == "B" ? "i-siteb" : "i-sitea",
            Eyebrow = T(viewer, "hud_alert_site", "Site closed"),
            Chip = SkillChip(viewer, Skills.AreaReaper),
            ChipHue = SkillHue(Skills.AreaReaper),
            Title = string.IsNullOrEmpty(letter)
                ? viewer.GetTranslationWithoutIlliterate("areareaper_bombsite_disabled")
                : T(viewer, "hud_alert_site_title", "Bombsite {0} is disabled", letter),
            Sub = T(viewer, "hud_alert_site_sub", "No bomb can be planted here this round."),
        });

        public static bool IlliterateAlert(CCSPlayerController viewer) => Raise(viewer, () => new AlertModel
        {
            Key = "illiterate",
            Accent = "a-read",
            Icon = "i-ghost",
            Eyebrow = T(viewer, "hud_alert_enemy", "Enemy skill"),
            Chip = SkillChip(viewer, Skills.Illiterate),
            ChipHue = SkillHue(Skills.Illiterate),
            Title = T(viewer, "hud_alert_read_title", "You cannot read"),
            Sub = T(viewer, "hud_alert_read_sub", "Your text is scrambled until the enemy with Illiterate dies."),
        });

        public static bool EnemySkill(CCSPlayerController viewer, Skills skill, string key, object[] args)
        {
            if (!Settings.EnemySkillAlerts) return false;

            return Raise(viewer, () => new AlertModel
            {
                Key = "enemy:" + SkillNames.Get(skill),
                Accent = "a-hit",
                Icon = "i-cross",
                Eyebrow = T(viewer, "hud_alert_enemy", "Enemy skill"),
                Chip = SkillChip(viewer, skill),
                ChipHue = SkillHue(skill),
                // The skill's own line, as the chat had it (scrambled under Illiterate, like the chat line)
                Title = viewer.GetTranslation(key, args),
                Sub = T(viewer, "hud_alert_enemy_sub", "An enemy used {0} on you.", SkillChip(viewer, skill)),
            });
        }

        private static void DriveAlert(CCSPlayerController p, View v, DateTime now)
        {
            var slot = v.AlertSlot;
            bool site = !string.Equals(Settings.AlertPosition, "Card", StringComparison.OrdinalIgnoreCase);
            One(p, v, AlertId, "pos", site ? "p-site" : "p-card");
            One(p, v, AlertId, "c", Cross ? "c-cross" : "c-bottom");

            // A new alert comes up once the last one has left and its bar has glided back to full
            if (v.AlertPending != null && v.AlertShown == null && slot.Collapsed && now - v.AlertBarResetAt >= BarWait)
            {
                v.AlertShown = v.AlertPending;
                v.AlertPending = null;
                v.AlertUntil = DateTime.MaxValue;
                v.AlertNum = int.MinValue;
                WriteAlert(p, v, v.AlertShown, now);
            }

            var shown = v.AlertShown;
            bool wanted = shown != null && v.AlertPending == null && now < v.AlertUntil;
            Drive(p, v, AlertId, slot, wanted, now);
            if (shown == null) return;

            if (slot.Shown && v.AlertUntil == DateTime.MaxValue)
            {
                v.AlertUntil = now.AddSeconds(AlertSeconds);
                v.AlertBarNextAt = DateTime.MinValue;
            }

            // The drain over the banner's 5 seconds, a step every BarEvery, aimed one glide ahead
            if (slot.Shown && v.AlertUntil != DateTime.MaxValue && now >= v.AlertBarNextAt)
            {
                v.AlertBarNextAt = now + BarEvery;
                AlertBar(p, v, (v.AlertUntil - now - Glide).TotalSeconds / AlertSeconds);
            }

            if (shown.CountTo != DateTime.MinValue)
            {
                int n = Math.Max(0, (int)Math.Ceiling((shown.CountTo - now).TotalSeconds - 0.05));
                if (n != v.AlertNum)
                {
                    Var(p, v, "al_num", n.ToString(CultureInfo.InvariantCulture));
                    if (v.AlertNum != int.MinValue && slot.Shown)
                        Kick(p, v, "al_num", "pop");
                    v.AlertNum = n;
                }
            }

            if (!wanted && slot.Collapsed)
            {
                v.LastAlertKey = shown.Key;
                v.LastAlertEnd = now;
                v.AlertShown = null;
                // Out of sight: the bar glides back to full for the next one
                AlertBar(p, v, 1);
                v.AlertBarResetAt = now;
            }
        }

        private static void WriteAlert(CCSPlayerController p, View v, AlertModel a, DateTime now)
        {
            One(p, v, AlertId, "a", a.Accent);
            One(p, v, AlertId, "i", a.Icon);
            Cls(p, v, AlertId, "no-num", a.Num < 0);
            Var(p, v, "al_eyebrow", a.Eyebrow);
            Var(p, v, "al_chip", a.Chip ?? "");
            Cls(p, v, "al_chip", "none", string.IsNullOrEmpty(a.Chip));
            One(p, v, "al_chip", "h", a.ChipHue);
            Var(p, v, "al_title", a.Title);
            Var(p, v, "al_sub", a.Sub);
            Var(p, v, "al_unit", a.Unit);
            if (a.Num >= 0)
            {
                int n = a.CountTo == DateTime.MinValue ? a.Num : Math.Max(0, (int)Math.Ceiling((a.CountTo - now).TotalSeconds - 0.05));
                Var(p, v, "al_num", n.ToString(CultureInfo.InvariantCulture));
                v.AlertNum = n;
            }
        }

        // ------------------------------------------------------------------ coins

        public static bool Coin(CCSPlayerController collector, int gained, int moneyAfter)
        {
            if (!Draws(collector)) return false;

            var v = ViewOf(collector);
            var now = DateTime.Now;
            bool up = now < v.CoinUntil;
            if (!up || (now - v.CoinLast).TotalSeconds > CoinMergeSeconds || v.CoinMax)
            {
                v.CoinSum = 0;
                v.CoinCount = 0;
            }

            if (gained <= 0)
            {
                // At max money: say so, instead of a "+$300" that never arrived
                v.CoinMax = true;
                v.CoinMoney = moneyAfter;
                v.CoinSum = 0;
                v.CoinCount = 0;
            }
            else
            {
                v.CoinMax = false;
                v.CoinSum += gained;
                v.CoinCount++;
            }

            v.CoinLast = now;
            v.CoinUntil = now.AddSeconds(CoinSeconds);
            v.CoinDirty = true;
            return true;
        }

        private static void DriveCoins(CCSPlayerController p, View v, DateTime now)
        {
            bool beside = string.Equals(Settings.CoinsPosition, "Beside", StringComparison.OrdinalIgnoreCase);
            One(p, v, CoinsId, "pos", beside ? "p-beside" : "p-under");

            if (v.CoinDirty)
            {
                v.CoinDirty = false;
                Cls(p, v, CoinsId, "max", v.CoinMax);
                Var(p, v, "cn_amt", v.CoinMax
                    ? "$" + v.CoinMoney.ToString("N0", CultureInfo.InvariantCulture)
                    : "+$" + v.CoinSum.ToString("N0", CultureInfo.InvariantCulture));
                Var(p, v, "cn_why", v.CoinMax
                    ? T(p, "hud_coin_max", "Max money")
                    : v.CoinCount > 1 ? T(p, "hud_coins", "{0} coins", v.CoinCount) : T(p, "hud_coin", "Coin"));
                if (v.CoinsSlot.Shown)
                    Kick(p, v, "cn_amt", "pop");
            }

            Drive(p, v, CoinsId, v.CoinsSlot, now < v.CoinUntil, now);
        }

        // ------------------------------------------------------------------ panels: show and collapse

        private sealed class Slot
        {
            public bool Shown;
            public bool Collapsed = true;
            public DateTime ShowAt = DateTime.MaxValue;
            public DateTime CollapseAt = DateTime.MaxValue;
        }

        private static void Drive(CCSPlayerController p, View v, string id, Slot s, bool wanted, DateTime now)
        {
            if (wanted)
            {
                s.CollapseAt = DateTime.MaxValue;
                if (s.Shown) return;

                if (s.Collapsed)
                {
                    Cls(p, v, id, "hidden", false);
                    s.Collapsed = false;
                    s.ShowAt = now + Entry;
                    return;
                }

                // Leaving, and wanted back: it is still in layout, so it can come back at once
                if (s.ShowAt == DateTime.MaxValue) s.ShowAt = now;
                if (now < s.ShowAt) return;

                Cls(p, v, id, "show", true);
                s.Shown = true;
                s.ShowAt = DateTime.MaxValue;
                return;
            }

            s.ShowAt = DateTime.MaxValue;
            if (s.Shown)
            {
                Cls(p, v, id, "show", false);
                s.Shown = false;
                s.CollapseAt = now + Exit;
                return;
            }

            if (s.Collapsed) return;

            // Never got as far as "show" (a replacement came during the entry): nothing to wait for
            if (s.CollapseAt == DateTime.MaxValue) s.CollapseAt = now;
            if (now >= s.CollapseAt)
            {
                Cls(p, v, id, "hidden", true);
                s.Collapsed = true;
                s.CollapseAt = DateTime.MaxValue;
            }
        }

        // ------------------------------------------------------------------ bars

        private enum BarMode { None, Static, Run }

        private readonly record struct BarSpec(BarMode Mode, float Value, DateTime Start, DateTime End, bool Fill, string? Tone)
        {
            public static readonly BarSpec None = new(BarMode.None, 0, default, default, false, null);
            public static BarSpec Static(float value, string? tone) => new(BarMode.Static, value, default, default, false, tone);
            public static BarSpec Run(DateTime start, DateTime end, bool fill, string? tone) => new(BarMode.Run, 0, start, end, fill, tone);
        }

        private sealed class BarView
        {
            public BarMode Mode;
            public DateTime Start, End;
            public bool Fill;

            /// <summary>When a run may get its next step.</summary>
            public DateTime NextStepAt = DateTime.MinValue;
        }

        private static int StepOf(double fraction) =>
            Math.Clamp((int)Math.Round(fraction * BarSteps, MidpointRounding.AwayFromZero), 0, BarSteps);

        private static string AtClass(double fraction) => $"at-{StepOf(fraction)}";

        /// <summary>The alert bar's step for <paramref name="fraction"/> (1 = full, its default, written as no step).</summary>
        private static void AlertBar(CCSPlayerController p, View v, double fraction)
        {
            int step = StepOf(fraction);
            One(p, v, "al_bar", "at", step >= BarSteps ? null : $"at-{step}");
        }

        private static void DriveBar(CCSPlayerController p, View v, string id, BarView b, BarSpec spec, DateTime now)
        {
            One(p, v, id, "tone", spec.Tone);

            switch (spec.Mode)
            {
                case BarMode.None:
                    // Hidden by the track: park it empty, so the next run glides from a known place
                    if (b.Mode != BarMode.None) ParkBar(p, v, id, b);
                    return;

                case BarMode.Static:
                    b.Mode = BarMode.Static;
                    One(p, v, id, "at", AtClass(spec.Value));
                    return;

                case BarMode.Run:
                {
                    bool same = b.Mode == BarMode.Run && b.Fill == spec.Fill
                        && Math.Abs((b.End - spec.End).TotalSeconds) < 0.3
                        && Math.Abs((b.Start - spec.Start).TotalSeconds) < 0.3;

                    if (!same)
                    {
                        b.Mode = BarMode.Run;
                        b.Start = spec.Start;
                        b.End = spec.End;
                        b.Fill = spec.Fill;
                        b.NextStepAt = DateTime.MinValue;
                    }

                    // A step every BarEvery, for where the run will be one glide from now. A new run needs nothing
                    // special: its first step is where it is, and the glide takes the fill there.
                    if (now < b.NextStepAt) return;
                    b.NextStepAt = now + BarEvery;

                    double length = (b.End - b.Start).TotalSeconds;
                    double f = length <= 0 ? 1 : Math.Clamp((now + Glide - b.Start).TotalSeconds / length, 0, 1);
                    One(p, v, id, "at", AtClass(b.Fill ? f : 1 - f));
                    return;
                }
            }
        }

        private static void ParkBar(CCSPlayerController p, View v, string id, BarView b)
        {
            if (b.Mode == BarMode.None) return;
            b.Mode = BarMode.None;
            One(p, v, id, "at", "at-0");
        }

        // ------------------------------------------------------------------ writes: only what changed

        private sealed class View
        {
            public readonly Dictionary<string, string> Vars = [];
            public readonly Dictionary<(string Id, string Cls), bool> Classes = [];
            public readonly Dictionary<(string Id, string Group), string?> Groups = new()
            {
                // The classes the layout is authored with: a rebuilt panel has these again
                [(CardId, "pos")] = "p-bottom",
                [(CardId, "s")] = "s-none",
                [(CardId, "k")] = "k-passive",
                [(CardId, "h")] = "h-white",
                [("hs_rar", "rar")] = "rar-0",
                [("hs_team", "tm")] = "tm-0",
                [(SpecId, "k")] = "k-passive",
                [(SpecId, "h")] = "h-white",
                [("sp_rar", "rar")] = "rar-0",
                [(AlertId, "pos")] = "p-site",
                [(AlertId, "c")] = "c-bottom",
                [(AlertId, "a")] = "a-bomb",
                [(AlertId, "i")] = "i-planted",
                [(CoinsId, "pos")] = "p-under",
            };
            public readonly Dictionary<string, bool> Flip = [];

            public View()
            {
                foreach (var id in new[] { CardId, SpecId, AlertId, CoinsId })
                    Classes[(id, "hidden")] = true;
                Classes[("hs_via", "none")] = true;
                Classes[("sp_via", "none")] = true;
                Classes[("sp_state", "none")] = true;
                Classes[("al_chip", "none")] = true;
                foreach (var (id, group) in Groups.Keys.ToList())
                    if (Groups[(id, group)] is { } cls) Classes[(id, cls)] = true;
            }

            public readonly Slot CardSlot = new(), SpecSlot = new(), AlertSlot = new(), CoinsSlot = new();
            public readonly BarView CardBar = new(), SpecBar = new();

            // the card, as last drawn
            public Skills LastSkill = (Skills)(-1);
            public bool LastWasDrawing;
            public HudState LastState = HudState.Auto;
            public int LastCount = -1;
            public bool LastToggle;

            // the draw
            public DateTime NextRollAt;
            public double RollStepMs = 95;
            public jSkill_SkillInfo? RollSkill;

            // the spectator line
            public uint SpecTarget = uint.MaxValue;
            public string SpecBig = "";

            // the alert
            public AlertModel? AlertShown, AlertPending;
            public DateTime AlertUntil = DateTime.MaxValue;
            public DateTime AlertBarResetAt = DateTime.MinValue;
            public DateTime AlertBarNextAt = DateTime.MinValue;
            public int AlertNum = int.MinValue;
            public string? LastAlertKey;
            public DateTime LastAlertEnd = DateTime.MinValue;

            // coins
            public int CoinSum, CoinCount, CoinMoney;
            public bool CoinMax, CoinDirty;
            public DateTime CoinLast = DateTime.MinValue, CoinUntil = DateTime.MinValue;
        }

        private static void Var(CCSPlayerController p, View v, string name, string value)
        {
            if (v.Vars.TryGetValue(name, out var old) && old == value) return;
            v.Vars[name] = value;
            panel!.SetVariableFor(p, name, value);
        }

        private static void Cls(CCSPlayerController p, View v, string id, string cls, bool on)
        {
            var key = (id, cls);
            if (v.Classes.TryGetValue(key, out var old) && old == on) return;
            if (panel!.TrySetClassFor(p, id, cls, on))
                v.Classes[key] = on;
        }

        /// <summary>Exactly one class of a group on (or none): the old one off, the new one on.</summary>
        private static void One(CCSPlayerController p, View v, string id, string group, string? cls)
        {
            var key = (id, group);
            v.Groups.TryGetValue(key, out var current);
            if (current == cls) return;
            if (current != null) Cls(p, v, id, current, false);
            if (cls != null) Cls(p, v, id, cls, true);
            v.Groups[key] = cls;
        }

        /// <summary>
        /// Restarts an animation: ping-pong between name-a and name-b, the only restart a class diff carries.
        /// One group per panel, so a press after a pop still changes the class and plays.
        /// </summary>
        private static void Kick(CCSPlayerController p, View v, string id, string name)
        {
            v.Flip.TryGetValue(id, out var flip);
            flip = !flip;
            v.Flip[id] = flip;
            One(p, v, id, "anim", $"{name}-{(flip ? "a" : "b")}");
        }

        /// <summary>A phrase from the language files, or the English fallback when the server's files do not have it yet.</summary>
        private static string T(CCSPlayerController p, string key, string fallback, params object[] args)
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            var text = p.GetTranslationWithoutIlliterate(key, args);
            if (text != key) return text;
            try { return args.Length == 0 ? fallback : string.Format(CultureInfo.InvariantCulture, fallback, args); }
            catch (FormatException) { return fallback; }
        }
    }
}
