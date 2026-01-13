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
        private static HttpClient _httpClient = new HttpClient();

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
                    var response = await _httpClient.GetAsync("http://localhost:8080/");
                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("✅ Medal service is ready");
                        return;
                    }
                }
                catch { }
                
                await Task.Delay(1000);
                Console.WriteLine($"⏳ Waiting for Medal service... ({i + 1}/{maxRetries})");
            }
            
            throw new Exception("Medal service failed to start");
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

            if (!userMessage.Attachments.Any())
            {
                await userMessage.ReplyAsync("❌ Please attach a `.lua` file with the `.i` command.", allowedMentions: AllowedMentions.None);
                return;
            }

            var attachment = userMessage.Attachments.First();
            if (!attachment.Filename.EndsWith(".lua"))
            {
                await userMessage.ReplyAsync("❌ Only `.lua` files are allowed.", allowedMentions: AllowedMentions.None);
                return;
            }

            var dmChannel = await userMessage.Author.CreateDMChannelAsync();
            var loadingMessage = await dmChannel.SendMessageAsync("📤 ⏳ **Processing your file...**");

            try
            {
                var service = _services.GetRequiredService<DeobfuscationService>();
                
                var bytes = await _httpClient.GetByteArrayAsync(attachment.Url);
                var input = Encoding.UTF8.GetString(bytes);

                await loadingMessage.ModifyAsync(msg => msg.Content = "📤 ⏳ **Deobfuscating with MoonSec...**");
                var deobfuscatedSource = service.DevirtualizeToSource(input);
                
                await loadingMessage.ModifyAsync(msg => msg.Content = "📤 ⏳ **Decompiling with Medal...**");
                var decompiled = await service.DecompileWithMedalAsync(deobfuscatedSource);

                await loadingMessage.DeleteAsync();
                
                if (decompiled.Length > 2000)
                {
                    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(decompiled));
                    await dmChannel.SendFileAsync(stream, "decompiled.lua", $"✅ Deobfuscation complete for `{attachment.Filename}`");
                }
                else
                {
                    await dmChannel.SendMessageAsync($"✅ Deobfuscation complete for `{attachment.Filename}`:\n```lua\n{decompiled}\n```");
                }
            }
            catch (Exception ex)
            {
                await loadingMessage.ModifyAsync(msg => msg.Content = $"❌ Processing failed: `{ex.Message}`");
                Console.WriteLine($"❌ Error: {ex}");
            }
        }
    }

    public class DeobfuscationService
    {
        private static HttpClient _httpClient = new HttpClient();
        
        public string DevirtualizeToSource(string code)
        {
            var result = new Deobfuscator().Deobfuscate(code);
            return result.ToString();
        }

        public async Task<string> DecompileWithMedalAsync(string luaSource)
        {
            var sourceBytes = Encoding.UTF8.GetBytes(luaSource);
            
            using var content = new MultipartFormDataContent();
            using var byteContent = new ByteArrayContent(sourceBytes);
            content.Add(byteContent, "file", "input.lua");

            var response = await _httpClient.PostAsync("http://localhost:8080/lua51/decompile", content);
            
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception($"Medal HTTP error: {response.StatusCode} - {error}");
            }

            return await response.Content.ReadAsStringAsync();
        }
    }
}
