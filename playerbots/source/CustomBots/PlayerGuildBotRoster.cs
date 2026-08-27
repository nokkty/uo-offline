// =========================================================================
// PlayerGuildBotRoster.cs — configuration, persistent identities, and
// idempotent lifecycle reconciliation for transient bots attached to real
// player guilds. Native guild adapters call the public lifecycle entry points
// defined here; no synthetic BotGuilds membership is created.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Server;
using Server.Commands;

namespace Server.CustomBots
{
    public static class PlayerGuildBotRoster
    {
        public const int CurrentConfigVersion = 1;
        public const int CurrentStateVersion = 1;

        private static readonly string RosterPath = Path.Combine(
            Core.BaseDirectory, "Data", "PlayerGuildBots", "roster.json");
        private static readonly string StatePath = Path.Combine(
            Core.BaseDirectory, "Data", "PlayerGuildBots", "roster-state.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
        };

        private static readonly object Sync = new();
        private static PlayerGuildBotRosterSnapshot _activeSnapshot;
        private static PlayerGuildBotRosterState _state = NewState();
        private static bool _stateLoadHealthy = true;

        private static readonly object LifecycleSync = new();
        private static readonly Dictionary<string, PlayerGuildBotGuild> ActiveGuilds =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> GuildGenerations =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingRecoveryKeys =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> RetiringKeys =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> ChatCursors =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> PendingChatReplies =
            new(StringComparer.OrdinalIgnoreCase);
        private static Func<IReadOnlyList<PlayerGuildBotGuild>> _guildProvider;
        private static bool _initialized;
        private static bool _startupReconcilePending = true;
        private static bool _reconcileScheduled;

        public static string ConfigurationPath => RosterPath;
        public static string StatePathForDiagnostics => StatePath;

        public static PlayerGuildBotRosterSnapshot ActiveSnapshot
        {
            get
            {
                lock (Sync)
                {
                    return _activeSnapshot;
                }
            }
        }

        public static bool HasValidConfiguration
        {
            get
            {
                lock (Sync)
                {
                    return _activeSnapshot != null;
                }
            }
        }

        // Shared owner key for NamePool reservations and persisted state.
        public static string GetReservationOwner(string guildId, string personaId) =>
            MakeKey(guildId, personaId);

        // Subscribers such as later chat/admin integrations may observe a
        // deletion, but reconciliation is owned here so targeted deletion
        // recovery cannot be forgotten.
        public static event Action<PlayerBot> RosterBotDeleted;

        // Headless/integration observers can verify clientless delivery without
        // changing the native chat path. The event is raised after the direct
        // sends complete and carries the exact live sender name plus count.
        public static event Action<string, string, string, int> GuildChatReplyDelivered;

        public static int PendingGuildChatReplyCount
        {
            get
            {
                lock (LifecycleSync)
                {
                    return PendingChatReplies.Count;
                }
            }
        }

        // Diagnostics/test seam: returns a snapshot and never mutates roster
        // state. Production callers should prefer the gump/query APIs.
        public static IReadOnlyList<PlayerBot> GetRosterBotsForDiagnostics(
            string guildId, string personaId = null) =>
            FindRosterBots(guildId, personaId).ToArray();

        public static void OnRosterBotDeleted(PlayerBot bot)
        {
            if (bot == null)
            {
                return;
            }

            try
            {
                RosterBotDeleted?.Invoke(bot);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] deletion observer failed: {ex.Message}");
            }

            var key = MakeKey(bot.PlayerGuildId, bot.PlayerGuildPersonaId);
            if (key == null)
            {
                return;
            }

            PlayerGuildBotGuild guild;
            long generation;
            lock (LifecycleSync)
            {
                if (_startupReconcilePending ||
                    RetiringKeys.Contains(key) ||
                    !ActiveGuilds.TryGetValue(bot.PlayerGuildId.Trim(), out guild) ||
                    !guild.IsActive ||
                    !PendingRecoveryKeys.Add(key))
                {
                    return;
                }
                generation = GuildGenerations.TryGetValue(guild.Id, out var current)
                    ? current
                    : 0;
            }

            // Delete callbacks can fire while World.Mobiles is being
            // enumerated. Defer one server tick and coalesce duplicate events.
            Timer.DelayCall(TimeSpan.Zero, () => RecoverDeleted(key, generation));
        }

        // A corrupt state file must not silently generate replacement names.
        // The manager leaves affected identities offline until the file is
        // repaired or deliberately removed by an operator.
        public static bool StateLoadHealthy
        {
            get
            {
                lock (Sync)
                {
                    return _stateLoadHealthy;
                }
            }
        }

        // ModernUO invokes Configure() before world loading. Reading data here
        // is safe; world enumeration and reconciliation belong to Initialize.
        public static void Configure()
        {
            CommandSystem.Register("GuildBots", AccessLevel.Player, OnCommand);
            LoadState();
            Reload();
        }

        [Usage("GuildBots roster [guildId] | reload")]
        [Description("View player-guild bot rosters or reload roster data (administrator only).")]
        private static void OnCommand(CommandEventArgs e)
        {
            var from = e.Mobile;
            if (from == null)
            {
                return;
            }

            var subcommand = e.Length > 0 ? e.GetString(0) : "roster";
            if (string.Equals(subcommand, "reload", StringComparison.OrdinalIgnoreCase))
            {
                if (from.AccessLevel < AccessLevel.Administrator)
                {
                    from.SendMessage("GuildBots reload requires administrator access.");
                    return;
                }

                var load = Reload();
                if (!load.Success)
                {
                    from.SendMessage(
                        $"GuildBots reload failed; active roster v{load.Version} was retained.");
                    foreach (var error in load.Errors)
                    {
                        from.SendMessage($"  {error}");
                    }
                    return;
                }

                // Reconcile immediately so the command can report concrete
                // add/retire/skip counts instead of making staff wait for the
                // coalesced startup timer.
                var report = ReconcileAllWithReport();
                from.SendMessage(
                    $"GuildBots reload OK: v{load.Version}, {load.PersonaCount} persona(s); " +
                    $"{report.ActiveGuildCount} guild(s), {report.CreatedCount} added, " +
                    $"{report.RetiredCount} retired, {report.SkippedCount} skipped/conflict(s), " +
                    $"{report.OnlineCount} online, {report.OfflineCount} offline, " +
                    $"{report.ReservedNameCount} reserved name(s).");
                return;
            }

            if (!string.Equals(subcommand, "roster", StringComparison.OrdinalIgnoreCase))
            {
                from.SendMessage("Usage: GuildBots roster [guildId] | GuildBots reload");
                return;
            }

            var guildId = e.Length > 1 ? e.GetString(1) : null;
            bool staff = from.AccessLevel >= AccessLevel.GameMaster;
            if (!staff && !string.IsNullOrWhiteSpace(guildId))
            {
                from.SendMessage("You may inspect only your own guild's roster.");
                return;
            }

            if (staff)
            {
                if (string.IsNullOrWhiteSpace(guildId))
                {
                    from.SendGump(new GuildBotsRosterGump(from));
                    return;
                }

                if (!TryGetActiveGuild(guildId, out _))
                {
                    from.SendMessage("That player guild is not active.");
                    return;
                }
            }
            else if (!TryGetGuildForMember(from, out var ownGuild))
            {
                from.SendMessage("You are not a member of an active player guild.");
                return;
            }
            else
            {
                guildId = ownGuild.Id;
            }

            from.SendGump(new GuildBotsRosterGump(from, guildId));
        }

