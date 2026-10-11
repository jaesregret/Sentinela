using System.Collections.Concurrent;
using Discord;
using Discord.Net;
using Discord.WebSocket;

namespace Sentinela.Moderation;

public sealed class AntiSpamService
{
    private readonly TimeSpan _window = TimeSpan.FromSeconds(8);
    private readonly int _threshold = 6;
    private readonly ConcurrentDictionary<(ulong GuildId, ulong UserId), UserSpamState> _states = new();

    public async Task ProcessAsync(SocketMessage message)
    {
        if (message.Channel is not SocketGuildChannel channel)
        {
            return;
        }

        var guild = channel.Guild;
        var now = DateTimeOffset.UtcNow;

        if (message.Source == MessageSource.Webhook || message.Author is SocketWebhookUser)
        {
            if (message.Author is not SocketWebhookUser webhookUser)
            {
                return;
            }

            var webhookId = webhookUser.WebhookId;
            var webhookState = GetState(guild.Id, webhookId);

            if (RegisterFlood(webhookState, now, false, out _))
            {
                await RemoveWebhookAsync(message, guild, webhookId);
            }

            return;
        }

        if (message.Author is not SocketGuildUser user || guild.CurrentUser.Id == user.Id)
        {
            return;
        }

        if (!user.IsBot && IsStaff(user))
        {
            return;
        }

        var state = GetState(guild.Id, user.Id);

        if (!RegisterFlood(state, now, !user.IsBot, out var warningCount))
        {
            return;
        }

        if (user.IsBot)
        {
            await BanBotAsync(user, guild);
            return;
        }

        if (state.BanAfterRejoin)
        {
            await BanForRepeatedFloodAsync(message, user, guild, state, warningCount - 1);
            return;
        }

        if (warningCount >= 6)
        {
            if (!guild.CurrentUser.GuildPermissions.KickMembers)
            {
                Console.WriteLine($"Não foi possível expulsar {user.Username}: falta a permissão Expulsar membros.");
                FinishPunishment(state, false, warningCount, false);
                return;
            }

            try
            {
                await user.KickAsync("Flood detectado repetidamente.");
                FinishPunishment(state, true, warningCount, true);
                await SendNoticeAsync(message, $"{user.Mention} foi expulso por continuar floodando o chat.");
            }
            catch (HttpException ex)
            {
                FinishPunishment(state, false, warningCount, false);
                Console.WriteLine($"Não foi possível expulsar {user.Username}: {ex.Message}");
            }

            return;
        }

        var duration = GetPenalty(warningCount);

        if (!guild.CurrentUser.GuildPermissions.ModerateMembers)
        {
            Console.WriteLine($"Não foi possível aplicar timeout em {user.Username}: falta a permissão Moderar membros.");
            FinishPunishment(state, false, warningCount, false);
            return;
        }

        try
        {
            await user.ModifyAsync(properties =>
            {
                properties.TimedOutUntil = now + duration;
            });

            FinishPunishment(state, true, warningCount, false);
            await SendNoticeAsync(message, $"{user.Mention} não é para floodar o chat. Timeout aplicado por {FormatDuration(duration)}.");
        }
        catch (HttpException ex)
        {
            FinishPunishment(state, false, warningCount, false);
            Console.WriteLine($"Não foi possível aplicar timeout em {user.Username}: {ex.Message}");
        }
    }

    private async Task RemoveWebhookAsync(SocketMessage message, SocketGuild guild, ulong webhookId)
    {
        if (!guild.CurrentUser.GuildPermissions.ManageWebhooks)
        {
            Console.WriteLine("Foi detectado flood de webhook, mas falta a permissão Gerenciar webhooks.");
            return;
        }

        try
        {
            var webhooks = await guild.GetWebhooksAsync();
            var webhook = webhooks.FirstOrDefault(item => item.Id == webhookId);

            if (webhook == null)
            {
                Console.WriteLine($"O webhook {webhookId} atingiu o limite de flood, mas não foi encontrado no servidor.");
                return;
            }

            await webhook.DeleteAsync();
            await SendNoticeAsync(message, $"O webhook {webhook.Name} foi removido por flood.");
        }
        catch (HttpException ex)
        {
            Console.WriteLine($"Não foi possível remover o webhook {webhookId}: {ex.Message}");
        }
    }

    private static async Task BanBotAsync(SocketGuildUser bot, SocketGuild guild)
    {
        if (!guild.CurrentUser.GuildPermissions.BanMembers)
        {
            Console.WriteLine($"O bot {bot.Username} atingiu o limite de flood, mas falta a permissão Banir membros.");
            return;
        }

        try
        {
            await guild.AddBanAsync(bot, reason: "Bot atingiu o limite de flood.");
        }
        catch (HttpException ex)
        {
            Console.WriteLine($"Não foi possível banir o bot {bot.Username}: {ex.Message}");
        }
    }

