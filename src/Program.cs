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
            // Start Health Check on the custom PORT1
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        private static async Task StartHealthCheckServer()
        {
            var builder = WebApplication.CreateBuilder();
            // Specifically looking for the "PORT1" variable you requested
            var port1 = Environment.GetEnvironmentVariable("PORT1") ?? "8080";
            builder.WebHost.UseSetting("urls", $"http://0.0.0.0:{port1}");
            
            var app = builder.Build();
            app.MapGet("/bot", () => $"Bot Healthy on custom PORT1: {port1}");
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

        [SlashCommand("deobfuscate", "Decompile MoonSec file via Shiny Luau")]
        public async Task Deobfuscate([Summary("file", "The .lua or .txt file")] Attachment file)
        {
            await DeferAsync();
            using var typing = Context.Channel.EnterTypingState();

            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(file.Url);
                var source = Encoding.UTF8.GetString(bytes);

                var result = await _service.ProcessPipelineAsync(source);

                string hexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(result));
                
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
            _retryPolicy = Policy
                .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode)
                .Or<HttpRequestException>()
                .WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)));
        }

        public async Task<string> ProcessPipelineAsync(string code)
        {
            var deobfuscator = new Deobfuscator();
            var res = deobfuscator.Deobfuscate(code);
            
            byte[] bin;
            using (var ms = new MemoryStream())
            {
                using (var ser = new Serializer(ms)) ser.Serialize(res);
                bin = ms.ToArray();
            }

            // Convert to Base64 (Required by Shiny's decompile_no_io logic)
            string b64 = Convert.ToBase64String(bin);

            var response = await _retryPolicy.ExecuteAsync(async () => 
            {
                return await _apiClient.PostAsync(LuauEndpoint, new StringContent(b64, Encoding.UTF8, "text/plain"));
            });

            if (!response.IsSuccessStatusCode) throw new Exception($"Shiny API error: {response.StatusCode}");
            return await response.Content.ReadAsStringAsync();
        }
    }
}
