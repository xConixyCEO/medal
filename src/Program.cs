using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Builder;
using MoonsecDeobfuscator.Deobfuscation;
using MoonsecDeobfuscator.Deobfuscation.Bytecode;
using Polly;
using Polly.Retry;

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private InteractionService _interactions;
        private IServiceProvider _services;

        public static async Task Main(string[] args)
        {
            // Start Health Check on 8080 for Render
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        private static async Task StartHealthCheckServer()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", "http://0.0.0.0:8080");
            var app = builder.Build();
            app.MapGet("/bot", () => "Shiny Bot is Online");
            await app.RunAsync();
        }

        public async Task RunAsync()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
            });

            _interactions = new InteractionService(_client.Rest);
            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_interactions)
                .AddSingleton<DeobfuscationService>()
                .BuildServiceProvider();

            _client.Ready += async () => 
            {
                await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
                await _interactions.RegisterCommandsGloballyAsync(true);
                Console.WriteLine("✅ Slash Commands Registered and Ready.");
            };

            _client.InteractionCreated += async (x) => 
            {
                var ctx = new SocketInteractionContext(_client, x);
                await _interactions.ExecuteCommandAsync(ctx, _services);
            };

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();
            await Task.Delay(-1);
        }
    }

    public class DeobfuscationModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DeobfuscationService _service;
        public DeobfuscationModule(DeobfuscationService service) => _service = service;

        [SlashCommand("deobfuscate", "Decompile MoonSec file via Shiny Luau")]
        public async Task Deobfuscate(Attachment file)
        {
            await DeferAsync();
            // Shows "Bot is typing..." in Discord during the process
            using var typing = Context.Channel.EnterTypingState();

            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(file.Url);
                var source = Encoding.UTF8.GetString(bytes);

                // Pipeline: Devirtualize -> .bin -> Base64 -> Shiny API
                var decompiledResult = await _service.ProcessLuauPipelineWithRetryAsync(source);

                string hexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(decompiledResult));
                
                await Context.Channel.SendFileAsync(ms, hexName, "✅ **Luau Decompilation Complete**");
                await DeleteOriginalResponseAsync();
            }
            catch (Exception ex)
            {
                await FollowupAsync($"❌ **Error:** `{ex.Message}`");
            }
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _apiClient = new HttpClient();
        private const string LuauEndpoint = "http://127.0.0.1:3000/luau/decompile";
        private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

        public DeobfuscationService()
        {
            // Retry 3 times with exponential backoff (2s, 4s, 8s)
            _retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)), 
                (outcome, timespan, retryCount, context) => {
                    Console.WriteLine($"[Retry] Attempt {retryCount} failed. Retrying...");
                });
        }

        public async Task<string> ProcessLuauPipelineWithRetryAsync(string code)
        {
            // 1. Devirtualize MoonSec
            var deobfuscator = new Deobfuscator();
            var result = deobfuscator.Deobfuscate(code);
            
            // 2. Serialize to .bin Bytecode
            byte[] bytecode;
            using (var ms = new MemoryStream())
            {
                using (var serializer = new Serializer(ms))
                {
                    serializer.Serialize(result);
                }
                bytecode = ms.ToArray();
            }

            // 3. Base64 Encode (matches Shiny's Rust BASE64_STANDARD.decode check)
            string base64Data = Convert.ToBase64String(bytecode);

            // 4. POST to internal Shiny service
            var response = await _retryPolicy.ExecuteAsync(async () => 
            {
                using var content = new StringContent(base64Data, Encoding.UTF8, "text/plain");
                return await _apiClient.PostAsync(LuauEndpoint, content);
            });

            if (!response.IsSuccessStatusCode)
            {
                string err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Shiny API error {response.StatusCode}: {err}");
            }

            return await response.Content.ReadAsStringAsync();
        }
    }
}
