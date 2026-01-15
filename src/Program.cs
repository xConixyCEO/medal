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
            _ = StartHealthCheckServer();
            await new Program().RunAsync();
        }

        public async Task RunAsync()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                // Added MessageContent intent if you plan to use prefixes later, 
                // but keep it standard for Slash Commands.
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
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
            var portStr = Environment.GetEnvironmentVariable("PORT") ?? "8080";
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting("urls", $"http://0.0.0.0:{portStr}");
            
            var app = builder.Build();
            app.MapGet("/bot", () => "Active"); // Keep Render alive
            await app.RunAsync();
        }

        private async Task ReadyAsync()
        {
            await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
            await _interactions.RegisterCommandsGloballyAsync(true);
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

        [SlashCommand("deobfuscate", "Upload .lua/.txt to decompile via MoonSec Pipeline")]
        public async Task Deobfuscate([Summary("file", "The obfuscated .lua or .txt file")] Attachment file)
        {
            await DeferAsync();

            // Accept only .lua and .txt files
            string ext = Path.GetExtension(file.Filename).ToLower();
            if (ext != ".lua" && ext != ".txt")
            {
                await FollowupAsync("❌ Invalid file type. Please upload a `.lua` or `.txt` file.");
                return;
            }

            try
            {
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(file.Url);
                var sourceCode = Encoding.UTF8.GetString(bytes);

                // 1. Process to .bin (Devirtualize)
                // 2. Send to API and get string result
                var decompiledResult = await _service.GetDecompiledSourceAsync(sourceCode);

                // 3. Generate random hex filename
                string hexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";

                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(decompiledResult));
                await Context.Channel.SendFileAsync(ms, hexName, "✅ **Decompilation Successful:**");
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
        // Replace with your actual Render API URL
        private const string ApiUrl = "https://medal-1.onrender.com/luau/decompile";

        public async Task<string> GetDecompiledSourceAsync(string code)
        {
            // Step 1: Devirtualize into bytecode (.bin format)
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

            // Step 2: Send .bin data to the API via POST
            using var content = new ByteArrayContent(bytecode);
            var response = await _apiClient.PostAsync(ApiUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"API returned {response.StatusCode}");
            }

            // Return the decompiled string
            return await response.Content.ReadAsStringAsync();
        }
    }
}

