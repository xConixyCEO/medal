using Discord;
using Discord.WebSocket;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Builder;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MoonsecDeobfuscator.Deobfuscation;

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private CommandService _commands;
        private IServiceProvider _services;
        private static readonly HttpClient _httpClient = new HttpClient();

        public static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        public async Task RunAsync()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.MessageContent | GatewayIntents.DirectMessages,
                AlwaysDownloadUsers = true
            });

            _commands = new CommandService();
            
            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_commands)
                .AddSingleton<DeobfuscationService>()
                .BuildServiceProvider();

            _client.Log += msg => { Console.WriteLine(msg); return Task.CompletedTask; };
            _client.Ready += ReadyAsync;
            _client.MessageReceived += HandleMessageAsync;

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            if (string.IsNullOrEmpty(token))
                throw new Exception("DISCORD_BOT_TOKEN missing");

            // Ensure Medal is up before connecting to Discord
            await WaitForMedalService();

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
            await Task.Delay(-1);
        }

        private async Task WaitForMedalService()
        {
            var maxRetries = 30;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    // The Medal Rust service returns "yep web-server is on" at root
                    var response = await _httpClient.GetAsync("http://127.0.0.1:8080/");
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("✅ Medal service is ready");
                        return;
                    }
                }
                catch { /* Ignore connection errors during wait */ }
                
                Console.WriteLine($"⏳ Waiting for Medal service... ({i + 1}/{maxRetries})");
                await Task.Delay(2000);
            }
            throw new Exception("Medal service failed to start on port 8080");
        }

        private static async Task StartHealthCheckServer()
        {
            var portStr = Environment.GetEnvironmentVariable("PORT") ?? "3000";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", $"http://0.0.0.0:{portStr}");
            
            var app = builder.Build();
            app.MapGet("/", () => "MoonSec Bot is running.");
            
            Console.WriteLine($"🌐 Health check listening on port {portStr}");
            await app.RunAsync();
        }

        private async Task ReadyAsync()
        {
            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            Console.WriteLine($"✅ Connected as {_client.CurrentUser}");
        }

        private async Task HandleMessageAsync(SocketMessage message)
        {
            if (message is not SocketUserMessage userMessage || userMessage.Author.IsBot)
                return;

            if (!userMessage.Content.StartsWith(".i", StringComparison.OrdinalIgnoreCase))
                return;

            var attachment = userMessage.Attachments.FirstOrDefault(a => a.Filename.EndsWith(".lua"));
            if (attachment == null)
            {
                await userMessage.ReplyAsync("❌ Please attach a valid `.lua` file.");
                return;
            }

            var dmChannel = await userMessage.Author.CreateDMChannelAsync();
            var loadingMessage = await dmChannel.SendMessageAsync("📤 ⏳ **Processing...**");

            try
            {
                var service = _services.GetRequiredService<DeobfuscationService>();
                
                // Download file
                var bytes = await _httpClient.GetByteArrayAsync(attachment.Url);
                var input = Encoding.UTF8.GetString(bytes);

                // Step 1: Devirtualization (Uses NLua - needs the symlinks in Docker)
                await loadingMessage.ModifyAsync(msg => msg.Content = "📤 ⏳ **Running Devirtualizer...**");
                var deobfuscatedSource = service.DevirtualizeToSource(input);
                
                // Step 2: Decompilation (Calls Medal Rust Service)
                await loadingMessage.ModifyAsync(msg => msg.Content = "📤 ⏳ **Decompiling with Medal...**");
                var decompiled = await service.DecompileWithMedalAsync(deobfuscatedSource);

                await loadingMessage.DeleteAsync();
                
                if (decompiled.Length > 1900)
                {
                    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(decompiled));
                    await dmChannel.SendFileAsync(stream, "decompiled.lua", $"✅ Done: `{attachment.Filename}`");
                }
                else
                {
                    await dmChannel.SendMessageAsync($"✅ Result for `{attachment.Filename}`:\n```lua\n{decompiled}\n```");
                }
            }
            catch (Exception ex)
            {
                await loadingMessage.ModifyAsync(msg => msg.Content = $"❌ Error: `{ex.Message}`");
                Console.WriteLine($"[ERROR] {ex}");
            }
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        
        public string DevirtualizeToSource(string code)
        {
            // This call relies on NLua/KeraLua finding 'liblua54.so'
            var result = new Deobfuscator().Deobfuscate(code);
            return result.ToString();
        }

        public async Task<string> DecompileWithMedalAsync(string luaSource)
        {
            // IMPORTANT: Your Rust code uses 'body: Bytes'.
            // We must send the raw string bytes, NOT a Multipart form.
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(luaSource));
            
            // Ensure the Medal service is started with the --lua51 flag to enable this route
            var response = await _httpClient.PostAsync("http://127.0.0.1:8080/lua51/decompile", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Medal API Error ({response.StatusCode}): {error}");
            }

            return await response.Content.ReadAsStringAsync();
        }
    }
}