    private static async Task BanForRepeatedFloodAsync(SocketMessage message, SocketGuildUser user, SocketGuild guild, UserSpamState state, int warningCount)
    {
        if (!guild.CurrentUser.GuildPermissions.BanMembers)
        {
            Console.WriteLine($"{user.Username} voltou após um kick e continuou floodando, mas falta a permissão Banir membros.");
            FinishPunishment(state, false, warningCount, false);
            return;
        }

        try
        {
            await guild.AddBanAsync(user, reason: "Voltou após kick e continuou floodando.");
            FinishPunishment(state, true, warningCount, false);
            await SendNoticeAsync(message, $"{user.Mention} foi banido por voltar e continuar floodando o chat.");
        }
        catch (HttpException ex)
        {
            FinishPunishment(state, false, warningCount, false);
            Console.WriteLine($"Não foi possível banir {user.Username}: {ex.Message}");
        }
    }

    private static bool IsStaff(SocketGuildUser user)
    {
        var permissions = user.GuildPermissions;

        return permissions.Administrator
            || permissions.ManageGuild
            || permissions.ManageMessages
            || permissions.ManageRoles
            || permissions.ModerateMembers
            || permissions.KickMembers
            || permissions.BanMembers;
    }

    private static async Task SendNoticeAsync(SocketMessage message, string text)
    {
        try
        {
            await message.Channel.SendMessageAsync(text);
        }
        catch (HttpException ex)
        {
            Console.WriteLine($"Não foi possível enviar o aviso de moderação: {ex.Message}");
        }
    }

    private UserSpamState GetState(ulong guildId, ulong userId)
    {
        return _states.GetOrAdd((guildId, userId), _ => new UserSpamState());
    }

    public void ResetWarnings(ulong guildId, ulong userId)
    {
        var state = GetState(guildId, userId);

        lock (state)
        {
            state.Messages.Clear();
            state.WarningCount = 0;
            state.BanAfterRejoin = false;
            state.PunishmentPending = false;
        }
    }

    public void SetWarningCount(ulong guildId, ulong userId, int count)
    {
        var state = GetState(guildId, userId);

        lock (state)
        {
            state.Messages.Clear();
            state.WarningCount = count;
            state.BanAfterRejoin = false;
            state.PunishmentPending = false;
        }
    }

    public int? RemoveWarning(ulong guildId, ulong userId)
    {
        var state = GetState(guildId, userId);

        lock (state)
        {
            if (state.WarningCount == 0)
            {
                return null;
            }

            state.Messages.Clear();
            state.WarningCount--;
            state.BanAfterRejoin = false;
            state.PunishmentPending = false;
            return state.WarningCount;
        }
    }

    private bool RegisterFlood(UserSpamState state, DateTimeOffset now, bool countWarning, out int warningCount)
    {
        lock (state)
        {
            state.Messages.Enqueue(now);

            while (state.Messages.Count > 0 && now - state.Messages.Peek() > _window)
            {
                state.Messages.Dequeue();
            }

            if (state.Messages.Count < _threshold)
            {
                warningCount = state.WarningCount;
                return false;
            }

            state.Messages.Clear();
            if (state.PunishmentPending)
            {
                warningCount = state.WarningCount;
                return false;
            }

            if (countWarning)
            {
                state.PunishmentPending = true;
                warningCount = state.WarningCount + 1;
            }
            else
            {
                warningCount = state.WarningCount;
            }

            return true;
        }
    }

    private static void FinishPunishment(UserSpamState state, bool succeeded, int warningCount, bool wasKicked)
    {
        lock (state)
        {
            if (succeeded)
            {
                state.WarningCount = warningCount;
                state.BanAfterRejoin = wasKicked;
            }

            state.PunishmentPending = false;
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return duration.TotalDays >= 2 ? $"{duration.TotalDays:0} dias" : "1 dia";
        }

        if (duration.TotalHours >= 1)
        {
            return duration.TotalHours >= 2 ? $"{duration.TotalHours:0} horas" : "1 hora";
        }

        if (duration.TotalMinutes >= 1)
        {
            return duration.TotalMinutes >= 2 ? $"{duration.TotalMinutes:0} minutos" : "1 minuto";
        }

        return $"{duration.TotalSeconds:0} segundos";
    }

    private static TimeSpan GetPenalty(int warningCount)
    {
        return warningCount switch
        {
            1 => TimeSpan.FromMinutes(1),
            2 => TimeSpan.FromMinutes(10),
            3 => TimeSpan.FromHours(2),
            4 => TimeSpan.FromHours(12),
            _ => TimeSpan.FromHours(24)
        };
    }

    private sealed class UserSpamState
    {
        public Queue<DateTimeOffset> Messages { get; } = new();
        public int WarningCount { get; set; }
        public bool BanAfterRejoin { get; set; }
        public bool PunishmentPending { get; set; }
    }
}
