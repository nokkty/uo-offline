using System;
using Server.Commands;
using Server.Guilds;
using Server.Gumps;
using Server.Mobiles;
using Server.Network;

namespace Server.CustomBots;

public sealed class ManageGuildGump : DynamicGump
{
    private readonly string _guildName;
    private readonly string _guildAbbreviation;

    public override bool Singleton => true;

    public ManageGuildGump(
        PlayerMobile player,
        string guildName = "",
        string guildAbbreviation = "") : base(10, 10)
    {
        _guildName = guildName;
        _guildAbbreviation = guildAbbreviation;
        player.CloseGump<ManageGuildGump>();
    }

    public static void Configure()
    {
        CommandSystem.Register("ManageGuild", AccessLevel.Player, OnCommand);
    }

    [Usage("ManageGuild")]
    [Description("Create a guild or open your native guild management menu.")]
    private static void OnCommand(CommandEventArgs e)
    {
        if (e.Mobile is not PlayerMobile player || player.Deleted)
        {
            return;
        }

        if (player.Guild is Guild guild && !guild.Disbanded)
        {
            OpenGuildManagement(player, guild);
        }
        else
        {
            player.SendGump(new ManageGuildGump(player));
        }
    }

    protected override void BuildLayout(ref DynamicGumpBuilder builder)
    {
        builder.AddPage();
        builder.AddBackground(0, 0, 500, 260, 0x2422);
        builder.AddHtml(25, 20, 450, 25,
            "<center><BASEFONT COLOR=#F4F4F4 SIZE=4><B>CREATE GUILD</B></BASEFONT></center>");
        builder.AddHtml(25, 60, 450, 40,
            "Create your native guild directly. No deed, house, registration fee, or guildstone is required.");

        builder.AddHtml(25, 115, 120, 25, "Guild name:");
        builder.AddBackground(155, 110, 320, 26, 0xBB8);
        builder.AddTextEntry(160, 113, 315, 21, 0x481, 1, _guildName);

        builder.AddHtml(25, 151, 120, 25, "Abbreviation:");
        builder.AddBackground(155, 146, 320, 26, 0xBB8);
        builder.AddTextEntry(160, 149, 315, 21, 0x481, 2, _guildAbbreviation);

        builder.AddButton(415, 190, 0xF7, 0xF8, 1);
        builder.AddButton(345, 190, 0xF2, 0xF1, 0);
    }

    public override void OnResponse(NetState sender, in RelayInfo info)
    {
        if (info.ButtonID != 1 || sender?.Mobile is not PlayerMobile player ||
            player.Deleted || player.Guild != null)
        {
            return;
        }

        var guildName = (info.GetTextEntry(1) ?? string.Empty).AsSpan().Trim().FixHtml();
        var guildAbbreviation = (info.GetTextEntry(2) ?? string.Empty).AsSpan().Trim().FixHtml();

        if (!ValidateInput(player, guildName, guildAbbreviation))
        {
            player.SendGump(new ManageGuildGump(player, guildName, guildAbbreviation));
            return;
        }

        Guild guild = null;
        try
        {
            // The native constructor registers the guild and makes the player
            // its sole member/leader. No deed, house, fee, or guildstone is involved.
            guild = new Guild(player, guildName, guildAbbreviation);
            player.GuildTitle = "Guildmaster";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManageGuild] guild creation failed: {ex.Message}");
            guild?.Disband();
            player.SendMessage("The guild could not be created; nothing was changed.");
            player.SendGump(new ManageGuildGump(player, guildName, guildAbbreviation));
            return;
        }

        player.SendLocalizedMessage(1063238); // Your new guild has been founded.
        PlayerGuildBotRoster.OnGuildCreated(guild);
        OpenGuildManagement(player, guild);
    }

    private static bool ValidateInput(
        PlayerMobile player, string guildName, string guildAbbreviation)
    {
        if (guildName.Length == 0)
        {
            player.SendMessage("Guild name cannot be blank.");
            return false;
        }

        if (guildAbbreviation.Length == 0)
        {
            player.SendMessage("You must provide a guild abbreviation.");
            return false;
        }

        if (guildName.Length > Guild.NameLimit)
        {
            player.SendMessage($"Guild name cannot exceed {Guild.NameLimit} characters.");
            return false;
        }

        if (guildAbbreviation.Length > Guild.AbbrevLimit)
        {
            player.SendMessage(
                $"Guild abbreviation cannot exceed {Guild.AbbrevLimit} characters.");
            return false;
        }

        if (!BaseGuildGump.CheckProfanity(guildAbbreviation) ||
            BaseGuild.FindByAbbrev(guildAbbreviation) != null)
        {
            player.SendMessage("That guild abbreviation is not available.");
            return false;
        }

        if (!BaseGuildGump.CheckProfanity(guildName) ||
            BaseGuild.FindByName(guildName) != null)
        {
            player.SendMessage("That guild name is not available.");
            return false;
        }

        return true;
    }

    private static void OpenGuildManagement(PlayerMobile player, Guild guild)
    {
        if (guild?.Disbanded != false || player.Guild != guild)
        {
            return;
        }

        if (Guild.NewGuildSystem)
        {
            Guild.GuildGumpRequest(player);
        }
        else
        {
            GuildGump.DisplayTo(player, guild);
        }
    }
}
