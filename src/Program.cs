using Discord;
using Discord.WebSocket;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using MoonsecDeobfuscator.Deobfuscation;

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private IServiceProvider _services;
        private static readonly HttpClient _httpClient = new HttpClient();

        public static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();
            // Start internal health check on 8080
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        public async Task RunAsync()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.MessageContent | GatewayIntents.DirectMessages
            });

            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton<DeobfuscationService>()
                .BuildServiceProvider();

            _client.Log += msg => { Console.WriteLine(msg); return Task.CompletedTask; };
            _client.MessageReceived += HandleMessageAsync;

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
            await Task.Delay(-1);
        }

        private static async Task StartHealthCheckServer()
        {
            // Now explicitly using 8080 for the Bot
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", "http://0.0.0.0:8080");
            
            var app = builder.Build();
            app.MapGet("/bot", () => "Bot is alive on internal port 8080.");
            await app.RunAsync();
        }

        private async Task HandleMessageAsync(SocketMessage message)
        {
            if (message is not SocketUserMessage userMsg || userMsg.Author.IsBot) return;
            if (!userMsg.Content.StartsWith(".i", StringComparison.OrdinalIgnoreCase)) return;

            var attachment = userMsg.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".lua"));
            if (attachment == null) return;

            var statusMessage = await userMsg.ReplyAsync("⏳ **Processing...**");

            _ = Task.Run(async () =>
            {
                try
                {
                    var service = _services.GetRequiredService<DeobfuscationService>();
                    var bytes = await _httpClient.GetByteArrayAsync(attachment.Url);
                    var input = Encoding.UTF8.GetString(bytes);

                    await statusMessage.ModifyAsync(m => m.Content = "🔍 **Devirtualizing...**");
                    var deobfuscated = service.DevirtualizeToSource(input);
                    
                    await statusMessage.ModifyAsync(m => m.Content = "🚀 **Decompiling (Medal on 3000)...**");
                    var result = await service.DecompileWithMedalAsync(deobfuscated);

                    await statusMessage.DeleteAsync();

                    if (result.Length > 2000)
                    {
                        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(result));
                        await userMsg.Channel.SendFileAsync(ms, "result.lua", $"✅ Done: `{attachment.Filename}`");
                    }
                    else
                    {
                        await userMsg.Channel.SendMessageAsync($"✅ Done!\n```lua\n{result}\n```");
                    }
                }
                catch (Exception ex)
                {
                    await statusMessage.ModifyAsync(m => m.Content = $"❌ Error: `{ex.Message}`");
                }
            });
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public string DevirtualizeToSource(string code) => new Deobfuscator().Deobfuscate(code).ToString();

        public async Task<string> DecompileWithMedalAsync(string luaSource)
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(luaSource));
            // Now calling port 3000 where the Rust service is listening
            var response = await _httpClient.PostAsync("http://127.0.0.1:3000/lua51/decompile", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Medal Error: {err}");
            }
            return await response.Content.ReadAsStringAsync();
        }
    }
}
