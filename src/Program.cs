using Discord;
using Discord.WebSocket;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Text;
using System.Diagnostics;
using MoonsecDeobfuscator.Deobfuscation;
using MoonsecDeobfuscator.Deobfuscation.Bytecode;

namespace MoonsecBot
{
    public class Program
    {
        private DiscordSocketClient _client;
        private CommandService _commands;
        private IServiceProvider _services;

        public static async Task Main(string[] args) => await new Program().RunAsync();

        public async Task RunAsync()
        {
            // Intent setup: Required to read the ".d" text from messages
            var config = new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
                AlwaysDownloadUsers = true
            };

            _client = new DiscordSocketClient(config);
            _commands = new CommandService();

            _services = new ServiceCollection()
                .AddSingleton(_client)
                .AddSingleton(_commands)
                .AddSingleton<DeobfuscationService>()
                .BuildServiceProvider();

            _client.Log += (msg) => { Console.WriteLine(msg); return Task.CompletedTask; };
            _client.MessageReceived += HandleCommandAsync;

            await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);

            // Fetch your token from Render Environment Variables
            var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
            await _client.LoginAsync(TokenType.Bot, token);
            await _client.StartAsync();

            await Task.Delay(-1);
        }

        private async Task HandleCommandAsync(SocketMessage arg)
        {
            if (arg is not SocketUserMessage message || message.Author.IsBot) return;

            int argPos = 0;
            // Triggers on .d or .D
            if (message.HasStringPrefix(".d", ref argPos, StringComparison.OrdinalIgnoreCase))
            {
                var context = new SocketCommandContext(_client, message);
                await _commands.ExecuteAsync(context, argPos, _services);
            }
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
            if (attachment == null) return;

            var sw = Stopwatch.StartNew();
            var statusMessage = await ReplyAsync("processing..");

            try
            {
                using var http = new HttpClient();
                var rawData = await http.GetByteArrayAsync(attachment.Url);
                var scriptSource = Encoding.UTF8.GetString(rawData);

                // Start Pipeline: Obfuscated -> Bytecode -> Medal Public URL
                var recoveredSource = await _service.ExecutePipelineAsync(scriptSource);
                sw.Stop();

                // Generate random 12-char hex filename (e.g. 7f3e1a2b5c6d.lua)
                string hexName = Guid.NewGuid().ToString("N").Substring(0, 12) + ".lua";

                // Delete 'processing..' and send final file
                await statusMessage.DeleteAsync();

                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(recoveredSource));
                await Context.Channel.SendFileAsync(ms, hexName, 
                    $"Finished processing in {sw.ElapsedMilliseconds}ms\n" +
                    "The bot has successfully recovered the file:");
            }
            catch (Exception ex)
            {
                // If the URL fails, it will show the error here
                await statusMessage.ModifyAsync(m => m.Content = $"❌ Error: `{ex.Message}`");
            }
        }
    }

    public class DeobfuscationService
    {
        private static readonly HttpClient _medalClient = new HttpClient();
        // Updated to use your public Render URL
        private const string MedalUrl = "https://medal-1.onrender.com/lua51/decompile";

        public async Task<string> ExecutePipelineAsync(string source)
        {
            // 1. Devirtualize
            var deobfuscator = new Deobfuscator();
            var chunk = deobfuscator.Deobfuscate(source);
            
            // 2. Serialize to Bytecode
            using var ms = new MemoryStream();
            using (var serializer = new Serializer(ms))
            {
                serializer.Serialize(chunk);
            }

            // 3. POST to Public URL
            using var content = new ByteArrayContent(ms.ToArray());
            var response = await _medalClient.PostAsync(MedalUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                var errorInfo = await response.Content.ReadAsStringAsync();
                throw new Exception($"Medal API error: {response.StatusCode} - {errorInfo}");
            }

            return await response.Content.ReadAsStringAsync();
        }
    }
}
