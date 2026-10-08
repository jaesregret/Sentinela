using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Sentinela.Moderation;

namespace Sentinela;

public static class ConfigurationLoader
{
    public static void LoadFromFile()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), ".env");

        if (!File.Exists(path))
        {
            return;
        }

        var lines = File.ReadAllLines(path);

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#"))
            {
                continue;
            }

            var index = line.IndexOf('=');

            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key)))
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

public class Program
{
    private readonly DiscordSocketClient _client;
    private readonly InteractionService _interactionService;
    private readonly AntiSpamService _antiSpamService;
    private readonly IServiceProvider _services;

    public static Task Main() => new Program().RunAsync();

    private Program()
    {
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
        });

        _interactionService = new InteractionService(_client);
        _antiSpamService = new AntiSpamService();
        _services = new ServiceCollection()
            .AddSingleton(_antiSpamService)
            .BuildServiceProvider();
    }

    private async Task RunAsync()
    {
        ConfigurationLoader.LoadFromFile();

        var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("DISCORD_TOKEN não foi definido. Crie um arquivo .env na raiz do projeto.");
            return;
        }

        _client.Log += message =>
        {
            Console.WriteLine(message);
            return Task.CompletedTask;
        };

        _client.Ready += ReadyAsync;
        _client.InteractionCreated += HandleInteractionAsync;
        _client.MessageReceived += HandleMessageAsync;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(Timeout.Infinite);
    }

    private async Task ReadyAsync()
    {
        await _interactionService.AddModulesAsync(typeof(Program).Assembly, _services);

        var guildId = Environment.GetEnvironmentVariable("DEV_GUILD_ID");

        if (ulong.TryParse(guildId, out var guild))
        {
            try
            {
                await _interactionService.RegisterCommandsToGuildAsync(guild);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Não foi possível registrar comandos no servidor de teste: {ex.Message}");
            }

            return;
        }

        try
        {
            await _interactionService.RegisterCommandsGloballyAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Não foi possível registrar comandos globalmente: {ex.Message}");
        }
    }

    private async Task HandleInteractionAsync(SocketInteraction interaction)
    {
        var context = new SocketInteractionContext(_client, interaction);
        await _interactionService.ExecuteCommandAsync(context, _services);
    }

    private async Task HandleMessageAsync(SocketMessage message)
    {
        await _antiSpamService.ProcessAsync(message);
    }
}
