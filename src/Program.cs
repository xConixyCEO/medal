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
            // Start Health Check on port 3000 for Render
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
            var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", $"http://0.0.0.0:{port}");
            var app = builder.Build();
            
            // Map to /bot to avoid collision with Medal's root
            app.MapGet("/bot", () => "MoonSec Bot is running.");
            await app.RunAsync();
        }

        private async Task HandleMessageAsync(SocketMessage message)
        {
            if (message is not SocketUserMessage userMsg || userMsg.Author.IsBot) return;
            if (!userMsg.Content.StartsWith(".i", StringComparison.OrdinalIgnoreCase)) return;

            var attachment = userMsg.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".lua"));
            if (attachment == null) return;

            // 1. Send initial reply
            var statusMessage = await userMsg.ReplyAsync("⏳ **Processing your file...**", allowedMentions: AllowedMentions.None);

            // 2. Offload work to a background thread
            _ = Task.Run(async () =>
            {
                try
                {
                    var service = _services.GetRequiredService<DeobfuscationService>();
                    
                    // Step A: Download
                    var bytes = await _httpClient.GetByteArrayAsync(attachment.Url);
                    var input = Encoding.UTF8.GetString(bytes);

                    // Step B: Devirtualize
                    await statusMessage.ModifyAsync(m => m.Content = "🔍 **Step 1: Devirtualizing Lua...**");
                    var deobfuscated = service.DevirtualizeToSource(input);
                    
                    // Step C: Decompile
                    await statusMessage.ModifyAsync(m => m.Content = "🚀 **Step 2: Decompiling with Medal...**");
                    var result = await service.DecompileWithMedalAsync(deobfuscated);

                    // 3. Send final result
                    await statusMessage.DeleteAsync();

                    if (result.Length > 2000)
                    {
                        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(result));
                        await userMsg.Channel.SendFileAsync(ms, "decompiled.lua", $"✅ **Success:** `{attachment.Filename}`");
                    }
                    else
                    {
                        await userMsg.Channel.SendMessageAsync($"✅ **Success:** `{attachment.Filename}`\n```lua\n{result}\n```");
                    }
                }
                catch (Exception ex)
                {
                    await statusMessage.ModifyAsync(m => m.Content = $"❌ **Error:** `{ex.Message}`");
                    Console.WriteLine($"[ERROR] {ex}");
                }
            });
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        public string DevirtualizeToSource(string code) 
        {
            // This call uses NLua/KeraLua (requires liblua54.so symlink)
            return new Deobfuscator().Deobfuscate(code).ToString();
        }

        public async Task<string> DecompileWithMedalAsync(string luaSource)
        {
            // Send raw bytes to the Medal Rust service on port 8080
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(luaSource));
            var response = await _httpClient.PostAsync("http://127.0.0.1:8080/lua51/decompile", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Medal API Error: {err}");
            }
            return await response.Content.ReadAsStringAsync();
        }
    }
}