        // Native guild adapters assign a provider during their Configure pass.
        // Keeping this boundary typed to a small snapshot avoids coupling the
        // roster manager to one ModernUO guild implementation.
        public static Func<IReadOnlyList<PlayerGuildBotGuild>> GuildProvider
        {
            get
            {
                lock (LifecycleSync)
                {
                    return _guildProvider;
                }
            }
            set
            {
                lock (LifecycleSync)
                {
                    _guildProvider = value;
                }
            }
        }

        // ModernUO invokes static Initialize methods after world loading. A
        // zero-delay callback runs after all Initialize methods, including the
        // ambient stale-bot purge, so stale roster deletions cannot respawn.
        public static void Initialize()
        {
            lock (LifecycleSync)
            {
                if (_initialized)
                {
                    return;
                }
                _initialized = true;
            }
            ScheduleReconcile();
        }

        public static IReadOnlyList<PlayerGuildBotGuild> GetActiveGuilds()
        {
            lock (LifecycleSync)
            {
                return ActiveGuilds.Values
                    .Where(guild => guild.IsActive)
                    .OrderBy(guild => guild.Tag, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(guild => guild.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        public static bool TryGetActiveGuild(
            string guildId, out PlayerGuildBotGuild guild)
        {
            guild = null;
            if (string.IsNullOrWhiteSpace(guildId))
            {
                return false;
            }

            lock (LifecycleSync)
            {
                return ActiveGuilds.TryGetValue(guildId.Trim(), out guild) &&
                       guild.IsActive;
            }
        }

        public static bool TryGetGuildForMember(
            Mobile member, out PlayerGuildBotGuild guild)
        {
            guild = null;
            if (member == null || member.Deleted || member is PlayerBot)
            {
                return false;
            }

            foreach (var candidate in GetActiveGuilds())
            {
                foreach (var guildMember in GetGuildMembers(candidate))
                {
                    if (ReferenceEquals(guildMember, member) ||
                        (guildMember != null && guildMember.Equals(member)))
                    {
                        guild = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        public static PlayerGuildBotRosterView GetRosterView(string guildId)
        {
            if (!TryGetActiveGuild(guildId, out var guild))
            {
                return null;
            }

            var snapshot = ActiveSnapshot;
            if (snapshot == null)
            {
                return null;
            }

            var rows = new List<PlayerGuildBotRosterRow>(snapshot.Personas.Count);
            foreach (var persona in snapshot.Personas)
            {
                TryGetBinding(guild.Id, persona.Id, out var binding);
                var live = FindRosterBots(guild.Id, persona.Id)
                    .FirstOrDefault(IsOnlineRosterBot);
                var exactName = binding?.ExactName;
                if (exactName == null && StateLoadHealthy)
                {
                    exactName = snapshot.FormatName(persona.BaseName, guild.Tag);
                }
                rows.Add(new PlayerGuildBotRosterRow(
                    persona.Id,
                    exactName,
                    persona.Class,
                    persona.SkillTier,
                    persona.Behavior,
                    binding?.HomeCity,
                    live != null));
            }

            return new PlayerGuildBotRosterView(guild, rows);
        }

        public static void OnGuildCreated(string guildId, string guildTag) =>
            OnGuildCreated(new PlayerGuildBotGuild(guildId, guildTag));

        public static void OnGuildCreated(
            string guildId,
            string guildTag,
            Func<IReadOnlyList<Mobile>> memberProvider) =>
            OnGuildCreated(new PlayerGuildBotGuild(guildId, guildTag,
                memberProvider: memberProvider));

        public static void OnGuildCreated(PlayerGuildBotGuild guild)
        {
            if (!TryNormalizeGuild(guild, out var normalized))
            {
                return;
            }

            lock (LifecycleSync)
            {
                ActiveGuilds[normalized.Id] = normalized;
                if (!GuildGenerations.ContainsKey(normalized.Id))
                {
                    GuildGenerations[normalized.Id] = 0;
                }
            }
            ReconcileGuild(normalized);
        }

        public static void OnGuildDisbanded(string guildId)
        {
            if (!string.IsNullOrWhiteSpace(guildId))
            {
                RetireGuild(guildId.Trim());
            }
        }

        public static void ScheduleReconcile()
        {
            lock (LifecycleSync)
            {
                if (!_initialized || _reconcileScheduled)
                {
                    return;
                }
                _reconcileScheduled = true;
            }
            Timer.DelayCall(TimeSpan.Zero, () =>
            {
                lock (LifecycleSync)
                {
                    _reconcileScheduled = false;
                    _startupReconcilePending = false;
                }
                ReconcileAll();
            });
        }

        // Keep the original void callback signature for native adapters;
        // command/admin callers can opt into the diagnostic result.
        public static void ReconcileAll() => ReconcileAllWithReport();

        public static RosterReconcileResult ReconcileAllWithReport()
        {
            PlayerGuildBotRosterSnapshot snapshot = ActiveSnapshot;
            if (snapshot == null)
            {
                Console.WriteLine(
                    "[PlayerGuildBotRoster] reconciliation skipped: no valid roster is active.");
                return RosterReconcileResult.Empty;
            }

            Func<IReadOnlyList<PlayerGuildBotGuild>> provider = GuildProvider;
            if (provider == null)
            {
                Console.WriteLine(
                    "[PlayerGuildBotRoster] reconciliation skipped: no native guild provider is registered.");
                return RosterReconcileResult.Empty;
            }

            IReadOnlyList<PlayerGuildBotGuild> supplied;
            try
            {
                supplied = provider();
                if (supplied == null)
                {
                    Console.WriteLine(
                        "[PlayerGuildBotRoster] guild enumeration returned no snapshot; " +
                        "reconciliation was not applied.");
                    return RosterReconcileResult.Empty;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild enumeration failed: {ex.Message}");
                return RosterReconcileResult.Empty;
            }

            int created = 0;
            int skipped = 0;
            int retired = 0;
            var active = new Dictionary<string, PlayerGuildBotGuild>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var guild in supplied)
            {
                if (TryNormalizeGuild(guild, out var normalized))
                {
                    active[normalized.Id] = normalized;
                }
            }

            // The provider is authoritative. Retire known/state-only guilds
            // absent from its active snapshot so no orphan identity remains.
            foreach (var guildId in GetKnownGuildIds())
            {
                if (!active.ContainsKey(guildId))
                {
                    retired += RetireGuild(guildId);
                }
            }

            foreach (var guild in active.Values)
            {
                lock (LifecycleSync)
                {
                    ActiveGuilds[guild.Id] = guild;
                    if (!GuildGenerations.ContainsKey(guild.Id))
                    {
                        GuildGenerations[guild.Id] = 0;
                    }
                }
                ReconcileGuild(guild, ref created, ref skipped, ref retired);
            }

            var result = BuildReconcileResult(active.Count, created, retired, skipped);
            Console.WriteLine(
                $"[PlayerGuildBotRoster] reconciled {active.Count} guild(s): " +
                $"{created} created, {retired} retired, {skipped} skipped.");
            return result;
        }

        private static RosterReconcileResult BuildReconcileResult(
            int activeGuildCount, int createdCount, int retiredCount, int skippedCount)
        {
            int online = 0;
            foreach (var guild in GetActiveGuilds())
            {
                online += FindRosterBots(guild.Id, null)
                    .Count(IsOnlineRosterBot);
            }

            int configured = ActiveSnapshot?.Personas.Count ?? 0;
            return new RosterReconcileResult(
                activeGuildCount,
                configured,
                createdCount,
                retiredCount,
                skippedCount,
                online,
                Math.Max(0, activeGuildCount * configured - online),
                NamePool.ReservedCount);
        }

        // Called by the native guild-chat adapter after a real player's
        // message has been accepted. Bots never enter this path as speakers.
        // A per-guild pending guard guarantees at most one delayed response.
        public static bool HandleGuildMessage(
            string guildId, Mobile speaker, string text)
        {
            if (string.IsNullOrWhiteSpace(guildId) ||
                string.IsNullOrWhiteSpace(text) ||
                speaker == null || speaker.Deleted ||
                speaker is PlayerBot || !speaker.Player)
            {
                return false;
            }

            PlayerGuildBotGuild guild;
            long generation;
            guildId = guildId.Trim();
            lock (LifecycleSync)
            {
                if (!ActiveGuilds.TryGetValue(guildId, out guild) ||
                    !guild.IsActive ||
                    PendingChatReplies.Contains(guildId))
                {
                    return false;
                }
                generation = GuildGenerations.TryGetValue(guildId,
                    out var current) ? current : 0;
            }

            var snapshot = ActiveSnapshot;
            if (snapshot == null)
            {
                return false;
            }

            var responders = FindChatResponders(guildId, snapshot);
            if (responders.Count == 0)
            {
                return false;
            }

            ChatResponder selected;
            lock (LifecycleSync)
            {
                if (!ActiveGuilds.ContainsKey(guildId) ||
                    PendingChatReplies.Contains(guildId))
                {
                    return false;
                }
                int cursor = ChatCursors.TryGetValue(guildId, out var previous)
                    ? previous
                    : 0;
                selected = responders[cursor % responders.Count];
                ChatCursors[guildId] = (cursor + 1) % responders.Count;
                PendingChatReplies.Add(guildId);
            }

            int delay = Utility.RandomMinMax(2, 5);
            Timer.DelayCall(TimeSpan.FromSeconds(delay), () =>
                DeliverGuildReply(guildId, selected.Persona.Id, generation));
            return true;
        }

        private static List<ChatResponder> FindChatResponders(
            string guildId, PlayerGuildBotRosterSnapshot snapshot)
        {
            var bots = FindRosterBots(guildId, null);
            var responders = new List<ChatResponder>();
            foreach (var persona in snapshot.Personas)
            {
                if (!HasChatLines(persona.ChatCategories))
                {
                    continue;
                }
                var bot = bots.FirstOrDefault(candidate =>
                    string.Equals(candidate.PlayerGuildPersonaId, persona.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsOnlineRosterBot(candidate));
                if (bot != null)
                {
                    responders.Add(new ChatResponder(persona));
                }
            }
            return responders;
        }

        private static bool HasChatLines(IReadOnlyList<string> categories)
        {
            if (categories == null)
            {
                return false;
            }
            foreach (var category in categories)
            {
                if (ChatLibrary.CategoryCount(category) > 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsOnlineRosterBot(PlayerBot bot) =>
            bot != null && !bot.Deleted && bot.Alive && !bot.LoggingOut &&
            bot.Map != null && bot.Map != Map.Internal;

        private static void DeliverGuildReply(
            string guildId, string personaId, long generation)
        {
            PlayerGuildBotGuild guild;
            lock (LifecycleSync)
            {
                PendingChatReplies.Remove(guildId);
                if (!ActiveGuilds.TryGetValue(guildId, out guild) ||
                    !guild.IsActive ||
                    GuildGenerations.TryGetValue(guildId, out var current) &&
                        current != generation)
                {
                    return;
                }
            }

            var snapshot = ActiveSnapshot;
            if (snapshot == null || !snapshot.TryGetPersona(personaId, out var persona))
            {
                return;
            }

            var bot = FindRosterBots(guildId, personaId)
                .FirstOrDefault(IsOnlineRosterBot);
            if (bot == null)
            {
                return;
            }

            var line = ChatLibrary.PickRandom(persona.ChatCategories?.ToArray());
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var recipients = GetGuildMembers(guild);
            int sent = 0;
            foreach (var member in recipients)
            {
                if (!IsOnlineRealMember(member))
                {
                    continue;
                }
                try
                {
                    // Mobile.SendMessage handles transport details for a real
                    // member; this path never reads a bot NetState.
                    member.SendMessage(0x3B2, $"{bot.Name}: {line}");
                    sent++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] guild '{guildId}' reply delivery " +
                        $"to '{member.Name}' failed: {ex.Message}");
                }
            }

            if (sent == 0)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guildId}' reply skipped: " +
                    "no online real members.");
            }

            try
            {
                GuildChatReplyDelivered?.Invoke(guildId, personaId, bot.Name, sent);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] chat delivery observer failed: {ex.Message}");
            }
        }

        private static IReadOnlyList<Mobile> GetGuildMembers(PlayerGuildBotGuild guild)
        {
            try
            {
                return guild.MemberProvider?.Invoke() ?? guild.Members ??
                    Array.Empty<Mobile>();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guild.Id}' member enumeration failed: " +
                    ex.Message);
                return Array.Empty<Mobile>();
            }
        }

        private static bool IsOnlineRealMember(Mobile member) =>
            member != null && !member.Deleted && member.Player &&
            member.Map != null && member.Map != Map.Internal &&
            member is not PlayerBot;

        public static RosterLoadResult Reload() => Reload(scheduleReconcile: true);

        // The headless bridge uses this overload while swapping a disposable
        // candidate so the global provider is never reconciled mid-scenario.
        public static RosterLoadResult Reload(bool scheduleReconcile)
        {
            var errors = new List<string>();
            PlayerGuildBotRosterSnapshot candidate = null;

            try
            {
                // Configure ordering is not guaranteed. Ensure behavior names
                // are available before validating a candidate document.
                if (!BehaviorRegistry.KnownNames.Any())
                {
                    BehaviorRegistry.Configure();
                }

                if (!File.Exists(RosterPath))
                {
                    errors.Add($"Roster file not found: {RosterPath}");
                }
                else
                {
                    var text = File.ReadAllText(RosterPath);
                    var document = JsonSerializer.Deserialize<RosterDocument>(text, JsonOptions);
                    candidate = Validate(document, errors);
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"Invalid JSON in {RosterPath}: {ex.Message}");
            }
            catch (IOException ex)
            {
                errors.Add($"Unable to read {RosterPath}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                errors.Add($"Unable to read {RosterPath}: {ex.Message}");
            }
            catch (Exception ex)
            {
                errors.Add($"Roster validation failed: {ex.Message}");
            }

            lock (Sync)
            {
                if (candidate != null && errors.Count == 0)
                {
                    _activeSnapshot = candidate;
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] loaded roster v{candidate.Version} " +
                        $"({candidate.Personas.Count} persona(s)).");
                    var result = new RosterLoadResult(true, candidate.Version,
                        candidate.Personas.Count, Array.Empty<string>());
                    try
                    {
                        // A valid roster reload also refreshes editable chat
                        // categories; an invalid roster never reaches here.
                        ChatLibrary.Load();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[PlayerGuildBotRoster] chat reload failed: {ex.Message}");
                    }
                    if (_initialized && scheduleReconcile)
                    {
                        ScheduleReconcile();
                    }
                    return result;
                }

                // Do not replace a working snapshot on a bad startup/reload.
                var retained = _activeSnapshot != null
                    ? $" Last valid roster retained ({_activeSnapshot.Personas.Count} persona(s))."
                    : " No valid roster is active; new roster creation is disabled.";

                foreach (var error in errors)
                {
                    Console.WriteLine($"[PlayerGuildBotRoster] ERROR: {error}");
                }
                Console.WriteLine($"[PlayerGuildBotRoster] roster rejected.{retained}");

                return new RosterLoadResult(false,
                    _activeSnapshot?.Version ?? 0,
                    _activeSnapshot?.Personas.Count ?? 0,
                    errors.Count == 0
                        ? new[] { "Roster candidate was empty or invalid." }
                        : errors.ToArray());
            }
        }

        private static void ReconcileGuild(
            PlayerGuildBotGuild guild,
            ref int created,
            ref int skipped,
            ref int retired)
        {
            var snapshot = ActiveSnapshot;
            if (snapshot == null)
            {
                return;
            }

            var configuredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var persona in snapshot.Personas)
            {
                configuredIds.Add(persona.Id);
                if (!ReconcilePersona(guild, snapshot, persona, out var wasCreated))
                {
                    skipped++;
                }
                if (wasCreated)
                {
                    created++;
                }
            }

            // Removed personas retain their historical binding for a future
            // re-add, but no longer keep a live bot or name reservation.
            foreach (var binding in GetBindings(guild.Id))
            {
                if (!configuredIds.Contains(binding.PersonaId) &&
                    RetirePersona(guild.Id, binding))
                {
                    retired++;
                }
            }
        }

        private static void ReconcileGuild(PlayerGuildBotGuild guild)
        {
            int created = 0;
            int skipped = 0;
            int retired = 0;
            ReconcileGuild(guild, ref created, ref skipped, ref retired);
            if (created > 0 || skipped > 0)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guild.Id}' reconciliation: " +
                    $"{created} created, {skipped} skipped.");
            }
        }

        private static bool ReconcilePersona(
            PlayerGuildBotGuild guild,
            PlayerGuildBotRosterSnapshot snapshot,
            PlayerGuildBotPersona persona,
            out bool created)
        {
            created = false;
            var bots = FindRosterBots(guild.Id, persona.Id);
            PlayerBot live = bots.Count > 0 ? bots[0] : null;
            for (int i = 1; i < bots.Count; i++)
            {
                DeleteRosterBot(bots[i]);
            }

            PlayerGuildBotRosterBinding binding;
            if (!TryGetBinding(guild.Id, persona.Id, out binding))
            {
                var homeCity = BotHomeCities.RollHome();
                if (string.IsNullOrWhiteSpace(homeCity))
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                        "skipped: no destination city is available for home-city assignment.");
                    return false;
                }

                var formattedName = snapshot.FormatName(persona.BaseName, guild.Tag);
                if (string.IsNullOrWhiteSpace(formattedName))
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                        "skipped: name format produced an empty name.");
                    return false;
                }
                if (!TryGetOrCreateBinding(guild.Id, persona.Id, formattedName,
                    homeCity, out binding, out created))
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                        "skipped: identity state is unavailable or unreadable.");
                    return false;
                }
                if (created)
                {
                    SaveState();
                }
            }

            var ownerKey = GetReservationOwner(guild.Id, persona.Id);
            if (string.IsNullOrWhiteSpace(ownerKey))
            {
                return false;
            }

            if (live != null)
            {
                if (!string.Equals(live.Name, binding.ExactName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                        $"name mismatch: live '{live.Name}', state '{binding.ExactName}'; " +
                        "manual repair required.");
                    return false;
                }
                if (!NamePool.Reserve(binding.ExactName, ownerKey))
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                        $"could not reserve live name '{binding.ExactName}'.");
                    return false;
                }
                return true;
            }

            if (PlayerBot.IsRealPlayerNameInUse(binding.ExactName))
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                    $"skipped: configured name '{binding.ExactName}' is used by a player; " +
                    "choose a different baseName or format.");
                return false;
            }

            if (!NamePool.Reserve(binding.ExactName, ownerKey))
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                    $"skipped: configured name '{binding.ExactName}' conflicts with " +
                    "another roster owner or live bot; no rename was attempted.");
                return false;
            }

            var bot = PlayerBot.CreatePlayerGuildBot(
                binding.ExactName,
                persona.Female,
                persona.Class,
                persona.SkillTier,
                persona.Behavior,
                binding.HomeCity,
                guild.Id,
                persona.Id);
            if (bot == null)
            {
                // Keep reservations for transient initialization failures, but
                // a real-player conflict must remain unreserved and explicit.
                if (PlayerBot.IsRealPlayerNameInUse(binding.ExactName))
                {
                    NamePool.ReleaseReservation(binding.ExactName, ownerKey);
                }
                return false;
            }

            if (!TryFindSpawnPoint(snapshot.SpawnRadius, bot, out var point))
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                    "has no valid destination/standable tile; identity remains reserved " +
                    "for the next reconciliation.");
                DeleteRosterBot(bot);
                return false;
            }

            try
            {
                bot.MoveToWorld(point, Map.Felucca);
                // Direct factory spawns have no Spawner to invoke this normal
                // hook, so call it exactly once after placement.
                bot.OnAfterSpawn();
                SaveState();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] guild '{guild.Id}' persona '{persona.Id}' " +
                    $"placement failed: {ex.Message}");
                DeleteRosterBot(bot);
                return false;
            }
        }

        private static bool TryFindSpawnPoint(
            int radius, PlayerBot bot, out Point3D point)
        {
            point = default;
            var destination = DestinationCatalog.PickWeighted(bot);
            if (destination == null)
            {
                return false;
            }

            Point3D anchor;
            var arrival = destination.PickArrival();
            if (arrival != null)
            {
                anchor = arrival.Point;
            }
            else
            {
                anchor = destination.ArrivalPoint ?? destination.Location;
            }

            if (!Walkable.NearestStandable(Map.Felucca, anchor.X, anchor.Y, radius,
                out var x, out var y, out var z))
            {
                return false;
            }

            point = new Point3D(x, y, z);
            return true;
        }

        private static bool RetirePersona(
            string guildId, PlayerGuildBotRosterBinding binding)
        {
            var bots = FindRosterBots(guildId, binding.PersonaId);
            bool hadLiveIdentity = bots.Count > 0 ||
                NamePool.IsReserved(binding.ExactName);
            foreach (var bot in bots)
            {
                DeleteRosterBot(bot);
            }
            NamePool.ReleaseReservation(binding.ExactName,
                GetReservationOwner(guildId, binding.PersonaId));
            return hadLiveIdentity;
        }

        private static int RetireGuild(string guildId)
        {
            guildId = guildId?.Trim();
            if (string.IsNullOrWhiteSpace(guildId))
            {
                return 0;
            }

            lock (LifecycleSync)
            {
                ActiveGuilds.Remove(guildId);
                GuildGenerations[guildId] = GuildGenerations.TryGetValue(guildId,
                    out var generation) ? generation + 1 : 1;
                PendingRecoveryKeys.RemoveWhere(key => IsGuildKey(key, guildId));
                PendingChatReplies.Remove(guildId);
                ChatCursors.Remove(guildId);
            }

            var bots = FindRosterBots(guildId, null);
            var bindings = GetBindings(guildId);
            foreach (var bot in bots)
            {
                DeleteRosterBot(bot);
            }
            foreach (var binding in bindings)
            {
                NamePool.ReleaseReservation(binding.ExactName,
                    GetReservationOwner(guildId, binding.PersonaId));
            }

            bool removed = RemoveGuildBindings(guildId);
            if (removed)
            {
                SaveState();
            }
            Console.WriteLine(
                $"[PlayerGuildBotRoster] retired guild '{guildId}': " +
                $"{bots.Count} bot(s), {bindings.Count} binding(s).");
            return Math.Max(bots.Count, bindings.Count);
        }

        private static void DeleteRosterBot(PlayerBot bot)
        {
            if (bot == null || bot.Deleted)
            {
                return;
            }

            var key = MakeKey(bot.PlayerGuildId, bot.PlayerGuildPersonaId);
            if (key != null)
            {
                lock (LifecycleSync)
                {
                    RetiringKeys.Add(key);
                    PendingRecoveryKeys.Remove(key);
                }
            }
            try
            {
                bot.Delete();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] bot deletion failed for '{bot.Name}': " +
                    ex.Message);
            }
            finally
            {
                if (key != null)
                {
                    lock (LifecycleSync)
                    {
                        RetiringKeys.Remove(key);
                    }
                }
            }
        }

        private static void RecoverDeleted(string key, long generation)
        {
            string guildId;
            string personaId;
            PlayerGuildBotGuild guild;
            lock (LifecycleSync)
            {
                PendingRecoveryKeys.Remove(key);
                if (!TrySplitKey(key, out guildId, out personaId) ||
                    !ActiveGuilds.TryGetValue(guildId, out guild) ||
                    !guild.IsActive ||
                    GuildGenerations.TryGetValue(guildId, out var current) &&
                        current != generation)
                {
                    return;
                }
            }

            var snapshot = ActiveSnapshot;
            if (snapshot == null || !snapshot.TryGetPersona(personaId, out _))
            {
                return;
            }
            ReconcileGuild(guild);
        }

        private static List<PlayerBot> FindRosterBots(
            string guildId, string personaId)
        {
            var found = new List<PlayerBot>();
            foreach (var mobile in World.Mobiles.Values)
            {
                if (mobile is not PlayerBot bot || bot.Deleted ||
                    !bot.IsPlayerGuildBot ||
                    !string.Equals(bot.PlayerGuildId, guildId,
                        StringComparison.OrdinalIgnoreCase) ||
                    (personaId != null && !string.Equals(bot.PlayerGuildPersonaId,
                        personaId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                found.Add(bot);
            }
            return found;
        }

        private static string[] GetKnownGuildIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (LifecycleSync)
            {
                foreach (var id in ActiveGuilds.Keys)
                {
                    ids.Add(id);
                }
            }
            lock (Sync)
            {
                foreach (var binding in _state.Bindings)
                {
                    if (!string.IsNullOrWhiteSpace(binding?.GuildId))
                    {
                        ids.Add(binding.GuildId.Trim());
                    }
                }
            }
            return ids.ToArray();
        }

        private static bool TryNormalizeGuild(
            PlayerGuildBotGuild guild, out PlayerGuildBotGuild normalized)
        {
            normalized = null;
            if (guild == null || !guild.IsActive ||
                string.IsNullOrWhiteSpace(guild.Id) ||
                string.IsNullOrWhiteSpace(guild.Tag))
            {
                return false;
            }
            normalized = new PlayerGuildBotGuild(
                guild.Id.Trim(), guild.Tag.Trim(), isActive: true,
                memberProvider: guild.MemberProvider, members: guild.Members);
            return true;
        }

        private static bool IsGuildKey(string key, string guildId) =>
            key != null && key.StartsWith(guildId + '\u001f',
                StringComparison.OrdinalIgnoreCase);

        private static bool TrySplitKey(
            string key, out string guildId, out string personaId)
        {
            guildId = null;
            personaId = null;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }
            int split = key.IndexOf('\u001f');
            if (split <= 0 || split >= key.Length - 1)
            {
                return false;
            }
            guildId = key[..split];
            personaId = key[(split + 1)..];
            return true;
        }

        private static PlayerGuildBotRosterSnapshot Validate(
            RosterDocument document, List<string> errors)
        {
            if (document == null)
            {
                errors.Add("The roster document is empty.");
                return null;
            }

            if (!document.Version.HasValue)
            {
                errors.Add("Missing required property 'version'.");
            }
            else if (document.Version.Value != CurrentConfigVersion)
            {
                errors.Add(
                    $"Unsupported roster version {document.Version.Value}; " +
                    $"expected {CurrentConfigVersion}.");
            }

            if (string.IsNullOrWhiteSpace(document.NameFormat))
            {
                errors.Add("Missing required property 'nameFormat'.");
            }
            else
            {
                ValidateNameFormat(document.NameFormat, errors);
            }

            if (!document.SpawnRadius.HasValue)
            {
                errors.Add("Missing required property 'spawnRadius'.");
            }
            else if (document.SpawnRadius.Value < 0)
            {
                errors.Add("'spawnRadius' must be zero or greater.");
            }

            if (document.Personas == null)
            {
                errors.Add("Missing required property 'personas'.");
            }

            if (errors.Count > 0)
            {
                return null;
            }

            var personas = new List<PlayerGuildBotPersona>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < document.Personas.Count; i++)
            {
                var source = document.Personas[i];
                if (source == null)
                {
                    errors.Add($"Persona[{i}] is null.");
                    continue;
                }

                var id = source.Id?.Trim();
                var baseName = source.BaseName?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add($"Persona[{i}] is missing 'id'.");
                }
                else if (!ids.Add(id))
                {
                    errors.Add($"Persona '{id}' is duplicated (IDs are case-insensitive).");
                }

                if (string.IsNullOrWhiteSpace(baseName))
                {
                    errors.Add($"Persona '{id ?? i.ToString()}' is missing 'baseName'.");
                }
                else if (baseName.IndexOfAny(new[] { '{', '}' }) >= 0)
                {
                    errors.Add($"Persona '{id}' has braces in 'baseName'.");
                }

                if (!source.Female.HasValue)
                {
                    errors.Add($"Persona '{id ?? i.ToString()}' is missing 'female'.");
                }

                BotClass botClass = default;
                if (!TryParseClass(source.Class, out botClass))
                {
                    errors.Add(
                        $"Persona '{id ?? i.ToString()}' has unsupported class '{source.Class}'.");
                }

                BotSkillTier skillTier = default;
                if (!TryParseSkillTier(source.SkillTier, out skillTier))
                {
                    errors.Add(
                        $"Persona '{id ?? i.ToString()}' has unsupported skill tier '{source.SkillTier}'.");
                }

                var behavior = source.Behavior?.Trim();
                if (string.IsNullOrWhiteSpace(behavior) ||
                    !BehaviorRegistry.KnownNames.Any(name =>
                        string.Equals(name, behavior, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add(
                        $"Persona '{id ?? i.ToString()}' has unknown behavior '{source.Behavior}'.");
                }

                if (source.ChatCategories == null)
                {
                    errors.Add($"Persona '{id ?? i.ToString()}' is missing 'chatCategories'.");
                }

                var categories = new List<string>();
                if (source.ChatCategories != null)
                {
                    foreach (var rawCategory in source.ChatCategories)
                    {
                        var category = rawCategory?.Trim();
                        if (string.IsNullOrWhiteSpace(category) ||
                            !IsCategoryIdentifier(category))
                        {
                            errors.Add(
                                $"Persona '{id ?? i.ToString()}' has invalid chat category " +
                                $"'{rawCategory}'.");
                            continue;
                        }
                        categories.Add(category);
                    }
                }

                if (!string.IsNullOrWhiteSpace(id) &&
                    !string.IsNullOrWhiteSpace(baseName) &&
                    source.Female.HasValue &&
                    TryParseClass(source.Class, out botClass) &&
                    TryParseSkillTier(source.SkillTier, out skillTier) &&
                    !string.IsNullOrWhiteSpace(behavior) &&
                    BehaviorRegistry.KnownNames.Any(name =>
                        string.Equals(name, behavior, StringComparison.OrdinalIgnoreCase)) &&
                    source.ChatCategories != null)
                {
                    personas.Add(new PlayerGuildBotPersona(
                        id, baseName, source.Female.Value, botClass, skillTier,
                        behavior, categories));
                }
            }

            return errors.Count == 0
                ? new PlayerGuildBotRosterSnapshot(
                    document.Version.Value,
                    document.NameFormat.Trim(),
                    document.SpawnRadius.Value,
                    personas)
                : null;
        }

        private static bool TryParseClass(string text, out BotClass value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text) ||
                !Enum.TryParse(text.Trim(), true, out value) ||
                !Enum.IsDefined(typeof(BotClass), value))
            {
                return false;
            }

            // Crafter is a legacy value retained only for old bot data, not
            // a supported new roster class.
            return value != BotClass.Crafter;
        }

        private static bool TryParseSkillTier(string text, out BotSkillTier value)
        {
            value = default;
            return !string.IsNullOrWhiteSpace(text) &&
                   Enum.TryParse(text.Trim(), true, out value) &&
                   Enum.IsDefined(typeof(BotSkillTier), value);
        }

        private static bool IsCategoryIdentifier(string category)
        {
            foreach (var c in category)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                {
                    return false;
                }
            }
            return true;
        }

        private static void ValidateNameFormat(string format, List<string> errors)
        {
            int baseNameCount = 0;
            int guildTagCount = 0;

            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] == '}')
                {
                    errors.Add("'nameFormat' contains an unmatched '}'.");
                    continue;
                }
                if (format[i] != '{')
                {
                    continue;
                }

                int close = format.IndexOf('}', i + 1);
                if (close < 0)
                {
                    errors.Add("'nameFormat' contains an unmatched '{'.");
                    break;
                }

                var token = format.Substring(i + 1, close - i - 1);
                if (token == "BaseName")
                {
                    baseNameCount++;
                }
                else if (token == "GuildTag")
                {
                    guildTagCount++;
                }
                else
                {
                    errors.Add($"'nameFormat' contains unsupported placeholder '{{{token}}}'.");
                }
                i = close;
            }

            if (baseNameCount != 1)
            {
                errors.Add("'nameFormat' must contain {BaseName} exactly once.");
            }
            if (guildTagCount != 1)
            {
                errors.Add("'nameFormat' must contain {GuildTag} exactly once.");
            }
        }

        // ---- Persistent identity state ----------------------------------

        public static bool TryGetBinding(
            string guildId, string personaId, out PlayerGuildBotRosterBinding binding)
        {
            binding = null;
            var key = MakeKey(guildId, personaId);
            if (key == null)
            {
                return false;
            }

            lock (Sync)
            {
                var found = _state.Bindings.FirstOrDefault(b =>
                    string.Equals(MakeKey(b.GuildId, b.PersonaId), key,
                        StringComparison.OrdinalIgnoreCase));
                if (found == null)
                {
                    return false;
                }
                binding = found.Clone();
                return true;
            }
        }

        // Creates a binding only when state was read successfully. Existing
        // bindings always win, so config/name-format edits cannot rename a
        // persona that already has a stable identity.
        public static bool TryGetOrCreateBinding(
            string guildId,
            string personaId,
            string exactName,
            string homeCity,
            out PlayerGuildBotRosterBinding binding,
            out bool created)
        {
            binding = null;
            created = false;
            var key = MakeKey(guildId, personaId);
            if (key == null || string.IsNullOrWhiteSpace(exactName) ||
                string.IsNullOrWhiteSpace(homeCity))
            {
                return false;
            }

            lock (Sync)
            {
                if (!_stateLoadHealthy)
                {
                    return false;
                }

                var found = _state.Bindings.FirstOrDefault(b =>
                    string.Equals(MakeKey(b.GuildId, b.PersonaId), key,
                        StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    binding = found.Clone();
                    return true;
                }

                found = new PlayerGuildBotRosterBinding
                {
                    GuildId = guildId.Trim(),
                    PersonaId = personaId.Trim(),
                    ExactName = exactName.Trim(),
                    HomeCity = homeCity.Trim(),
                };
                _state.Bindings.Add(found);
                binding = found.Clone();
                created = true;
                return true;
            }
        }

        public static IReadOnlyList<PlayerGuildBotRosterBinding> GetBindings(string guildId)
        {
            if (string.IsNullOrWhiteSpace(guildId))
            {
                return Array.Empty<PlayerGuildBotRosterBinding>();
            }

            lock (Sync)
            {
                return _state.Bindings
                    .Where(b => string.Equals(b.GuildId?.Trim(), guildId.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.Clone())
                    .ToArray();
            }
        }

        // Retired persona bindings intentionally remain until guild disband,
        // so a later re-add can recover the same name and home city.
        public static bool RemoveGuildBindings(string guildId)
        {
            if (string.IsNullOrWhiteSpace(guildId))
            {
                return false;
            }

            lock (Sync)
            {
                int removed = _state.Bindings.RemoveAll(b =>
                    string.Equals(b.GuildId?.Trim(), guildId.Trim(),
                        StringComparison.OrdinalIgnoreCase));
                return removed > 0;
            }
        }

        public static bool SaveState()
        {
            lock (Sync)
            {
                if (!_stateLoadHealthy)
                {
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] ERROR: refusing to overwrite unreadable " +
                        $"state at {StatePath}; repair it before saving.");
                    return false;
                }
                return SaveStateLocked();
            }
        }

        private static bool SaveStateLocked()
        {
            string tempPath = null;
            try
            {
                var directory = Path.GetDirectoryName(StatePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                tempPath = StatePath + ".tmp";
                var json = JsonSerializer.Serialize(_state, JsonOptions);
                using (var stream = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(
                    stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(StatePath))
                {
                    File.Replace(tempPath, StatePath, null);
                }
                else
                {
                    File.Move(tempPath, StatePath);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[PlayerGuildBotRoster] ERROR: state write failed at {StatePath}: " +
                    ex.Message);
                return false;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath) && File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
        }

        private static void LoadState()
        {
            lock (Sync)
            {
                if (!File.Exists(StatePath))
                {
                    _state = NewState();
                    _stateLoadHealthy = true;
                    return;
                }

                try
                {
                    var text = File.ReadAllText(StatePath);
                    var errors = ValidateStateDocumentShape(text);
                    var loaded = errors.Count == 0
                        ? JsonSerializer.Deserialize<PlayerGuildBotRosterState>(text, JsonOptions)
                        : null;
                    if (loaded != null)
                    {
                        errors.AddRange(ValidateState(loaded));
                    }
                    if (errors.Count > 0)
                    {
                        _state = NewState();
                        _stateLoadHealthy = false;
                        foreach (var error in errors)
                        {
                            Console.WriteLine($"[PlayerGuildBotRoster] ERROR: {error}");
                        }
                        Console.WriteLine(
                            "[PlayerGuildBotRoster] state is unreadable; affected " +
                            "personas remain offline until it is repaired.");
                        return;
                    }

                    _state = loaded;
                    _stateLoadHealthy = true;
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] loaded {_state.Bindings.Count} persisted " +
                        "guild/persona binding(s).");
                }
                catch (Exception ex)
                {
                    _state = NewState();
                    _stateLoadHealthy = false;
                    Console.WriteLine(
                        $"[PlayerGuildBotRoster] ERROR: state read failed at {StatePath}: " +
                        ex.Message);
                    Console.WriteLine(
                        "[PlayerGuildBotRoster] state is unreadable; affected personas " +
                        "remain offline until it is repaired.");
                }
            }
        }

        private static List<string> ValidateStateDocumentShape(string text)
        {
            var errors = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    errors.Add("Persisted roster state must be a JSON object.");
                    return errors;
                }

                if (!HasProperty(document.RootElement, "version"))
                {
                    errors.Add("Persisted roster state is missing 'version'.");
                }
                if (!HasProperty(document.RootElement, "bindings"))
                {
                    errors.Add("Persisted roster state is missing 'bindings'.");
                }
            }
            catch (JsonException ex)
            {
                errors.Add($"Invalid JSON in {StatePath}: {ex.Message}");
            }
            return errors;
        }

        private static bool HasProperty(JsonElement objectElement, string name)
        {
            foreach (var property in objectElement.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static List<string> ValidateState(PlayerGuildBotRosterState state)
        {
            var errors = new List<string>();
            if (state == null)
            {
                errors.Add("Persisted roster state is empty.");
                return errors;
            }
            if (state.Version != CurrentStateVersion)
            {
                errors.Add(
                    $"Unsupported roster state version {state.Version}; " +
                    $"expected {CurrentStateVersion}.");
            }
            if (state.Bindings == null)
            {
                errors.Add("Persisted roster state is missing 'bindings'.");
                return errors;
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < state.Bindings.Count; i++)
            {
                var binding = state.Bindings[i];
                if (binding == null)
                {
                    errors.Add($"Persisted binding[{i}] is null.");
                    continue;
                }

                var key = MakeKey(binding.GuildId, binding.PersonaId);
                if (key == null)
                {
                    errors.Add($"Persisted binding[{i}] is missing guildId or personaId.");
                }
                else if (!keys.Add(key))
                {
                    errors.Add(
                        $"Persisted binding[{i}] duplicates guildId/personaId '{key}'.");
                }

                if (string.IsNullOrWhiteSpace(binding.ExactName))
                {
                    errors.Add($"Persisted binding[{i}] is missing exactName.");
                }
                if (string.IsNullOrWhiteSpace(binding.HomeCity))
                {
                    errors.Add($"Persisted binding[{i}] is missing homeCity.");
                }
            }
            return errors;
        }

        private static PlayerGuildBotRosterState NewState() =>
            new() { Version = CurrentStateVersion, Bindings = new List<PlayerGuildBotRosterBinding>() };

        private static string MakeKey(string guildId, string personaId)
        {
            if (string.IsNullOrWhiteSpace(guildId) || string.IsNullOrWhiteSpace(personaId))
            {
                return null;
            }
            return $"{guildId.Trim()}\u001f{personaId.Trim()}";
        }
    }

    public sealed class PlayerGuildBotGuild
    {
        public PlayerGuildBotGuild(
            string id,
            string tag,
            bool isActive = true,
            Func<IReadOnlyList<Mobile>> memberProvider = null,
            IReadOnlyList<Mobile> members = null)
        {
            Id = id;
            Tag = tag;
            IsActive = isActive;
            MemberProvider = memberProvider;
            Members = members;
        }

        public string Id { get; }
        public string Tag { get; }
        public bool IsActive { get; }
        public Func<IReadOnlyList<Mobile>> MemberProvider { get; }
        public IReadOnlyList<Mobile> Members { get; }
    }

    internal sealed class ChatResponder
    {
        public ChatResponder(PlayerGuildBotPersona persona)
        {
            Persona = persona;
        }

        public PlayerGuildBotPersona Persona { get; }
    }

    public sealed class RosterLoadResult
    {
        internal RosterLoadResult(
            bool success, int version, int personaCount, IReadOnlyList<string> errors)
        {
            Success = success;
            Version = version;
            PersonaCount = personaCount;
            Errors = errors ?? Array.Empty<string>();
        }

        public bool Success { get; }
        public int Version { get; }
        public int PersonaCount { get; }
        public IReadOnlyList<string> Errors { get; }
    }

    public sealed class RosterReconcileResult
    {
        internal RosterReconcileResult(
            int activeGuildCount,
            int configuredPersonaCount,
            int createdCount,
            int retiredCount,
            int skippedCount,
            int onlineCount,
            int offlineCount,
            int reservedNameCount)
        {
            ActiveGuildCount = activeGuildCount;
            ConfiguredPersonaCount = configuredPersonaCount;
            CreatedCount = createdCount;
            RetiredCount = retiredCount;
            SkippedCount = skippedCount;
            OnlineCount = onlineCount;
            OfflineCount = offlineCount;
            ReservedNameCount = reservedNameCount;
        }

        public static RosterReconcileResult Empty { get; } =
            new(0, 0, 0, 0, 0, 0, 0, NamePool.ReservedCount);

        public int ActiveGuildCount { get; }
        public int ConfiguredPersonaCount { get; }
        public int CreatedCount { get; }
        public int RetiredCount { get; }
        public int SkippedCount { get; }
        public int OnlineCount { get; }
        public int OfflineCount { get; }
        public int ReservedNameCount { get; }
    }

    public sealed class PlayerGuildBotRosterView
    {
        internal PlayerGuildBotRosterView(
            PlayerGuildBotGuild guild,
            IReadOnlyList<PlayerGuildBotRosterRow> personas)
        {
            Guild = guild;
            Personas = personas;
        }

        public PlayerGuildBotGuild Guild { get; }
        public IReadOnlyList<PlayerGuildBotRosterRow> Personas { get; }
    }

    public sealed class PlayerGuildBotRosterRow
    {
        internal PlayerGuildBotRosterRow(
            string personaId,
            string exactName,
            BotClass botClass,
            BotSkillTier skillTier,
            string behavior,
            string homeCity,
            bool isOnline)
        {
            PersonaId = personaId;
            ExactName = exactName;
            Class = botClass;
            SkillTier = skillTier;
            Behavior = behavior;
            HomeCity = homeCity;
            IsOnline = isOnline;
        }

        public string PersonaId { get; }
        public string ExactName { get; }
        public BotClass Class { get; }
        public BotSkillTier SkillTier { get; }
        public string Behavior { get; }
        public string HomeCity { get; }
        public bool IsOnline { get; }
    }

    public sealed class PlayerGuildBotRosterSnapshot
    {
        private readonly Dictionary<string, PlayerGuildBotPersona> _byId;

        internal PlayerGuildBotRosterSnapshot(
            int version,
            string nameFormat,
            int spawnRadius,
            IReadOnlyList<PlayerGuildBotPersona> personas)
        {
            Version = version;
            NameFormat = nameFormat;
            SpawnRadius = spawnRadius;
            Personas = personas;
            _byId = personas.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        }

        public int Version { get; }
        public string NameFormat { get; }
        public int SpawnRadius { get; }
        public IReadOnlyList<PlayerGuildBotPersona> Personas { get; }

        public bool TryGetPersona(string id, out PlayerGuildBotPersona persona) =>
            _byId.TryGetValue(id ?? "", out persona);

        public string FormatName(string baseName, string guildTag) =>
            NameFormat
                .Replace("{BaseName}", baseName ?? "", StringComparison.Ordinal)
                .Replace("{GuildTag}", guildTag ?? "", StringComparison.Ordinal)
                .Trim();
    }

    public sealed class PlayerGuildBotPersona
    {
        internal PlayerGuildBotPersona(
            string id,
            string baseName,
            bool female,
            BotClass botClass,
            BotSkillTier skillTier,
            string behavior,
            IReadOnlyList<string> chatCategories)
        {
            Id = id;
            BaseName = baseName;
            Female = female;
            Class = botClass;
            SkillTier = skillTier;
            Behavior = behavior;
            ChatCategories = chatCategories;
        }

        public string Id { get; }
        public string BaseName { get; }
        public bool Female { get; }
        public BotClass Class { get; }
        public BotSkillTier SkillTier { get; }
        public string Behavior { get; }
        public IReadOnlyList<string> ChatCategories { get; }
    }

    public sealed class PlayerGuildBotRosterState
    {
        public int Version { get; set; } = PlayerGuildBotRoster.CurrentStateVersion;
        public List<PlayerGuildBotRosterBinding> Bindings { get; set; } = new();
    }

    public sealed class PlayerGuildBotRosterBinding
    {
        public string GuildId { get; set; }
        public string PersonaId { get; set; }
        public string ExactName { get; set; }
        public string HomeCity { get; set; }

        internal PlayerGuildBotRosterBinding Clone() => new()
        {
            GuildId = GuildId,
            PersonaId = PersonaId,
            ExactName = ExactName,
            HomeCity = HomeCity,
        };
    }

    // DTOs deliberately keep required scalar values nullable. System.Text.Json
    // otherwise turns missing false/zero values into plausible-looking values,
    // making malformed configuration indistinguishable from valid data.
    internal sealed class RosterDocument
    {
        public int? Version { get; set; }
        public string NameFormat { get; set; }
        public int? SpawnRadius { get; set; }
        public List<RosterPersonaDocument> Personas { get; set; }
    }

    internal sealed class RosterPersonaDocument
    {
        public string Id { get; set; }
        public string BaseName { get; set; }
        public bool? Female { get; set; }
        public string Class { get; set; }
        public string SkillTier { get; set; }
        public string Behavior { get; set; }
        public List<string> ChatCategories { get; set; }
    }
}
