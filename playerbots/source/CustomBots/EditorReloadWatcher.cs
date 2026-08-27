// =========================================================================
// EditorReloadWatcher.cs — lets the map editor's buttons act on the running
// game without typing commands in the client.
//
// Two file-token bridges (same one-way idiom as the LiveMap snapshot):
//
//   "Reload in game"  -> Data/Live/reload_request.txt
//        Reloads waypoints, destinations (with arrival points), and zones
//        (areas). Cheap; data only. Writes Data/Live/reload_ack.json.
//
//   "Regenerate bots" -> Data/Live/genbots_request.txt
//        Clears and re-lays the whole bot population (= [GenerateBots), so
//        bank/shop crowds move onto newly-placed arrival points. Heavier.
//        Writes Data/Live/genbots_ack.json.
//
// serve_map.py bumps a token; this watcher polls every couple seconds and
// acts when a token changes, then writes an ack the editor reads back.
// =========================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Server;

namespace Server.CustomBots
{
    public static class EditorReloadWatcher
    {
        private static string Live(string file) =>
            Path.Combine(Core.BaseDirectory, "Data", "Live", file);

        private static readonly string ReloadReq = Live("reload_request.txt");
        private static readonly string ReloadAck = Live("reload_ack.json");
        private static readonly string GenReq    = Live("genbots_request.txt");
        private static readonly string GenAck    = Live("genbots_ack.json");
        private static readonly string AuditReq  = Live("audit_request.txt");
        private static readonly string AuditAck  = Live("audit_report.json");
        private static readonly string WalkReq   = Live("walkmap_request.txt");
        private static readonly string WalkAck   = Live("walkmap.pgm");
        private static readonly string PartyReq  = Live("party_request.txt");
        private static readonly string PartyAck  = Live("party_ack.json");
        private static readonly string DeathReq  = Live("death_request.txt");
        private static readonly string DeathAck  = Live("death_ack.json");
        private static readonly string FactionReq = Live("faction_request.txt");
        private static readonly string FactionAck = Live("faction_ack.json");
        private static readonly string LiveMapReq = Live("livemap_request.txt");
        private static readonly string LiveMapAck = Live("livemap_ack.json");
        private static readonly string PKsReq = Live("pks_request.txt");
        private static readonly string PKsAck = Live("pks_ack.json");
        private static readonly string ThuntReq = Live("thunt_request.txt");
        private static readonly string ThuntAck = Live("thunt_ack.json");
        private static readonly string ShopReq = Live("shop_request.txt");
        private static readonly string ShopAck = Live("shop_ack.json");
        private static readonly string SosReq = Live("sos_request.txt");
        private static readonly string SosAck = Live("sos_ack.json");
        private static readonly string TameReq = Live("tame_request.txt");
        private static readonly string TameAck = Live("tame_ack.json");
        private static readonly string HousesReq = Live("houses_request.txt");
        private static readonly string HousesAck = Live("houses_ack.json");
        private static readonly string GotoReq = Live("goto_request.txt");
        private static readonly string GotoAck = Live("goto_ack.json");
        private static readonly string GossipReq = Live("gossip_request.txt");
        private static readonly string GossipAck = Live("gossip_ack.json");
        private static readonly string PadReq = Live("padaudit_request.txt");
        private static readonly string PadAck = Live("padaudit_report.json");
        // say_request.txt: "token x y text..." — spawns a throwaway REAL
        // PlayerMobile at (x,y) that says the text, then deletes. Purely a
        // headless test rig for player-facing reactions (speech responder).
        private static readonly string SayReq = Live("say_request.txt");
        // party_request.txt: "token x y" — rig player at (x,y) invites the
        // nearest eligible bot to a party, walks a short route while the
        // bot follows, then vanishes (testing the leader-gone path too).
        private static readonly string PartyTestReq = Live("partytest_request.txt");
        // t2a_request.txt: "token" — spawn one throwaway GM bot per class
        // (plus extra Mage rolls so the tank-mage variants show), dump
        // stats + skills to console, verify the T2A caps, delete them.
        private static readonly string T2AReq = Live("t2a_request.txt");
        // pkfresh_request.txt: "token" — delete the live PK BOTS (keeping
        // their spawners) and force an immediate respawn, so the roads
        // refill with reds rolled under the current PK templates.
        private static readonly string PKFreshReq = Live("pkfresh_request.txt");
        // guildbots_request.txt: one monotonically increasing token. The
        // watcher runs a disposable player-guild scenario and writes a compact
        // pass/fail report to guildbots_ack.json.
        private static readonly string GuildBotsReq = Live("guildbots_request.txt");
        private static readonly string GuildBotsAck = Live("guildbots_ack.json");

        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
        private static long _lastReload = -1;
        private static long _lastGen = -1;
        private static long _lastAudit = -1;
        private static long _lastWalk = -1;
        private static long _lastParty = -1;
        private static long _lastDeath = -1;
        private static long _lastFaction = -1;
        private static long _lastLiveMap = -1;
        private static long _lastPKs = -1;
        private static long _lastThunt = -1;
        private static long _lastShop = -1;
        private static long _lastSos = -1;
        private static long _lastTame = -1;
        private static long _lastHouses = -1;
        private static long _lastGoto = -1;
        private static long _lastGossip = -1;
        private static long _lastPad = -1;
        private static long _lastSay = -1;
        private static long _lastPartyTest = -1;
        private static long _lastT2A = -1;
        private static long _lastPKFresh = -1;
        private static long _lastGuildBots = -1;
        private static GuildBotsScenario _guildBotsScenario;
        private static Timer _timer;

        // ModernUO calls Initialize() after the world loads — registries and
        // spawners exist by then, so reload/regen are safe.
        public static void Initialize()
        {
            // Seed from existing tokens so stale files at boot don't trigger.
            _lastReload = ReadToken(ReloadReq) ?? 0;
            _lastGen    = ReadToken(GenReq) ?? 0;
            _lastAudit  = ReadToken(AuditReq) ?? 0;
            _lastWalk   = ReadWalkRequest(out _, out _, out _, out _) ?? 0;
            _lastParty  = ReadToken(PartyReq) ?? 0;
            _lastDeath  = ReadToken(DeathReq) ?? 0;
            _lastFaction = ReadToken(FactionReq) ?? 0;
            _lastLiveMap = ReadLiveMapRequest(out _) ?? 0;
            _lastPKs = ReadToken(PKsReq) ?? 0;
            _lastThunt = ReadToken(ThuntReq) ?? 0;
            _lastShop = ReadToken(ShopReq) ?? 0;
            _lastSos = ReadToken(SosReq) ?? 0;
            _lastTame = ReadToken(TameReq) ?? 0;
            _lastHouses = ReadHousesRequest(out _, out _) ?? 0;
            _lastGoto = ReadGotoRequest(out _) ?? 0;
            _lastGossip = ReadToken(GossipReq) ?? 0;
            _lastPad = ReadToken(PadReq) ?? 0;
            _lastSay = ReadSayRequest(out _, out _, out _) ?? 0;
            _lastPartyTest = ReadCoordRequest(PartyTestReq, out _) ?? 0;
            _lastT2A = ReadToken(T2AReq) ?? 0;
            _lastPKFresh = ReadToken(PKFreshReq) ?? 0;
            // Seed stale editor tokens at boot; only a changed token starts a
            // scenario, matching every other one-way watcher bridge.
            _lastGuildBots = ReadGuildBotsRequest() ?? 0;
            _timer = Timer.DelayCall(Interval, Interval, Poll);
        }

        private static long? ReadToken(string path)
        {
            try
            {
                if (File.Exists(path) &&
                    long.TryParse(File.ReadAllText(path).Trim(), out var t))
                {
                    return t;
                }
            }
            catch
            {
                // file may be mid-write; retry next tick
            }
            return null;
        }

