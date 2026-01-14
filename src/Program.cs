using Discord;
using Discord.WebSocket;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using System.Net;
using Microsoft.AspNetCore.Builder;
using MoonsecDeobfuscator.Deobfuscation;
using MoonsecDeobfuscator.Deobfuscation.Bytecode;
using System.Diagnostics;

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private CommandService _commands;
        private IServiceProvider _services;

        public static async Task Main(string[] args)
        {
            DotNetEnv.Env.Load();
            _ = StartHealthCheckServer(); // Port 8080 for internal health
            await new Program().RunAsync();
        }

        public async Task RunAsync()
        {
            _client = new DiscordSocketClient(new DiscordSocketConfig
            {
                // REQUIRED: You must enable this in the Discord Dev Portal
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
                AlwaysDownloadUsers = true
            });

            _commands = new CommandService();

            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_commands)
                .AddSingleton<DeobfuscationService>()
                .BuildServiceProvider();

            _client.Log += msg => { Console.WriteLine(msg); return Task.CompletedTask; };
            _client.MessageReceived += HandleCommandAsync;

            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private async Task HandleCommandAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage message || message.Author.IsBot) return;

            int argPos = 0;
            // Matches .d or .D
            if (message.HasStringPrefix(".d", ref argPos, StringComparison.OrdinalIgnoreCase))
            {
                var context = new SocketCommandContext(_client, message);
                await _commands.ExecuteAsync(context, argPos, _services);
            }
        }

        private static async Task StartHealthCheckServer()
        {
            var builder = WebApplication.CreateBuilder();
            // Bot listens on 8080 internally
            builder.WebHost.UseSetting("urls", "http://0.0.0.0:8080");
            var app = builder.Build();
            app.MapGet("/bot", () => "Bot logic is online. Jit made by AI");
            await app.RunAsync();
        }
    }

    public class DeobfuscationModule : ModuleBase<SocketCommandContext>
    {
        private readonly DeobfuscationService _service;
        public DeobfuscationModule(DeobfuscationService service) => _service = service;

        [Command("d")]
        public async Task Deobfuscate()
        {
            var attachment = Context.Message.Attachments.FirstOrDefault();
            if (attachment == null)
            {
                await ReplyAsync(" Please attach a file with the `.d` command... Common sense");
                return;
            }

            var sw = Stopwatch.StartNew();
            var statusMessage = await ReplyAsync("processing..");

            try
            {
                using var http = new HttpClient();
                var rawData = await http.GetByteArrayAsync(attachment.Url);
                var scriptSource = Encoding.UTF8.GetString(rawData);

                // Start Pipeline: Obfuscated -> Bytecode -> Medal Decompiler
                var recoveredSource = await _service.ExecutePipelineAsync(scriptSource);
                sw.Stop();

                // Generate 12-character random hex filename
                string randomHexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";

                // Cleanup status and send final file
                await statusMessage.DeleteAsync();

                using var resultStream = new MemoryStream(Encoding.UTF8.GetBytes(recoveredSource));
                await Context.Channel.SendFileAsync(resultStream, randomHexName, 
                    $"Finished processing in {sw.ElapsedMilliseconds}ms\nThe bot has successfully recovered the file:");
            }
            catch (Exception ex)
            {
                await statusMessage.ModifyAsync(m => m.Content = $"❌ Error: `{ex.Message}`");
                Console.WriteLine(ex);
            }
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _medalClient = new HttpClient();

        public async Task<string> ExecutePipelineAsync(string source)
        {
            // 1. Devirtualize into Bytecode object
            var deobfuscator = new Deobfuscator();
            var chunk = deobfuscator.Deobfuscate(source);
            
            // 2. Serialize object to raw binary buffer
            using var ms = new MemoryStream();
            using (var serializer = new Serializer(ms))
            {
                serializer.Serialize(chunk);
            }
            byte[] bytecode = ms.ToArray();

            // 3. POST Binary to Medal Service on port 3000
            using var content = new ByteArrayContent(bytecode);
            var response = await _medalClient.PostAsync("http://127.0.0.1:3000/lua51/decompile", content);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync();
                throw new Exception($"Medal Service Error: {err}");
            }

            return await response.Content.ReadAsStringAsync();
        }
    }
}

