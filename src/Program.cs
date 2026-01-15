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
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        private static async Task StartHealthCheckServer()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", "http://0.0.0.0:8080");
            var app = builder.Build();
            app.MapGet("/bot", () => "Bot is Healthy");
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
            };

            _client.InteractionCreated += async (x) => 
            {
                var ctx = new SocketInteractionContext(_client, x);
                await _interactions.ExecuteCommandAsync(ctx, _services);
            };

            await _client.LoginAsync(TokenType.Bot, Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN"));
            await _client.StartAsync();
            await Task.Delay(-1);
        }
    }

    public class DeobfuscationModule : InteractionModuleBase<SocketInteractionContext>
    {
        private readonly DeobfuscationService _service;
        public DeobfuscationModule(DeobfuscationService service) => _service = service;

        [SlashCommand("deobfuscate", "Process MoonSec file through Luau pipeline")]
        public async Task Deobfuscate(Attachment file)
        {
            await DeferAsync();
            
            // Starts the "Bot is typing..." status in Discord
            using var typing = Context.Channel.EnterTypingState();

            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(file.Url);
                var source = Encoding.UTF8.GetString(bytes);

                var decompiledResult = await _service.ProcessLuauPipelineWithRetryAsync(source);

                string hexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(decompiledResult));
                
                await Context.Channel.SendFileAsync(ms, hexName, "✅ **Luau Decompilation Complete**");
                await DeleteOriginalResponseAsync();
            }
            catch (Exception ex)
            {
                await FollowupAsync($"❌ Error: `{ex.Message}`");
            }
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _apiClient = new HttpClient();
        private const string LuauEndpoint = "http://127.0.0.1:3000/luau/decompile";
        
        // Policy: Retry 3 times with a 2s, 4s, and 8s delay
        private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

        public DeobfuscationService()
        {
            _retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(3, retryAttempt => 
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), 
                    (outcome, timespan, retryCount, context) =>
                    {
                        Console.WriteLine($"[Retry] Attempt {retryCount} failed. Retrying in {timespan.TotalSeconds}s...");
                    });
        }

        public async Task<string> ProcessLuauPipelineWithRetryAsync(string code)
        {
            var deobfuscator = new Deobfuscator();
            var result = deobfuscator.Deobfuscate(code);
            
            byte[] bytecode;
            using (var ms = new MemoryStream())
            {
                using (var serializer = new Serializer(ms))
                {
                    serializer.Serialize(result);
                }
                bytecode = ms.ToArray();
            }

            // Execute the POST request within the retry policy
            var response = await _retryPolicy.ExecuteAsync(async () => 
            {
                using var content = new ByteArrayContent(bytecode);
                return await _apiClient.PostAsync(LuauEndpoint, content);
            });

            if (!response.IsSuccessStatusCode)
                throw new Exception($"API failed after 3 retries ({response.StatusCode})");

            return await response.Content.ReadAsStringAsync();
        }
    }
}