        private static void Poll()
        {
            var reload = ReadToken(ReloadReq);
            if (reload != null && reload.Value != _lastReload)
            {
                _lastReload = reload.Value;
                DoReload(reload.Value);
            }

            var gen = ReadToken(GenReq);
            if (gen != null && gen.Value != _lastGen)
            {
                _lastGen = gen.Value;
                DoRegen(gen.Value);
            }

            var audit = ReadToken(AuditReq);
            if (audit != null && audit.Value != _lastAudit)
            {
                _lastAudit = audit.Value;
                DoAudit(audit.Value);
            }

            var walk = ReadWalkRequest(out int wx0, out int wy0, out int wx1, out int wy1);
            if (walk != null && walk.Value != _lastWalk)
            {
                _lastWalk = walk.Value;
                DoWalkmap(walk.Value, wx0, wy0, wx1, wy1);
            }

            var partyTok = ReadToken(PartyReq);
            if (partyTok != null && partyTok.Value != _lastParty)
            {
                _lastParty = partyTok.Value;
                DoFormParty(partyTok.Value);
            }

            var deathTok = ReadToken(DeathReq);
            if (deathTok != null && deathTok.Value != _lastDeath)
            {
                _lastDeath = deathTok.Value;
                DoTestDeath(deathTok.Value);
            }

            var factionTok = ReadToken(FactionReq);
            if (factionTok != null && factionTok.Value != _lastFaction)
            {
                _lastFaction = factionTok.Value;
                DoTestFactionFight(factionTok.Value);
            }

            var gossipTok = ReadToken(GossipReq);
            if (gossipTok != null && gossipTok.Value != _lastGossip)
            {
                _lastGossip = gossipTok.Value;
                DoTestGossip(gossipTok.Value);
            }

            var liveTok = ReadLiveMapRequest(out double liveSecs);
            if (liveTok != null && liveTok.Value != _lastLiveMap)
            {
                _lastLiveMap = liveTok.Value;
                DoLiveMap(liveTok.Value, liveSecs);
            }

            var pksTok = ReadToken(PKsReq);
            if (pksTok != null && pksTok.Value != _lastPKs)
            {
                _lastPKs = pksTok.Value;
                DoGenPKs(pksTok.Value);
            }

            var thuntTok = ReadToken(ThuntReq);
            if (thuntTok != null && thuntTok.Value != _lastThunt)
            {
                _lastThunt = thuntTok.Value;
                DoTestThunt(thuntTok.Value);
            }

            var shopTok = ReadToken(ShopReq);
            if (shopTok != null && shopTok.Value != _lastShop)
            {
                _lastShop = shopTok.Value;
                DoTestShop(shopTok.Value);
            }

            var sosTok = ReadToken(SosReq);
            if (sosTok != null && sosTok.Value != _lastSos)
            {
                _lastSos = sosTok.Value;
                DoTestSos(sosTok.Value);
            }

            var tameTok = ReadToken(TameReq);
            if (tameTok != null && tameTok.Value != _lastTame)
            {
                _lastTame = tameTok.Value;
                DoTestTame(tameTok.Value);
            }

            var housesTok = ReadHousesRequest(out string housesOp, out int housesCount);
            if (housesTok != null && housesTok.Value != _lastHouses)
            {
                _lastHouses = housesTok.Value;
                DoHouses(housesTok.Value, housesOp, housesCount);
            }

            var gotoTok = ReadGotoRequest(out string gotoDest);
            if (gotoTok != null && gotoTok.Value != _lastGoto)
            {
                _lastGoto = gotoTok.Value;
                DoGoto(gotoTok.Value, gotoDest);
            }

            var padTok = ReadToken(PadReq);
            if (padTok != null && padTok.Value != _lastPad)
            {
                _lastPad = padTok.Value;
                DoPadAudit(padTok.Value);
            }

            var sayTok = ReadSayRequest(out var sayLoc, out var sayText, out _);
            if (sayTok != null && sayTok.Value != _lastSay)
            {
                _lastSay = sayTok.Value;
                DoSay(sayLoc, sayText);
            }

            var ptTok = ReadCoordRequest(PartyTestReq, out var ptLoc);
            if (ptTok != null && ptTok.Value != _lastPartyTest)
            {
                _lastPartyTest = ptTok.Value;
                DoPartyTest(ptLoc);
            }

            var t2aTok = ReadToken(T2AReq);
            if (t2aTok != null && t2aTok.Value != _lastT2A)
            {
                _lastT2A = t2aTok.Value;
                DoT2AAudit(t2aTok.Value);
            }

            var pkFreshTok = ReadToken(PKFreshReq);
            if (pkFreshTok != null && pkFreshTok.Value != _lastPKFresh)
            {
                _lastPKFresh = pkFreshTok.Value;
                DoPKFresh(pkFreshTok.Value);
            }

            var guildBotsTok = ReadGuildBotsRequest();
            if (guildBotsTok != null && guildBotsTok.Value != _lastGuildBots)
            {
                _lastGuildBots = guildBotsTok.Value;
                DoGuildBots(guildBotsTok.Value);
            }
        }

        private static long? ReadGuildBotsRequest()
        {
            try
            {
                if (!File.Exists(GuildBotsReq))
                {
                    return null;
                }
                var parts = File.ReadAllText(GuildBotsReq).Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 0 && long.TryParse(parts[0], out var token)
                    ? token
                    : null;
            }
            catch
            {
                return null; // file may be mid-write; retry next tick
            }
        }

        private static void DoGuildBots(long token)
        {
            if (_guildBotsScenario != null)
            {
                const string error = "scenario already running; retry after its ack is written";
                Console.WriteLine(
                    $"[EditorReload] guildbots token {token} ignored: {error}.");
                WriteAck(GuildBotsAck,
                    $"{{\"token\":{token},\"passed\":false,\"errors\":[\"{error}\"]}}");
                return;
            }
            _guildBotsScenario = new GuildBotsScenario(token);
            _guildBotsScenario.Start();
        }

        // pkfresh_request.txt: cull the live PK bots (spawners untouched)
        // and respawn them immediately — the fresh reds roll the current
        // PK templates. Unlike pks_request this never re-places spawners,
        // so it's safe when pk_spawns.json is empty.
        private static void DoPKFresh(long token)
        {
            var bots = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot b && !b.Deleted &&
                    !b.IsPlayerGuildBot && b.Behavior is PKBehavior)
                {
                    bots.Add(b);
                }
            }
            foreach (var b in bots)
            {
                try { b.Delete(); } catch { }
            }

            // Collect FIRST, respawn after — Respawn() creates bots whose
            // equipment lands in World.Items, and mutating the collection
            // mid-enumeration is a hard crash.
            var spawners = new List<PlayerBotSpawner>();
            foreach (var item in World.Items.Values)
            {
                if (item is PlayerBotSpawner sp && !sp.Deleted &&
                    sp.BehaviorName == "PK")
                {
                    spawners.Add(sp);
                }
            }
            foreach (var sp in spawners)
            {
                try { sp.Respawn(); } catch { }
            }

