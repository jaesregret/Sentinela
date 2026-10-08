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

        if (message.Author is SocketWebhookUser webhookUser)
        {
            var webhookState = GetState(guild.Id, webhookUser.Id);

            if (RegisterFlood(webhookState, now, false, out _))
            {
                await RemoveWebhookAsync(message, guild, webhookUser.Id);
            }

            return;
        }

        if (message.Author is not SocketGuildUser user || guild.CurrentUser.Id == user.Id)
        {
            return;
        }

        if (!user.IsBot && user.GuildPermissions.Administrator)
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
            await BanForRepeatedFloodAsync(message, user, guild, state);
            return;
        }

        if (warningCount >= 6)
        {
            if (!guild.CurrentUser.GuildPermissions.KickMembers)
            {
                Console.WriteLine($"Não foi possível expulsar {user.Username}: falta a permissão Expulsar membros.");
                return;
            }

            await message.Channel.SendMessageAsync($"{user.Mention} foi expulso por continuar floodando o chat.");
            await user.KickAsync("Flood detectado repetidamente.");
            state.BanAfterRejoin = true;
            return;
        }

        var duration = GetPenalty(warningCount);

        if (!guild.CurrentUser.GuildPermissions.ModerateMembers)
        {
            Console.WriteLine($"Não foi possível aplicar timeout em {user.Username}: falta a permissão Moderar membros.");
            return;
        }

        try
        {
            await user.ModifyAsync(properties =>
            {
                properties.TimedOutUntil = now + duration;
            });

            await message.Channel.SendMessageAsync($"{user.Mention} não é para floodar o chat. Timeout aplicado por {FormatDuration(duration)}.");
        }
        catch (HttpException ex)
        {
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
            await message.Channel.SendMessageAsync($"O webhook {webhook.Name} foi removido por flood.");
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

    private static async Task BanForRepeatedFloodAsync(SocketMessage message, SocketGuildUser user, SocketGuild guild, UserSpamState state)
    {
        if (!guild.CurrentUser.GuildPermissions.BanMembers)
        {
            Console.WriteLine($"{user.Username} voltou após um kick e continuou floodando, mas falta a permissão Banir membros.");
            return;
        }

        try
        {
            await message.Channel.SendMessageAsync($"{user.Mention} foi banido por voltar e continuar floodando o chat.");
            await guild.AddBanAsync(user, reason: "Voltou após kick e continuou floodando.");
            state.BanAfterRejoin = false;
        }
        catch (HttpException ex)
        {
            Console.WriteLine($"Não foi possível banir {user.Username}: {ex.Message}");
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
        }
    }

    public void SetWarningCount(ulong guildId, ulong userId, int count)
    {
        var state = GetState(guildId, userId);

        lock (state)
        {
            state.Messages.Clear();
            state.WarningCount = count;
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
            if (countWarning)
            {
                state.WarningCount++;
            }

            warningCount = state.WarningCount;
            return true;
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
    }
}
