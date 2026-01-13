using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Builder;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Threading;
using MoonsecDeobfuscator.Deobfuscation;
using MoonsecDeobfuscator.Deobfuscation.Bytecode;

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private InteractionService _interactions;
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
                GatewayIntents = GatewayIntents.Guilds,
                AlwaysDownloadUsers = true
            });

            _interactions = new InteractionService(_client.Rest);
            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_interactions)
                .AddSingleton<DeobfuscationService>()
                .BuildServiceProvider();

            _client.Log += msg => { Console.WriteLine(msg); return Task.CompletedTask; };
            _client.Ready += ReadyAsync;
            _client.InteractionCreated += HandleInteractionAsync;

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            if (string.IsNullOrEmpty(token))
                throw new Exception("DISCORD_BOT_TOKEN missing");

            // Wait for Medal service to be ready
            await WaitForMedalService();

            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
            await Task.Delay(-1);
        }

        private static async Task WaitForMedalService()
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
                catch { /* Medal not ready yet */ }
                
                await Task.Delay(1000);
                Console.WriteLine($"⏳ Waiting for Medal service... ({i + 1}/{maxRetries})");
            }
            
            throw new Exception("Medal service failed to start");
        }

        private static async Task StartHealthCheckServer()
        {
            var portStr = Environment.GetEnvironmentVariable("PORT") ?? "3000";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Any, int.Parse(portStr)));
            
            var app = builder.Build();
            app.MapGet("/", () => "MoonSec Bot is running.");
            
            Console.WriteLine($"🌐 Health check listening on port {portStr}");
            await app.RunAsync();
        }

        private async Task ReadyAsync()
        {
            await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            await _interactions.RegisterCommandsGloballyAsync(true);
            Console.WriteLine($"✅ Connected as {_client.CurrentUser}");
        }

        private async Task HandleInteractionAsync(SocketInteraction interaction)
        {
            var context = new SocketInteractionContext(_client, interaction);
            await _interactions.ExecuteCommandAsync(context, _services);
        }
    }

    public class DeobfuscationModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DeobfuscationService _service;

        public DeobfuscationModule(DeobfuscationService service)
        {
            _service = service;
        }

        [SlashCommand("deobfuscate", "Deobfuscates a MoonSec-protected Lua file.")]
        public async Task Deobfuscate(
            [Summary("file", "Lua file to deobfuscate")] Attachment file)
        {
            await DeferAsync();

            if (!file.Filename.EndsWith(".lua"))
            {
                await FollowupAsync("❌ Only `.lua` files are allowed.");
                return;
            }

            try
            {
                // Step 1: Download file
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(file.Url);
                var input = Encoding.UTF8.GetString(bytes);

                // Step 2: Deobfuscate and generate bytecode (.luac)
                await ModifyOriginalResponseAsync(msg => msg.Content = "🔄 **Step 1/3:** Deobfuscating with MoonSec...");
                var bytecode = _service.Devirtualize(input);
                
                var tempLuac = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.luac");
                await File.WriteAllBytesAsync(tempLuac, bytecode);

                // Step 3: Decompile with Medal via HTTP
                await ModifyOriginalResponseAsync(msg => msg.Content = "🔄 **Step 2/3:** Decompiling with Medal...");
                var decompiled = await _service.DecompileWithMedalAsync(bytecode);

                // Clean up temp file
                try { File.Delete(tempLuac); } catch { }

                if (string.IsNullOrWhiteSpace(decompiled))
                {
                    await ModifyOriginalResponseAsync(msg => msg.Content = "❌ Medal decompilation failed.");
                    return;
                }

                // Step 4: Send decompiled code
                await ModifyOriginalResponseAsync(msg => msg.Content = "✅ **Step 3/3:** Sending result...");

                if (decompiled.Length > 2000)
                {
                    await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(decompiled));
                    await FollowupWithFileAsync(
                        stream,
                        "decompiled.lua",
                        text: $"✅ Decompiled by {_client.CurrentUser}"
                    );
                }
                else
                {
                    var embed = new EmbedBuilder()
                        .WithTitle("✅ Deobfuscation Complete")
                        .WithDescription($"```lua\n{decompiled}\n```")
                        .WithColor(Color.Green)
                        .WithFooter($"Processed for {Context.User.Username}")
                        .Build();
                    
                    await FollowupAsync(embed: embed);
                }

                await DeleteOriginalResponseAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex}");
                await FollowupAsync($"❌ Processing failed: `{ex.Message}`");
            }
        }
    }

    public class DeobfuscationService
    {
        private static HttpClient _httpClient = new HttpClient();
        
        public byte[] Devirtualize(string code)
        {
            var result = new Deobfuscator().Deobfuscate(code);
            using var ms = new MemoryStream();
            using var serializer = new Serializer(ms);
            serializer.Serialize(result);
            return ms.ToArray();
        }

        public async Task<string> DecompileWithMedalAsync(byte[] bytecode)
        {
            // Create multipart form data
            using var content = new MultipartFormDataContent();
            using var byteContent = new ByteArrayContent(bytecode);
            content.Add(byteContent, "file", "input.luac");

            // Send to Medal HTTP endpoint
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