            Console.WriteLine(
                $"[EditorReload] pkfresh: culled {bots.Count} red(s), " +
                $"respawned {spawners.Count} PK spawner(s) (token {token}).");
        }

        // -------------------------------------------------------------------
        // t2a_request.txt: headless audit of the T2A stat/skill templates.
        // Spawns one throwaway GRANDMASTER bot per class — Mage six times
        // so the tank-mage weapon variants show up — prints each bot's
        // stats, template skills, and held weapon, checks the era caps
        // (every stat <= 100, total <= 225), then deletes the bots.
        // -------------------------------------------------------------------
        private static void DoT2AAudit(long token)
        {
            Console.WriteLine($"[t2a] template audit (token {token})");
            bool allOk = true;

            var classes = new[]
            {
                BotClass.Warrior,
                BotClass.Mage, BotClass.Mage, BotClass.Mage,
                BotClass.Mage, BotClass.Mage, BotClass.Mage,
                BotClass.Fencer, BotClass.Archer, BotClass.Tamer,
                BotClass.Smith, BotClass.Tailor, BotClass.Fisherman,
                BotClass.Carpenter,
                BotClass.Healer, BotClass.Thief, BotClass.Bard,
                BotClass.Ranger, BotClass.Lumberjack, BotClass.Miner,
                BotClass.TreasureHunter, BotClass.Merchant,
            };

            foreach (var cls in classes)
            {
                PlayerBot bot = null;
                try
                {
                    bot = new PlayerBot(cls, BotSkillTier.Grandmaster);

                    int sum = bot.RawStr + bot.RawDex + bot.RawInt;
                    bool ok = bot.RawStr <= 100 && bot.RawDex <= 100 &&
                              bot.RawInt <= 100 && sum <= 225;
                    if (!ok)
                    {
                        allOk = false;
                    }

                    string skills = "";
                    for (int i = 0; i < bot.Skills.Length; i++)
                    {
                        var sk = bot.Skills[i];
                        if (sk.Base > 0)
                        {
                            skills += $"{(skills.Length > 0 ? ", " : "")}{sk.SkillName} {sk.Base:F1}";
                        }
                    }

                    var weap = bot.FindItemOnLayer(Layer.TwoHanded) as Items.BaseWeapon
                            ?? bot.FindItemOnLayer(Layer.OneHanded) as Items.BaseWeapon;

                    string weapDesc = weap == null ? "none"
                        : weap.DamageLevel != Items.WeaponDamageLevel.Regular
                            ? $"{weap.GetType().Name}[{weap.AccuracyLevel}/{weap.DamageLevel}]"
                        : weap.Quality == Items.WeaponQuality.Exceptional
                            ? $"{weap.GetType().Name}[exc]"
                            : weap.GetType().Name;

                    Console.WriteLine(
                        $"[t2a] {cls,-10} {bot.RawStr}/{bot.RawDex}/{bot.RawInt} " +
                        $"(sum {sum}{(ok ? "" : " CAP VIOLATION")}) " +
                        $"weapon={weapDesc} | {skills}");

                    // PvP-kit classes: dump the pack so the 1999 loadout is
                    // verifiable headless (potion battery, trapped pouches,
                    // bandages, reagent depth, spare weapons).
                    if (cls is BotClass.Warrior or BotClass.Mage or BotClass.Lumberjack &&
                        bot.Backpack != null)
                    {
                        var tally = new Dictionary<string, int>();
                        int pouches = 0, armorPieces = 0, excArmor = 0;
                        foreach (var it in bot.Backpack.Items)
                        {
                            if (it is Items.Pouch p && p.TrapType == Items.TrapType.MagicTrap)
                            {
                                pouches++;
                                continue;
                            }
                            var key = it.GetType().Name;
                            tally[key] = tally.GetValueOrDefault(key) + Math.Max(1, it.Amount);
                        }
                        foreach (var it in bot.Items)
                        {
                            if (it is Items.BaseArmor ar)
                            {
                                armorPieces++;
                                if (ar.Quality == Items.ArmorQuality.Exceptional) excArmor++;
                            }
                        }
                        string packStr = "";
                        foreach (var (k, v) in tally)
                        {
                            packStr += $"{(packStr.Length > 0 ? ", " : "")}{k} x{v}";
                        }
                        Console.WriteLine(
                            $"[t2a]   pack: trappedPouches x{pouches}, " +
                            $"armor {excArmor}/{armorPieces} exceptional | {packStr}");
                    }
                }
                catch (Exception ex)
                {
                    allOk = false;
                    Console.WriteLine($"[t2a] {cls}: FAILED — {ex.Message}");
                }
                finally
                {
                    bot?.Delete();
                }
            }

            Console.WriteLine(allOk
                ? "[t2a] PASS — all bots within T2A caps (stats <= 100, totals <= 225)"
                : "[t2a] FAIL — cap violations above");
        }

        // "token x y" file reader shared by simple coordinate rigs.
        private static long? ReadCoordRequest(string path, out Point3D loc)
        {
            loc = Point3D.Zero;
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                var parts = File.ReadAllText(path).Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !long.TryParse(parts[0], out var t) ||
                    !int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var y))
                {
                    return null;
                }
                int z = Walkable.TryFindSeedZ(Map.Felucca, x, y, 0, out var seedZ)
                    ? seedZ
                    : Map.Felucca.GetAverageZ(x, y);
                loc = new Point3D(x, y, z);
                return t;
            }
            catch
            {
                return null;
            }
        }

        // Headless E2E for player-led parties: rig player appears, invites
        // the nearest eligible bot, walks a route while logging the bot's
        // follow distance, then vanishes — the follower should bail out
        // gracefully via the leader-gone path.
        private static void DoPartyTest(Point3D loc)
        {
            try
            {
                var map = Map.Felucca;
                var rig = new Server.Mobiles.PlayerMobile
                {
                    Name = "Party Tester",
                    Body = 0x190,
                    Hue = 0x83EA,
                    Player = true,
                };
                rig.MoveToWorld(loc, map);

                PlayerBot pick = null;
                int best = int.MaxValue;
                foreach (var m in map.GetMobilesInRange(loc, 25))
                {
                    if (m is PlayerBot b && !b.Deleted && b.Alive &&
                        !b.LifecycleExempt && b.Party == null &&
                        !BotPartyManager.IsInParty(b) &&
                        !BotClassHelper.IsArtisan(b.Class) &&
                        !BotClassHelper.IsGatherer(b.Class) &&
                        b.Class != BotClass.Crafter &&
                        b.Behavior is TravelerBehavior or IdleBehavior
                                   or WanderBehavior or AdventurerBehavior)
                    {
                        int d = (int)b.GetDistanceToSqrt(loc);
                        if (d < best)
                        {
                            best = d;
                            pick = b;
                        }
                    }
                }

                if (pick == null)
                {
                    Console.WriteLine("[partytest] no eligible bot within 25 tiles");
                    rig.Delete();
                    return;
                }

                Console.WriteLine($"[partytest] rig at ({loc.X},{loc.Y}) inviting {pick.Name}");
                Server.Engines.PartySystem.Party.Invite(rig, pick);

                // Walk a square-ish route; log the follower's distance.
                for (int step = 1; step <= 8; step++)
                {
                    int s = step;
                    Timer.DelayCall(TimeSpan.FromSeconds(8 + s * 6), () =>
                    {
                        if (rig.Deleted)
                        {
                            return;
                        }
                        int dx = s <= 4 ? 5 : 0;
                        int dy = s <= 4 ? 0 : 5;
                        var next = new Point3D(rig.X + dx, rig.Y + dy, rig.Z);
                        if (Walkable.TryFindSeedZ(map, next.X, next.Y, rig.Z, out var nz))
                        {
                            next = new Point3D(next.X, next.Y, nz);
                            rig.MoveToWorld(next, map);
                        }
                        int dist = pick.Deleted ? -1 : (int)pick.GetDistanceToSqrt(rig.Location);
                        Console.WriteLine(
                            $"[partytest] hop {s}: rig ({rig.X},{rig.Y}), " +
                            $"{pick.Name} dist={dist}, behavior={pick.Behavior?.SerializableName}");
                    });
                }

                Timer.DelayCall(TimeSpan.FromSeconds(70), () =>
                {
                    Console.WriteLine("[partytest] rig vanishing (leader-gone path)");
                    if (!rig.Deleted)
                    {
                        rig.Delete();
                    }
                });
                Timer.DelayCall(TimeSpan.FromSeconds(80), () =>
                {
                    Console.WriteLine(
                        $"[partytest] final: {pick.Name} behavior=" +
                        $"{pick.Behavior?.SerializableName}, party={(pick.Party == null ? "none" : "set")}");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[partytest] failed: {ex.Message}");
            }
        }

        // say_request.txt: "token x y text..." — a throwaway real player
        // stands at (x,y), says the text, and vanishes 6s later. Lets the
        // speech responder be tested without a client.
        private static long? ReadSayRequest(out Point3D loc, out string text, out bool ok)
        {
            loc = Point3D.Zero;
            text = "";
            ok = false;
            try
            {
                if (!File.Exists(SayReq))
                {
                    return null;
                }
                var parts = File.ReadAllText(SayReq).Split(
                    new[] { ' ', '\t' }, 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || !long.TryParse(parts[0], out var t) ||
                    !int.TryParse(parts[1], out var x) || !int.TryParse(parts[2], out var y))
                {
                    return null;
                }
                // Standable Z, not land Z — indoors the floor is a static
                // above the terrain, and a speaker sunk into it fails every
                // listener's CanSee/LOS check (nobody "hears" a voice from
                // inside the foundations).
                int z = Walkable.TryFindSeedZ(Map.Felucca, x, y, 0, out var seedZ)
                    ? seedZ
                    : Map.Felucca.GetAverageZ(x, y);
                loc = new Point3D(x, y, z);
                text = parts[3].Trim();
                ok = true;
                return t;
            }
            catch
            {
                return null;
            }
        }

        private static void DoSay(Point3D loc, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            try
            {
                var m = new Server.Mobiles.PlayerMobile
                {
                    Name = "Test Player",
                    Body = 0x190,
                    Hue = 0x83EA,
                    // Login normally stamps this; without it every
                    // HandlesOnSpeech(from) gate sees an NPC and the
                    // speech is never delivered to any listener.
                    Player = true,
                };
                m.MoveToWorld(loc, Map.Felucca);
                m.DoSpeech(text, Array.Empty<int>(), MessageType.Regular, 0x3B2);
                Console.WriteLine($"[EditorReload] say-rig at ({loc.X},{loc.Y}): \"{text}\"");
                // Long enough for slow reactions to play out (a party
                // invite chain takes ~8s end to end).
                Timer.DelayCall(TimeSpan.FromSeconds(30), () =>
                {
                    if (!m.Deleted)
                    {
                        m.Delete();
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] say-rig failed: {ex.Message}");
            }
        }

        // padaudit_request.txt: functional teleporter-pad audit � walks a
        // probe onto every dungeon entrance/descend/ascend pad.
        private static void DoPadAudit(long token)
        {
            List<string> findings;
            try
            {
                findings = BotPadAudit.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] pad audit: {ex.Message}");
                WriteAck(PadAck, $"{{\"token\":{token},\"error\":\"{ex.Message.Replace("\"", "'")}\"}}");
                return;
            }
            var items = new List<string>();
            foreach (var f in findings)
            {
                items.Add("\"" + f.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
            }
            WriteAck(PadAck,
                $"{{\"token\":{token},\"findings\":[{string.Join(",", items)}]}}");
        }

        // goto_request.txt: "<token> <destination name>" � send a random
        // eligible bot traveling to the named destination. Headless way to
        // exercise a specific route (island ferries, new trails) on demand.
        private static long? ReadGotoRequest(out string dest)
        {
            dest = null;
            try
            {
                if (!File.Exists(GotoReq))
                {
                    return null;
                }
                var text = File.ReadAllText(GotoReq).Trim();
                int sp = text.IndexOf(' ');
                if (sp <= 0 || !long.TryParse(text[..sp], out var t))
                {
                    return null;
                }
                dest = text[(sp + 1)..].Trim();
                return t;
            }
            catch
            {
                return null;
            }
        }

        private static void DoGoto(long token, string destName)
        {
            var dest = destName != null ? DestinationCatalog.GetByName(destName) : null;
            if (dest == null)
            {
                Console.WriteLine($"[EditorReload] goto: unknown destination '{destName}'");
                WriteAck(GotoAck, $"{{\"token\":{token},\"sent\":false}}");
                return;
            }

            // Nearest eligible bot to the destination � exercises the
            // interesting LAST leg of a route instead of a cross-map trek.
            PlayerBot pick = null;
            int best = int.MaxValue;
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Alive &&
                    !bot.LifecycleExempt && !bot.LoggingOut &&
                    !bot.CorpseRunPending && bot.Combatant == null &&
                    !BotPartyManager.IsInParty(bot) &&
                    !DungeonRegistry.IsInDungeon(bot) &&
                    (bot.Behavior is TravelerBehavior or BankSitterBehavior
                                  or IdleBehavior or WanderBehavior))
                {
                    int d = Math.Max(Math.Abs(bot.X - dest.Location.X),
                                     Math.Abs(bot.Y - dest.Location.Y));
                    if (d < best)
                    {
                        best = d;
                        pick = bot;
                    }
                }
            }

            if (pick == null)
            {
                WriteAck(GotoAck, $"{{\"token\":{token},\"sent\":false}}");
                return;
            }

            pick.Behavior = new TravelerBehavior { DestinationName = dest.Name };
            Console.WriteLine($"[goto] {pick.Name} sent to '{dest.Name}'");
            WriteAck(GotoAck,
                $"{{\"token\":{token},\"sent\":true," +
                $"\"name\":\"{pick.Name.Replace("\"", "\\\"")}\"}}");
        }

        // houses_request.txt: "<token> scatter <n>" or "<token> clear" �
        // headless [BotHouses for the housing spike.
        private static long? ReadHousesRequest(out string op, out int count)
        {
            op = "scatter";
            count = 50;
            try
            {
                if (!File.Exists(HousesReq))
                {
                    return null;
                }
                var parts = File.ReadAllText(HousesReq).Trim().Split(' ',
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !long.TryParse(parts[0], out var t))
                {
                    return null;
                }
                if (parts.Length > 1)
                {
                    op = parts[1].ToLowerInvariant();
                }
                if (parts.Length > 2 && int.TryParse(parts[2], out var n))
                {
                    count = n;
                }
                return t;
            }
            catch
            {
                return null;
            }
        }

        private static void DoHouses(long token, string op, int count)
        {
            try
            {
                if (op == "clear")
                {
                    int removed = BotHousing.Clear();
                    WriteAck(HousesAck,
                        $"{{\"token\":{token},\"op\":\"clear\",\"removed\":{removed}}}");
                    return;
                }
                int placed = BotHousing.Scatter(Map.Felucca, count, out var ms, out var tried);
                WriteAck(HousesAck,
                    $"{{\"token\":{token},\"op\":\"scatter\",\"placed\":{placed}," +
                    $"\"tried\":{tried},\"ms\":{ms}}}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] houses: {ex.Message}");
                WriteAck(HousesAck, $"{{\"token\":{token},\"error\":true}}");
            }
        }

        // thunt_request.txt: force-start a treasure hunt � headless
        // equivalent of [BotThunt force.
        private static void DoTestThunt(long token)
        {
            bool started = false;
            try { started = BotTreasureHunts.TryStartHunt(force: true); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] thunt: {ex.Message}"); }
            WriteAck(ThuntAck, $"{{\"token\":{token},\"started\":{(started ? "true" : "false")}}}");
        }

        // shop_request.txt: stock every hawker that has nothing, then force
        // one bot-to-bot sale. The ack reports what is on the shelves, so a
        // headless run can check the whole chain without a client:
        //   stocked  = hawkers holding real goods after the pass
        //   sale     = a buyer actually set off toward a seller
        // Watch the console for [shop] lines to see the haggle play out.
        private static void DoTestShop(long token)
        {
            int stocked = 0;
            bool sale = false;
            string sample = "";

            try
            {
                foreach (var m in World.Mobiles.Values)
                {
                    if (m is not PlayerBot bot || bot.Deleted || !bot.Alive)
                    {
                        continue;
                    }
                    if (bot.Behavior is not BankSitterBehavior
                        { Role: BankSitterBehavior.BankRole.Hawker })
                    {
                        continue;
                    }

                    var stock = BotShop.Stock(bot);
                    if (stock == null)
                    {
                        continue;
                    }

                    stocked++;
                    if (sample.Length == 0)
                    {
                        sample = $"{bot.Name}: {stock.Noun} @ {stock.Asking}";
                    }

                    if (!sale)
                    {
                        sale = BotShopDeal.TryStart(bot, b =>
                            b is { Deleted: false, Alive: true, LoggingOut: false } &&
                            b.Combatant == null);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] shop: {ex.Message}");
            }

            Console.WriteLine(
                $"[EditorReload] shop: {stocked} hawker(s) stocked, " +
                $"sale started: {sale}{(sample.Length > 0 ? $" (e.g. {sample})" : "")}");

            WriteAck(ShopAck,
                $"{{\"token\":{token},\"stocked\":{stocked}," +
                $"\"sale\":{(sale ? "true" : "false")}," +
                $"\"holding\":{BotShop.StockedCount}}}");
        }

        // sos_request.txt: force a fisherman to reel in an SOS bottle �
        // headless equivalent of [BotSos force.
        private static void DoTestSos(long token)
        {
            bool started = false;
            try { started = BotSeaEvents.TryFishUpBottle(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] sos: {ex.Message}"); }
            WriteAck(SosAck, $"{{\"token\":{token},\"started\":{(started ? "true" : "false")}}}");
        }

        // tame_request.txt: force a tamer to start working a quarry �
        // headless equivalent of [BotTame force.
        private static void DoTestTame(long token)
        {
            bool started = false;
            try { started = BotTaming.TryStartTaming(force: true); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] tame: {ex.Message}"); }
            WriteAck(TameAck, $"{{\"token\":{token},\"started\":{(started ? "true" : "false")}}}");
        }

        // pks_request.txt: place the default road-PK spawner set (born-red
        // hunters) — the headless [GeneratePKs. Clears any existing PK
        // spawners first so it never stacks.
        private static void DoGenPKs(long token)
        {
            int placed = 0, pks = 0;
            try
            {
                GeneratePKsCommand.ClearPKSpawners();
                (placed, pks) = GeneratePKsCommand.PlaceDefault();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] genpks: {ex.Message}");
            }

            Console.WriteLine(
                $"[EditorReload] PK spawners placed: {placed} for ~{pks} red hunters (token {token}).");
            WriteAck(PKsAck,
                $"{{\"token\":{token},\"spawners\":{placed},\"pks\":{pks}}}");
        }

        // livemap_request.txt: "token seconds" — seconds >= 1 starts the
        // [LiveMap snapshot timer at that cadence, seconds <= 0 stops it.
        // Lets the map editor's Live checkbox drive snapshots directly, no
        // client needed.
        private static long? ReadLiveMapRequest(out double seconds)
        {
            seconds = 0;
            try
            {
                if (!File.Exists(LiveMapReq)) return null;
                var parts = File.ReadAllText(LiveMapReq).Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1 || !long.TryParse(parts[0], out var t)) return null;
                if (parts.Length > 1)
                {
                    double.TryParse(parts[1], out seconds);
                }
                return t;
            }
            catch
            {
                return null; // mid-write; retry next tick
            }
        }

        private static void DoLiveMap(long token, double seconds)
        {
            bool on = seconds >= 1;
            int n = 0;
            try
            {
                if (on)
                {
                    LiveMapSnapshot.StartFromEditor(seconds);
                    n = LiveMapSnapshot.WriteSnapshot();
                }
                else
                {
                    LiveMapSnapshot.StopFromEditor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] livemap: {ex.Message}");
            }

            Console.WriteLine(on
                ? $"[EditorReload] LiveMap ON every {seconds:0}s ({n} entities, token {token})."
                : $"[EditorReload] LiveMap OFF (token {token}).");
            WriteAck(LiveMapAck,
                $"{{\"token\":{token},\"on\":{(on ? "true" : "false")}," +
                $"\"seconds\":{seconds:0.#},\"entities\":{n}}}");
        }

        // death_request.txt: kill a random eligible surface bot so headless
        // soaks can exercise the full death story (ghost → healer walk →
        // res → corpse run) on demand. Watch the [death] console lines.
        private static void DoTestDeath(long token)
        {
            var candidates = new List<PlayerBot>();
            foreach (var m in World.Mobiles.Values)
            {
                if (m is PlayerBot bot && !bot.Deleted && bot.Alive &&
                    !bot.LifecycleExempt && !bot.LoggingOut &&
                    !BotPartyManager.IsInParty(bot) &&
                    !DungeonRegistry.IsInDungeon(bot))
                {
                    candidates.Add(bot);
                }
            }

            if (candidates.Count == 0)
            {
                WriteAck(DeathAck, $"{{\"token\":{token},\"killed\":false}}");
                return;
            }

            var victim = candidates[Utility.Random(candidates.Count)];
            Console.WriteLine($"[EditorReload] test death: killing {victim.Name} (token {token}).");
            victim.Kill();
            WriteAck(DeathAck,
                $"{{\"token\":{token},\"killed\":true," +
                $"\"name\":\"{victim.Name.Replace("\"", "\\\"")}\"," +
                $"\"x\":{victim.X},\"y\":{victim.Y}}}");
        }

        // faction_request.txt: force an Order-vs-Chaos fight (teleporting
        // one fighter to the other if none are colocated) — headless
        // equivalent of [BotFactions fight.
        private static void DoTestFactionFight(long token)
        {
            bool started = false;
            try
            {
                started = BotFactionWar.TryStartFight(teleport: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] faction fight: {ex.Message}");
            }
            WriteAck(FactionAck, $"{{\"token\":{token},\"started\":{(started ? "true" : "false")}}}");
        }

        // gossip_request.txt: compose a batch of gossip lines headlessly.
        // Gossip normally only speaks with a real player in earshot, so a
        // soak can't observe it — this token runs ComposeGossip for a mix
        // of speakers (recent event ACTORS to exercise the first-person
        // "_self" templates, plus a stranger for the third-person paths)
        // and writes whatever came out to gossip_ack.json.
        private static void DoTestGossip(long token)
        {
            var lines = new List<string>();
            try
            {
                // Speakers carry a LOCATION now — gossip is distance-gated
                // (news travels at walking-rumor speed). Event actors speak
                // from where their event happened (they were there), the
                // Britain passerby hears only what's reached Britain, and
                // the Minoc miner demonstrates the gate: fresh far-off news
                // never comes out of them.
                var speakers = new List<(string name, Point3D loc)>
                {
                    ("A Passerby", new Point3D(1424, 1683, 0)),      // Britain bank
                    ("A Miner In Minoc", new Point3D(2500, 561, 0)), // far north-east
                };
                foreach (var ev in BotEventJournal.Recent(30))
                {
                    if (!string.IsNullOrEmpty(ev.Actor) &&
                        !speakers.Exists(s => s.name == ev.Actor))
                    {
                        speakers.Add((ev.Actor, new Point3D(ev.X, ev.Y, 0)));
                    }
                    if (speakers.Count >= 9)
                    {
                        break;
                    }
                }

                for (int round = 0; round < 3; round++)
                {
                    foreach (var (speaker, loc) in speakers)
                    {
                        var line = BotEventJournal.ComposeGossip(speaker, loc);
                        if (!string.IsNullOrEmpty(line))
                        {
                            lines.Add($"{speaker}: {line}");
                        }
                        if (lines.Count >= 12)
                        {
                            break;
                        }
                    }
                    if (lines.Count >= 12)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] gossip test: {ex.Message}");
            }

            WriteAck(GossipAck, System.Text.Json.JsonSerializer.Serialize(
                new { token, count = lines.Count, lines }));
            Console.WriteLine($"[EditorReload] gossip test: composed {lines.Count} line(s).");
        }

        // party_request.txt: force-form a hunting party (= [BotParties form
        // without a client) so headless soaks can exercise the full party
        // pipeline on demand. Ack reports who formed and where they're headed.
        private static void DoFormParty(long token)
        {
            BotParty party = null;
            try { party = BotPartyManager.TryFormParty(null); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] party: {ex.Message}"); }

            if (party == null)
            {
                Console.WriteLine($"[EditorReload] party request: no eligible leader/recruits (token {token}).");
                WriteAck(PartyAck, $"{{\"token\":{token},\"formed\":false}}");
                return;
            }

            var members = new List<string>();
            foreach (var m in party.Members)
            {
                members.Add("\"" + m.Name.Replace("\"", "\\\"") + "\"");
            }
            WriteAck(PartyAck,
                $"{{\"token\":{token},\"formed\":true," +
                $"\"leader\":\"{party.Leader.Name.Replace("\"", "\\\"")}\"," +
                $"\"dungeon\":\"{party.Target.Dungeon}\"," +
                $"\"members\":[{string.Join(",", members)}]}}");
        }

        // walkmap_request.txt: "token x0 y0 x1 y1" — dump per-tile
        // walkability of the rect to Data/Live/walkmap.pgm (P5, 255 =
        // standable). Lets offline tools plan waypoints against the
        // server's REAL movement rules instead of guessing from map art.
        private static long? ReadWalkRequest(out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = x1 = y1 = 0;
            try
            {
                if (!File.Exists(WalkReq)) return null;
                var parts = File.ReadAllText(WalkReq).Split(
                    new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !long.TryParse(parts[0], out var t)) return null;
                x0 = int.Parse(parts[1]); y0 = int.Parse(parts[2]);
                x1 = int.Parse(parts[3]); y1 = int.Parse(parts[4]);
                return t;
            }
            catch
            {
                return null; // mid-write; retry next tick
            }
        }

        private static void DoWalkmap(long token, int x0, int y0, int x1, int y1)
        {
            const int MaxSide = 512; // bound the game-thread stall
            if (x1 < x0) (x0, x1) = (x1, x0);
            if (y1 < y0) (y0, y1) = (y1, y0);
            x1 = Math.Min(x1, x0 + MaxSide - 1);
            y1 = Math.Min(y1, y0 + MaxSide - 1);
            int w = x1 - x0 + 1, h = y1 - y0 + 1;

            var map = Map.Felucca;
            var bytes = new byte[w * h];
            var zbytes = new byte[w * h]; // resolved standing Z + 128 (0 = unwalkable)
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Walkable.TryFindSeedZ(map, x0 + x, y0 + y, 0, out int z))
                    {
                        bytes[y * w + x] = 255;
                        zbytes[y * w + x] = (byte)Math.Clamp(z + 128, 1, 255);
                    }
                }
            }

            try
            {
                using var fs = new FileStream(WalkAck, FileMode.Create, FileAccess.Write);
                var header = System.Text.Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
                fs.Write(header, 0, header.Length);
                fs.Write(bytes, 0, bytes.Length);

                // Z sidecar: offline trail A* needs per-tile heights to apply
                // the climb/drop step rules — a flat mask over-connects
                // adjacent tiles split by a cliff seam.
                using var fz = new FileStream(
                    Live("walkmap_z.pgm"), FileMode.Create, FileAccess.Write);
                fz.Write(header, 0, header.Length);
                fz.Write(zbytes, 0, zbytes.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] walkmap write failed: {ex.Message}");
            }

            Console.WriteLine(
                $"[EditorReload] walkmap ({x0},{y0})-({x1},{y1}) {w}x{h} written (token {token}).");
        }

        // Full nav-data verification without a client: the [AuditNav data
        // checks plus the [auditedges walkability flood, results to
        // Data/Live/audit_report.json. Lets data authored outside the game
        // (map editor, scripts, Claude) be verified headlessly.
        private static void DoAudit(long token)
        {
            var lines = new List<string>();
            try { lines.AddRange(AuditNavCommand.Run()); }
            catch (Exception ex) { lines.Add($"AuditNav failed: {ex.Message}"); }
            try
            {
                foreach (var l in AuditEdgesCommand.Scan())
                {
                    lines.Add($"EDGEWALK: {l}");
                }
            }
            catch (Exception ex) { lines.Add($"auditedges scan failed: {ex.Message}"); }

            Console.WriteLine($"[EditorReload] audit ran: {lines.Count} finding(s) (token {token}).");

            for (int i = 0; i < lines.Count; i++)
            {
                lines[i] = "\"" + lines[i].Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            }
            WriteAck(AuditAck,
                $"{{\"token\":{token},\"findings\":[{string.Join(",", lines)}]}}");
        }

        private static void DoReload(long token)
        {
            int wps = 0, dests = 0, zones = 0;
            try { wps = WaypointRegistry.Load(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] waypoints: {ex.Message}"); }
            try { dests = DestinationCatalog.Load(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] destinations: {ex.Message}"); }
            try { zones = ZoneRegistry.Reload(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] zones: {ex.Message}"); }

            Console.WriteLine(
                $"[EditorReload] reloaded {wps} waypoint(s), {dests} destination(s), " +
                $"{zones} zone(s) (token {token}).");

            WriteAck(ReloadAck,
                $"{{\"token\":{token},\"waypoints\":{wps}," +
                $"\"destinations\":{dests},\"zones\":{zones}}}");
        }

        private static void DoRegen(long token)
        {
            int spawners = 0;
            try { spawners = GenerateBotsCommand.RegenerateForPopulation(); }
            catch (Exception ex) { Console.WriteLine($"[EditorReload] regen: {ex.Message}"); }

            Console.WriteLine(
                $"[EditorReload] regenerated bot population: {spawners} spawner(s) (token {token}).");

            WriteAck(GenAck, $"{{\"token\":{token},\"spawners\":{spawners}}}");
        }

        // -------------------------------------------------------------------
        // guildbots_request.txt: disposable roster E2E. The scenario uses the
        // typed PlayerGuildBotGuild adapter boundary, so it remains usable
        // across ModernUO guild API revisions without a speculative engine
        // dependency. Every run cleans its fixed test identity before and
        // after execution; no native guild catalog or permanent guild is
        // changed.
        // -------------------------------------------------------------------
        private sealed class GuildBotsScenario
        {
            private const string TestGuildId = "__guildbots_headless__";

            private readonly long _token;
            private readonly string _testGuildTag;
            private readonly List<GuildBotsAssertion> _assertions = new();
            private readonly List<GuildBotsDelivery> _deliveries = new();
            private readonly List<string> _errors = new();
            private readonly int _syntheticGuildCount = BotGuilds.All.Length;
            private readonly string _syntheticGuildFingerprint;
            private string _originalRoster;
            private PlayerGuildBotRosterSnapshot _initialSnapshot;
            private PlayerGuildBotGuild _guild;
            private Server.Mobiles.PlayerMobile _member;
            private PlayerBot _partyBot;
            private int _baselineReservations;
            private bool _firstChatAccepted;
            private DateTime _firstChatStarted;
            private DateTime _secondChatStarted;
            private bool _finished;

            public GuildBotsScenario(long token)
            {
                _token = token;
                var suffix = ((token % 1000) + 1000) % 1000;
                _testGuildTag = $"HBT{suffix:000}";
                _syntheticGuildFingerprint = SyntheticGuildFingerprint();
            }

            public void Start()
            {
                try
                {
                    _originalRoster = File.ReadAllText(PlayerGuildBotRoster.ConfigurationPath);
                    _initialSnapshot = PlayerGuildBotRoster.ActiveSnapshot;
                    Check(_initialSnapshot != null, "configuration.active",
                        "a valid roster snapshot must be active before the scenario starts");
                    if (_initialSnapshot == null)
                    {
                        Finish();
                        return;
                    }

                    // Remove leftovers from a crashed prior run before checking
                    // the real-guild gate. The ID is deliberately namespaced.
                    PlayerGuildBotRoster.OnGuildDisbanded(TestGuildId);
                    _baselineReservations = NamePool.ReservedCount;
                    Check(CountBots() == 0, "gating.noBots",
                        "no disposable roster bots exist before guild creation");
                    Check(NoNamesReserved(_initialSnapshot), "gating.noReservations",
                        "no disposable roster names are reserved before guild creation");
                    Check(!PlayerGuildBotRoster.HandleGuildMessage(
                            TestGuildId, null, "before guild"),
                        "gating.noChat", "inactive guilds do not produce roster replies");

                    _member = new Server.Mobiles.PlayerMobile
                    {
                        Name = "GuildBots Tester",
                        Body = 0x190,
                        Hue = 0x83EA,
                        Player = true,
                    };
                    var loc = TestLocation();
                    _member.MoveToWorld(loc, Map.Felucca);
                    _guild = new PlayerGuildBotGuild(
                        TestGuildId, _testGuildTag, members: new Mobile[] { _member });

                    PlayerGuildBotRoster.OnGuildCreated(_guild);
                    AssertRoster(_initialSnapshot, "reconcile.create");
                    int firstCount = CountBots();
                    PlayerGuildBotRoster.OnGuildCreated(_guild);
                    Check(CountBots() == firstCount, "reconcile.idempotent",
                        "repeating create does not duplicate bots");
                    Check(CountBots() == _initialSnapshot.Personas.Count,
                        "reconcile.onePerPersona", "one live bot exists per configured persona");

                    CheckAmbientAndSyntheticBoundaries();
                    StartDeletionCheck();
                }
                catch (Exception ex)
                {
                    Fail("start", ex);
                    Finish();
                }
            }

            private void StartDeletionCheck()
            {
                try
                {
                    var persona = _initialSnapshot.Personas[0];
                    var bots = PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, persona.Id);
                    Check(bots.Count == 1, "recovery.beforeDelete",
                        "a live persona is available for targeted deletion recovery");
                    if (bots.Count == 1)
                    {
                        bots[0].Delete();
                    }
                    Timer.DelayCall(TimeSpan.FromSeconds(2), AfterDeletion);
                }
                catch (Exception ex)
                {
                    Fail("recovery.start", ex);
                    StartChatCheck();
                }
            }

            private void AfterDeletion()
            {
                if (_finished) return;
                try
                {
                    var persona = _initialSnapshot.Personas[0];
                    var bots = PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, persona.Id);
                    PlayerGuildBotRoster.TryGetBinding(
                        TestGuildId, persona.Id, out var binding);
                    Check(bots.Count == 1 && binding != null &&
                            NamePool.IsReserved(binding.ExactName) &&
                            string.Equals(bots[0].Name, binding.ExactName,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(bots[0].HomeCity, binding.HomeCity,
                                StringComparison.OrdinalIgnoreCase),
                        "recovery.sameIdentity",
                        "deleted persona is recreated with the same name and home city");
                    StartChatCheck();
                }
                catch (Exception ex)
                {
                    Fail("recovery.finish", ex);
                    StartChatCheck();
                }
            }

            private void StartChatCheck()
            {
                try
                {
                    PlayerGuildBotRoster.GuildChatReplyDelivered += OnChatDelivery;
                    var botSpeaker = PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId).FirstOrDefault();
                    Check(botSpeaker != null && !PlayerGuildBotRoster.HandleGuildMessage(
                            TestGuildId, botSpeaker, "bot speech"),
                        "chat.botSpeakerIgnored", "bot-generated speech cannot trigger a roster reply");
                    _firstChatStarted = DateTime.UtcNow;
                    _firstChatAccepted = PlayerGuildBotRoster.HandleGuildMessage(
                        TestGuildId, _member, "hello roster");
                    bool duplicate = PlayerGuildBotRoster.HandleGuildMessage(
                        TestGuildId, _member, "hello again");
                    Check(_firstChatAccepted, "chat.firstAccepted",
                        "a configured online roster can schedule a guild reply");
                    Check(!duplicate, "chat.atMostOne",
                        "a guild has at most one pending reply at a time");
                    Timer.DelayCall(TimeSpan.FromSeconds(7), AfterFirstChat);
                }
                catch (Exception ex)
                {
                    Fail("chat.start", ex);
                    try
                    {
                        RunConfigChecks();
                        StartPartyCheck();
                    }
                    catch (Exception followup)
                    {
                        Fail("chat.start.followup", followup);
                        Finish();
                    }
                }
            }

            private void AfterFirstChat()
            {
                if (_finished) return;
                try
                {
                    Check(_deliveries.Count <= 1, "chat.firstDelivery",
                        $"first message produced {_deliveries.Count} delivery callback(s)");
                    _secondChatStarted = DateTime.UtcNow;
                    bool second = PlayerGuildBotRoster.HandleGuildMessage(
                        TestGuildId, _member, "second hello");
                    Check(!_firstChatAccepted || second, "chat.secondAccepted",
                        "a second message can schedule after the first delay completes");
                    Timer.DelayCall(TimeSpan.FromSeconds(7), AfterSecondChat);
                }
                catch (Exception ex)
                {
                    Fail("chat.first", ex);
                    Finish();
                }
            }

            private void AfterSecondChat()
            {
                if (_finished) return;
                try
                {
                    Check(_deliveries.Count <= 2, "chat.deliveryBound",
                        $"two messages produced {_deliveries.Count} delivery callback(s)");
                    if (_firstChatAccepted)
                    {
                        bool delivered = false;
                        foreach (var delivery in _deliveries)
                        {
                            if (delivery.RecipientCount > 0)
                            {
                                delivered = true;
                                break;
                            }
                        }
                        Check(delivered, "chat.clientlessDelivery",
                            "a configured response reached at least one real recipient");
                        Check(_deliveries.Count >= 2, "chat.twoMessages",
                            "both scheduled messages complete after their bounded delay");
                        if (_deliveries.Count >= 2 && _initialSnapshot.Personas.Count > 1)
                        {
                            Check(!string.Equals(_deliveries[0].PersonaId,
                                    _deliveries[1].PersonaId,
                                    StringComparison.OrdinalIgnoreCase),
                                "chat.roundRobin", "successive replies use different personas");
                        }
                    }
                    RunConfigChecks();
                    StartPartyCheck();
                }
                catch (Exception ex)
                {
                    Fail("chat.second", ex);
                    Finish();
                }
            }

            private void RunConfigChecks()
            {
                var original = new List<PlayerGuildBotPersona>();
                foreach (var persona in _initialSnapshot.Personas)
                {
                    original.Add(persona);
                }
                if (original.Count == 0)
                {
                    Fail("config.personas", "the active roster has no personas");
                    return;
                }

                var removed = original[original.Count - 1];
                PlayerGuildBotRoster.TryGetBinding(
                    TestGuildId, removed.Id, out var removedBinding);
                var withoutRemoved = new List<PlayerGuildBotPersona>(original);
                withoutRemoved.RemoveAt(withoutRemoved.Count - 1);
                WriteRoster(withoutRemoved);
                var removeLoad = PlayerGuildBotRoster.Reload(scheduleReconcile: false);
                Check(removeLoad.Success, "config.retireReload",
                    "valid persona removal reloads successfully");
                PlayerGuildBotRoster.OnGuildCreated(_guild);
                Check(PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, removed.Id).Count == 0,
                    "config.retireBot", "removed persona is retired from the live guild");
                Check(removedBinding != null && !NamePool.IsReserved(removedBinding.ExactName),
                    "config.retireReservation",
                    "retired persona releases its name while retaining historical state");
                Check(removedBinding != null && PlayerGuildBotRoster.TryGetBinding(
                        TestGuildId, removed.Id, out _),
                    "config.retireState", "retired persona keeps its binding for re-add");

                var extra = new PlayerGuildBotPersona(
                    "headless-extra", "HeadlessExtra", false, BotClass.Warrior,
                    BotSkillTier.Expert, original[0].Behavior, new[] { "guild_chat" });
                var withExtra = new List<PlayerGuildBotPersona>(original) { extra };
                WriteRoster(withExtra);
                var addLoad = PlayerGuildBotRoster.Reload(scheduleReconcile: false);
                Check(addLoad.Success, "config.addReload",
                    "valid persona addition reloads successfully");
                PlayerGuildBotRoster.OnGuildCreated(_guild);
                Check(PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, extra.Id).Count == 1,
                    "config.addBot", "new persona is reconciled for the active guild");
                Check(PlayerGuildBotRoster.GetRosterView(TestGuildId)?.Personas.Count ==
                        withExtra.Count,
                    "config.addView", "roster view includes the added persona");

                var beforeInvalid = PlayerGuildBotRoster.ActiveSnapshot;
                WriteRosterText("{ invalid roster");
                var invalid = PlayerGuildBotRoster.Reload(scheduleReconcile: false);
                Check(!invalid.Success && ReferenceEquals(beforeInvalid,
                        PlayerGuildBotRoster.ActiveSnapshot),
                    "config.invalidRetention",
                    "invalid reload retains the last valid snapshot");
                var invalidErrorText = invalid.Errors.Count == 0
                    ? "no validation error was returned"
                    : string.Join(" | ", invalid.Errors);
                Check(invalid.Errors.Any(error => error.Contains("Invalid JSON",
                        StringComparison.OrdinalIgnoreCase)),
                    "config.invalidError", $"invalid reload error: {invalidErrorText}");

                var conflict = new PlayerGuildBotPersona(
                    "headless-conflict", original[0].BaseName, original[0].Female,
                    original[0].Class, original[0].SkillTier, original[0].Behavior,
                    original[0].ChatCategories);
                var withConflict = new List<PlayerGuildBotPersona>(withExtra) { conflict };
                WriteRoster(withConflict);
                var conflictLoad = PlayerGuildBotRoster.Reload(scheduleReconcile: false);
                Check(conflictLoad.Success, "config.conflictReload",
                    "conflict candidate remains structurally valid");
                int reservationsBefore = NamePool.ReservedCount;
                PlayerGuildBotRoster.OnGuildCreated(_guild);
                Check(PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, conflict.Id).Count == 0,
                    "config.conflictSkip",
                    $"conflicting name '{withConflict[0].BaseName} of {_testGuildTag}' " +
                    "is skipped without renaming");
                Check(NamePool.ReservedCount == reservationsBefore,
                    "config.conflictReservation",
                    $"conflict owner '{conflict.Id}' does not leak a reservation");

                WriteRoster(withExtra);
                Check(PlayerGuildBotRoster.Reload(scheduleReconcile: false).Success,
                    "config.restoreAdded", "valid roster is restored after conflict check");
                PlayerGuildBotRoster.OnGuildCreated(_guild);
                Check(PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, extra.Id).Count == 1,
                    "config.restoreAddedBot", "added persona survives restoration");

                WriteRoster(original);
                Check(PlayerGuildBotRoster.Reload(scheduleReconcile: false).Success,
                    "config.restoreOriginal", "original roster reloads successfully");
                PlayerGuildBotRoster.OnGuildCreated(_guild);
                Check(CountBots() == original.Count, "config.restoreCount",
                    "restoring the original roster retires the temporary persona");
                Check(removedBinding != null &&
                        PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                            TestGuildId, removed.Id).Count == 1 &&
                        string.Equals(GetBotName(removed.Id), removedBinding.ExactName,
                            StringComparison.OrdinalIgnoreCase),
                    "config.readdIdentity", "re-added persona recovers its exact historical name");
            }

            private void StartPartyCheck()
            {
                try
                {
                    foreach (var persona in PlayerGuildBotRoster.ActiveSnapshot.Personas)
                    {
                        if (BotClassHelper.IsArtisan(persona.Class) ||
                            BotClassHelper.IsGatherer(persona.Class) ||
                            persona.Class == BotClass.Crafter)
                        {
                            continue;
                        }
                        var bots = PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                            TestGuildId, persona.Id);
                        if (bots.Count == 0) continue;
                        _partyBot = bots[0];
                        break;
                    }

                    Check(_partyBot != null, "party.eligiblePersona",
                        "at least one configured roster bot is party-eligible");
                    if (_partyBot == null)
                    {
                        BeginDisbandCheck();
                        return;
                    }

                    Server.Engines.PartySystem.Party.Invite(_member, _partyBot);
                    BotPlayerParty.CheckInvite(_partyBot);
                    Timer.DelayCall(TimeSpan.FromSeconds(4), AfterParty);
                }
                catch (Exception ex)
                {
                    Fail("party.start", ex);
                    BeginDisbandCheck();
                }
            }

            private void AfterParty()
            {
                if (_finished) return;
                try
                {
                    bool joined = BotPlayerParty.InPlayerParty(_partyBot);
                    Check(joined, "party.join", "roster bot accepts a normal player invitation");
                    if (_partyBot?.Party is Server.Engines.PartySystem.Party party)
                    {
                        party.Disband();
                    }
                    _partyBot.Party = null;
                    if (_partyBot.Behavior is PlayerGroupBehavior group)
                    {
                        group.Tick(_partyBot);
                    }
                    Check(_partyBot != null &&
                            string.Equals(_partyBot.ConfiguredBehaviorName,
                                _partyBot.Behavior?.SerializableName,
                                StringComparison.OrdinalIgnoreCase),
                        "party.restoreBehavior",
                        "leaving a player party restores configured roster behavior");
                    BeginDisbandCheck();
                }
                catch (Exception ex)
                {
                    Fail("party.finish", ex);
                    BeginDisbandCheck();
                }
            }

            private void BeginDisbandCheck()
            {
                if (_finished) return;
                try
                {
                    int before = _deliveries.Count;
                    bool pending = PlayerGuildBotRoster.HandleGuildMessage(
                        TestGuildId, _member, "disband pending");
                    PlayerGuildBotRoster.OnGuildDisbanded(TestGuildId);
                    Check(CountBots() == 0, "disband.bots", "disband removes every roster bot");
                    Check(PlayerGuildBotRoster.GetBindings(TestGuildId).Count == 0,
                        "disband.state", "disband removes disposable roster state");
                    Check(NoNamesReserved(_initialSnapshot) &&
                            NamePool.ReservedCount == _baselineReservations,
                        "disband.reservations",
                        "disband releases every disposable roster reservation");
                    Timer.DelayCall(TimeSpan.FromSeconds(6), () =>
                    {
                        if (_finished) return;
                        Check(!pending || _deliveries.Count == before,
                            "disband.chatCancel", "disband cancels pending delayed replies");
                        Finish();
                    });
                }
                catch (Exception ex)
                {
                    Fail("disband", ex);
                    Finish();
                }
            }

            private void OnChatDelivery(string guildId, string personaId,
                string senderName, int recipientCount)
            {
                if (!string.Equals(guildId, TestGuildId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                var started = _deliveries.Count == 0
                    ? _firstChatStarted
                    : _secondChatStarted;
                var elapsed = started == DateTime.MinValue
                    ? 0
                    : (DateTime.UtcNow - started).TotalSeconds;
                _deliveries.Add(new GuildBotsDelivery
                {
                    PersonaId = personaId,
                    SenderName = senderName,
                    RecipientCount = recipientCount,
                    DelaySeconds = elapsed,
                });
                Check(elapsed >= 1.5 && elapsed <= 7.0, "chat.delay",
                    $"reply callback arrived after {elapsed:0.0}s (expected 2-5s)");
                if (recipientCount > 0)
                {
                    var binding = PlayerGuildBotRoster.GetBindings(TestGuildId)
                        .FirstOrDefault(item => string.Equals(item.PersonaId, personaId,
                            StringComparison.OrdinalIgnoreCase));
                    Check(binding != null && string.Equals(binding.ExactName, senderName,
                            StringComparison.OrdinalIgnoreCase),
                        "chat.exactSender", "chat sender matches the persisted in-world name");
                }
            }

            private void CheckAmbientAndSyntheticBoundaries()
            {
                int ambientBefore = NamePool.InUseCount - NamePool.LiveReservedCount;
                bool allowBefore = BotSessionManager.AllowSpawn();
                Check(_syntheticGuildCount == BotGuilds.All.Length &&
                        string.Equals(_syntheticGuildFingerprint, SyntheticGuildFingerprint(),
                            StringComparison.Ordinal),
                    "compat.syntheticGuilds", "synthetic BotGuilds catalog remains unchanged");
                foreach (var bot in PlayerGuildBotRoster.GetRosterBotsForDiagnostics(TestGuildId))
                {
                    Check(bot.IsPlayerGuildBot && !bot.LifecycleExempt &&
                            bot.BotGuildIndex < 0 && bot.NetState == null &&
                            !IsNativeGuildMember(bot),
                        $"identity.bound.{bot.PlayerGuildPersonaId}",
                        "roster identity is separate from synthetic/native guild membership");
                }
                int ambientAfter = NamePool.InUseCount - NamePool.LiveReservedCount;
                Check(ambientBefore == ambientAfter && allowBefore == BotSessionManager.AllowSpawn(),
                    "population.ambientBoundary",
                    "roster names do not consume ambient population accounting");
            }

            private static string SyntheticGuildFingerprint()
            {
                var parts = new List<string>(BotGuilds.All.Length);
                foreach (var guild in BotGuilds.All)
                {
                    parts.Add($"{guild.Name}:{guild.Tag}:{guild.Weight}:{guild.Faction}");
                }
                return string.Join("|", parts);
            }

            private bool IsNativeGuildMember(PlayerBot bot)
            {
                if (_guild?.Members == null)
                {
                    return false;
                }
                foreach (var member in _guild.Members)
                {
                    if (ReferenceEquals(member, bot))
                    {
                        return true;
                    }
                }
                return false;
            }

            private void AssertRoster(PlayerGuildBotRosterSnapshot snapshot, string prefix)
            {
                int total = 0;
                foreach (var persona in snapshot.Personas)
                {
                    var bots = PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                        TestGuildId, persona.Id);
                    total += bots.Count;
                    PlayerGuildBotRoster.TryGetBinding(
                        TestGuildId, persona.Id, out var binding);
                    Check(bots.Count == 1, $"{prefix}.count.{persona.Id}",
                        $"expected one live bot, found {bots.Count}");
                    if (bots.Count == 0 || binding == null) continue;
                    var bot = bots[0];
                    Check(string.Equals(bot.Name, binding.ExactName,
                            StringComparison.OrdinalIgnoreCase) &&
                            !bot.Name.Contains("[", StringComparison.Ordinal) &&
                            bot.Female == persona.Female && bot.Class == persona.Class &&
                            bot.SkillTier == persona.SkillTier &&
                            string.Equals(bot.ConfiguredBehaviorName, persona.Behavior,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(bot.Behavior?.SerializableName, persona.Behavior,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(bot.HomeCity, binding.HomeCity,
                                StringComparison.OrdinalIgnoreCase),
                        $"{prefix}.identity.{persona.Id}",
                        "name, gender, class, tier, behavior, and home city match state/config");
                }
                Check(total == snapshot.Personas.Count, $"{prefix}.total",
                    $"expected {snapshot.Personas.Count} roster bots, found {total}");
            }

            private bool NoNamesReserved(PlayerGuildBotRosterSnapshot snapshot)
            {
                foreach (var persona in snapshot.Personas)
                {
                    var name = snapshot.FormatName(persona.BaseName, _testGuildTag);
                    if (NamePool.IsReserved(name)) return false;
                }
                return true;
            }

            private int CountBots() => PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                TestGuildId).Count;

            private string GetBotName(string personaId)
            {
                var bots = PlayerGuildBotRoster.GetRosterBotsForDiagnostics(
                    TestGuildId, personaId);
                return bots.Count == 0 ? null : bots[0].Name;
            }

            private Point3D TestLocation()
            {
                const int x = 1424;
                const int y = 1683;
                int z = Walkable.TryFindSeedZ(Map.Felucca, x, y, 0, out var seedZ)
                    ? seedZ
                    : Map.Felucca.GetAverageZ(x, y);
                return new Point3D(x, y, z);
            }

            private void WriteRoster(IReadOnlyList<PlayerGuildBotPersona> personas)
            {
                var records = new List<object>();
                foreach (var persona in personas)
                {
                    records.Add(new
                    {
                        id = persona.Id,
                        baseName = persona.BaseName,
                        female = persona.Female,
                        @class = persona.Class.ToString(),
                        skillTier = persona.SkillTier.ToString(),
                        behavior = persona.Behavior,
                        chatCategories = persona.ChatCategories,
                    });
                }
                var document = new
                {
                    version = PlayerGuildBotRoster.CurrentConfigVersion,
                    nameFormat = PlayerGuildBotRoster.ActiveSnapshot.NameFormat,
                    spawnRadius = PlayerGuildBotRoster.ActiveSnapshot.SpawnRadius,
                    personas = records,
                };
                WriteRosterText(System.Text.Json.JsonSerializer.Serialize(document,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            private void WriteRosterText(string text) =>
                File.WriteAllText(PlayerGuildBotRoster.ConfigurationPath, text);

            private void Check(bool passed, string name, string detail)
            {
                _assertions.Add(new GuildBotsAssertion
                {
                    Name = name,
                    Passed = passed,
                    Detail = detail,
                });
                if (!passed)
                {
                    _errors.Add($"{name}: {detail}");
                }
            }

            private void Fail(string name, Exception ex) =>
                Fail(name, ex?.Message ?? "unknown error");

            private void Fail(string name, string detail)
            {
                Check(false, name, detail);
            }

            private void Finish()
            {
                if (_finished) return;
                _finished = true;
                try
                {
                    PlayerGuildBotRoster.GuildChatReplyDelivered -= OnChatDelivery;
                    if (_member?.Party is Server.Engines.PartySystem.Party party)
                    {
                        party.Disband();
                    }
                    PlayerGuildBotRoster.OnGuildDisbanded(TestGuildId);
                    if (_member != null && !_member.Deleted)
                    {
                        _member.Delete();
                    }
                    if (_originalRoster != null)
                    {
                        WriteRosterText(_originalRoster);
                        var restored = PlayerGuildBotRoster.Reload(scheduleReconcile: false);
                        if (!restored.Success)
                        {
                            Fail("cleanup.restoreRoster", "failed to restore the original roster file");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Fail("cleanup", ex);
                }

                var ack = new
                {
                    token = _token,
                    passed = _errors.Count == 0,
                    assertions = _assertions,
                    errors = _errors,
                    deliveryCount = _deliveries.Count,
                    botsRemaining = CountBots(),
                    reservationsRemaining = NamePool.ReservedCount,
                    pendingGuildReplies = PlayerGuildBotRoster.PendingGuildChatReplyCount,
                    syntheticGuildCount = BotGuilds.All.Length,
                    guildAdapter = "PlayerGuildBotGuild snapshot",
                };
                WriteAck(GuildBotsAck,
                    System.Text.Json.JsonSerializer.Serialize(ack,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                _guildBotsScenario = null;
            }

            private sealed class GuildBotsAssertion
            {
                public string Name { get; set; }
                public bool Passed { get; set; }
                public string Detail { get; set; }
            }

            private sealed class GuildBotsDelivery
            {
                public string PersonaId { get; set; }
                public string SenderName { get; set; }
                public int RecipientCount { get; set; }
                public double DelaySeconds { get; set; }
            }
        }

        private static void WriteAck(string path, string json)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[EditorReload] ack write failed: {ex.Message}");
            }
        }
    }
}
