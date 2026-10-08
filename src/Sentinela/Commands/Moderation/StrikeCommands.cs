using Discord.Interactions;
using Discord.WebSocket;
using Sentinela.Moderation;

namespace Sentinela.Commands.Moderation;

public class StrikeCommands : InteractionModuleBase<SocketInteractionContext>
{
    private readonly AntiSpamService _antiSpamService;

    public StrikeCommands(AntiSpamService antiSpamService)
    {
        _antiSpamService = antiSpamService;
    }

    [SlashCommand("strikereset", "Zera os strikes de um usuário.")]
    public async Task ResetAsync(SocketGuildUser user)
    {
        if (!CanManageStrikes())
        {
            await RespondAsync("Você não tem permissão para fazer isso.", ephemeral: true);
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("Esse comando só pode ser usado em um servidor.", ephemeral: true);
            return;
        }

        _antiSpamService.ResetWarnings(Context.Guild.Id, user.Id);
        await RespondAsync($"Os strikes de {user.Username} foram zerados.", ephemeral: true);
    }

    [SlashCommand("strikeremove", "Remove um strike de um usuário.")]
    public async Task RemoveAsync(SocketGuildUser user)
    {
        if (!CanManageStrikes())
        {
            await RespondAsync("Você não tem permissão para fazer isso.", ephemeral: true);
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("Esse comando só pode ser usado em um servidor.", ephemeral: true);
            return;
        }

        var remainingStrikes = _antiSpamService.RemoveWarning(Context.Guild.Id, user.Id);

        if (remainingStrikes is null)
        {
            await RespondAsync($"{user.Username} não tem strikes para remover.", ephemeral: true);
            return;
        }

        await RespondAsync($"Um strike foi removido de {user.Username}. Restam {remainingStrikes}.", ephemeral: true);
    }

    [SlashCommand("strikeset", "Define a quantidade de strikes de um usuário, de 0 a 5.")]
    public async Task SetAsync(SocketGuildUser user, int quantidade)
    {
        if (!CanManageStrikes())
        {
            await RespondAsync("Você não tem permissão para fazer isso.", ephemeral: true);
            return;
        }

        if (Context.Guild is null)
        {
            await RespondAsync("Esse comando só pode ser usado em um servidor.", ephemeral: true);
            return;
        }

        if (quantidade is < 0 or > 5)
        {
            await RespondAsync("A quantidade precisa estar entre 0 e 5.", ephemeral: true);
            return;
        }

        _antiSpamService.SetWarningCount(Context.Guild.Id, user.Id, quantidade);
        await RespondAsync($"Os strikes de {user.Username} foram definidos como {quantidade}.", ephemeral: true);
    }

    private bool CanManageStrikes()
    {
        return Context.User is SocketGuildUser moderator
            && (moderator.GuildPermissions.Administrator || moderator.GuildPermissions.ManageMessages);
    }
}
