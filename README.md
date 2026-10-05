# Sentinela

Bot de moderação e proteção para Discord.

## Stack (Fase 1)

- C# / .NET 8
- Discord.Net
- Serilog
- Dependency Injection nativa

## Configuração

1. Crie um aplicativo em https://discord.com/developers/applications
2. Vá em **Bot** → Reset Token e copie o token
3. Ative as intents necessárias (por enquanto só precisamos de Guilds)
4. Convide o bot com as permissões mínimas necessárias (não use Administrator)

Crie o arquivo `.env` na raiz do projeto:

```env
DISCORD_TOKEN= bot token
DEV_GUILD_ID= id do servidor de teste
