// =========================================================================
// GuildBotsRosterGump.cs — player-guild roster inspection.
//
// Players see only their own guild. GameMasters can select any active player
// guild. The manager supplies every row so names/status cannot drift from the
// live PlayerBot identity or guild-chat sender name.
// =========================================================================

using System;
using Server;
using Server.Gumps;
using Server.Network;

namespace Server.CustomBots
{
    public sealed class GuildBotsRosterGump : Gump
    {
        private const int Width = 620;
        private const int Background = 9270;
        private const int ButtonNormal = 4005;
        private const int ButtonPressed = 4007;
        private const int ExitUp = 0xFB1;
        private const int ExitDown = 0xFB3;
        private const int LabelHue = 1153;
        private const int SelectButtonBase = 100;
        private const int CloseButton = 3;

        private readonly string _guildId;

        public GuildBotsRosterGump(Mobile from, string guildId = null)
            : base(50, 40)
        {
            _guildId = guildId?.Trim();
            Build(from);
        }

        private void Build(Mobile from)
        {
            bool staff = from != null && from.AccessLevel >= AccessLevel.GameMaster;
            bool selector = staff && string.IsNullOrWhiteSpace(_guildId);
            int rowCount = selector
                ? PlayerGuildBotRoster.GetActiveGuilds().Count
                : PlayerGuildBotRoster.GetRosterView(_guildId)?.Personas.Count ?? 0;
            int height = Math.Max(230, 180 + rowCount * 30);

            AddPage(0);
            AddBackground(0, 0, Width, height, Background);
            AddHtml(14, 14, Width - 70, 24,
                "<BASEFONT COLOR=#F4F4F4 SIZE=4><B>GuildBots Roster</B></BASEFONT>");
            AddButton(Width - 38, 14, ExitUp, ExitDown, CloseButton);

            int y = 48;
            if (selector)
            {
                BuildStaffSelector(y);
                return;
            }

            BuildRoster(from, y, staff);
        }

        private void BuildStaffSelector(int y)
        {
            AddLabel(14, y, LabelHue, "Select an active player guild to inspect:");
            y += 28;

            var guilds = PlayerGuildBotRoster.GetActiveGuilds();
            if (guilds.Count == 0)
            {
                AddLabel(24, y, LabelHue, "No active player guilds. Roster bots are disabled until a guild exists.");
                return;
            }

            for (int i = 0; i < guilds.Count; i++)
            {
                var guild = guilds[i];
                AddButton(24, y, ButtonNormal, ButtonPressed, SelectButtonBase + i);
                AddLabel(56, y + 2, LabelHue, $"[{guild.Tag}] {guild.Id}");
                y += 30;
            }
        }

        private void BuildRoster(Mobile from, int y, bool staff)
        {
            PlayerGuildBotGuild guild = null;
            if (staff)
            {
                PlayerGuildBotRoster.TryGetActiveGuild(_guildId, out guild);
            }
            else if (from != null)
            {
                PlayerGuildBotRoster.TryGetGuildForMember(from, out guild);
                if (guild != null && !string.IsNullOrWhiteSpace(_guildId) &&
                    !string.Equals(guild.Id, _guildId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    guild = null;
                }
            }

            if (guild == null)
            {
                AddLabel(24, y, LabelHue,
                    staff
                        ? "That player guild is no longer active."
                        : "You are not a member of an active player guild.");
                AddFooter(y + 34, staff);
                return;
            }

            var view = PlayerGuildBotRoster.GetRosterView(guild.Id);
            if (view == null)
            {
                AddLabel(24, y, LabelHue,
                    "No valid roster configuration is active; try again after an administrator reload.");
                AddFooter(y + 34, staff);
                return;
            }

            AddLabel(14, y, LabelHue, $"Guild [{guild.Tag}]  ({guild.Id})");
            y += 28;
            AddLabel(24, y, LabelHue, "Persona");
            AddLabel(140, y, LabelHue, "Name");
            AddLabel(330, y, LabelHue, "Class / tier");
            AddLabel(450, y, LabelHue, "Behavior");
            AddLabel(550, y, LabelHue, "State");
            y += 24;

            if (view.Personas.Count == 0)
            {
                AddLabel(24, y, LabelHue, "No configured personas.");
                AddFooter(y + 34, staff);
                return;
            }

            foreach (var row in view.Personas)
            {
                AddLabel(24, y, LabelHue, row.PersonaId);
                AddLabel(140, y, LabelHue, row.ExactName ?? "(unavailable)");
                AddLabel(330, y, LabelHue,
                    $"{BotClassHelper.DisplayName(row.Class)} / {row.SkillTier}");
                AddLabel(450, y, LabelHue, row.Behavior ?? "-");
                AddLabel(550, y, LabelHue, row.IsOnline ? "Online" : "Offline");
                y += 30;
            }

            AddHtml(24, y + 2, Width - 48, 22,
                "<BASEFONT COLOR=#C8C0A8><I>Online status is based on the live roster bot; " +
                "offline personas keep their configured identity.</I></BASEFONT>");
            AddFooter(y + 30, staff);
        }

        private void AddFooter(int y, bool staff)
        {
            AddButton(24, y, ButtonNormal, ButtonPressed, 1);
            AddLabel(56, y + 2, LabelHue, "Refresh");
            if (staff)
            {
                AddButton(190, y, ButtonNormal, ButtonPressed, 2);
                AddLabel(222, y + 2, LabelHue, "Guild list");
            }
        }

        public override void OnResponse(NetState sender, in RelayInfo info)
        {
            var from = sender?.Mobile;
            if (from == null)
            {
                return;
            }

            bool staff = from.AccessLevel >= AccessLevel.GameMaster;
            if (info.ButtonID == CloseButton || info.ButtonID == 0)
            {
                return;
            }
            if (info.ButtonID == 1)
            {
                if (staff && string.IsNullOrWhiteSpace(_guildId))
                {
                    from.SendGump(new GuildBotsRosterGump(from));
                }
                else
                {
                    from.SendGump(new GuildBotsRosterGump(from, _guildId));
                }
                return;
            }
            if (info.ButtonID == 2 && staff)
            {
                from.SendGump(new GuildBotsRosterGump(from));
                return;
            }
            if (info.ButtonID < SelectButtonBase || !staff ||
                !string.IsNullOrWhiteSpace(_guildId))
            {
                return;
            }

            int index = info.ButtonID - SelectButtonBase;
            var guilds = PlayerGuildBotRoster.GetActiveGuilds();
            if (index < 0 || index >= guilds.Count)
            {
                from.SendGump(new GuildBotsRosterGump(from));
                return;
            }

            from.SendGump(new GuildBotsRosterGump(from, guilds[index].Id));
        }
    }
}
