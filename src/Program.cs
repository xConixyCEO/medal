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

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private InteractionService _interactions;
        private IServiceProvider _services;

        public static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();
            // Start Health Check Server on 8080 for Render
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        public async Task RunAsync()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages,
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
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private static async Task StartHealthCheckServer()
        {
            var builder = WebApplication.CreateBuilder();
            // Listening on 8080 for Render's secondary port/health check
            builder.WebHost.UseSetting("urls", "http://0.0.0.0:8080");
            
            var app = builder.Build();
            app.MapGet("/bot", () => "Bot logic is active on 8080");
            app.MapGet("/", () => "Service is running");
            
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

        public DeobfuscationModule(DeobfuscationService service) => _service = service;

        [SlashCommand("deobfuscate", "Decompile a MoonSec file (Pipeline: File -> .bin -> API)")]
        public async Task Deobfuscate([Summary("file", "Upload .lua or .txt")] Attachment file)
        {
            await DeferAsync();

            if (!file.Filename.EndsWith(".lua") && !file.Filename.EndsWith(".txt"))
            {
                await FollowupAsync("❌ Only `.lua` or `.txt` files are allowed.");
                return;
            }

            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(file.Url);
                var inputSource = Encoding.UTF8.GetString(bytes);

                // 1. Devirtualize to Bytecode (.bin)
                // 2. POST to API for Decompilation
                var decompiledCode = await _service.FullDecompilePipelineAsync(inputSource);

                // Generate random hex name
                string hexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";

                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(decompiledCode));
                await Context.Channel.SendFileAsync(ms, hexName, "✅ **Decompilation Successful**");
                
                // Cleanup the "Bot is thinking" message
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
        // Points to the Medal Decompiler running on port 3000
        private const string ApiUrl = "http://127.0.0.1:3000/lua51/decompile";

        public async Task<string> FullDecompilePipelineAsync(string code)
        {
            // STEP 1: Devirtualize to Bytecode (.bin)
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

            // STEP 2: Send .bin to Decompiler API
            using var content = new ByteArrayContent(bytecode);
            var response = await _apiClient.PostAsync(ApiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Decompiler API error: {response.StatusCode}");
            }

            // STEP 3: Return the final decompiled text
            return await response.Content.ReadAsStringAsync();
        }
    }
}
