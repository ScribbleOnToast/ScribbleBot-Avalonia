using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using ScribbleBot.Agents;
using ScribbleBot.Agents.Tools;
using ScribbleBot.Services;
using ScribbleBot.Settings;
using ScribbleBot.ViewModels;
using Serilog;
using Serilog.Events;

namespace ScribbleBot;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 1. Configure Serilog
            string logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScribbleBot",
                "logs");

            string logFilePath = Path.Combine(logFolder, "scribblebot-.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .WriteTo.File(
                    path: logFilePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"
                )
                .CreateLogger();

            // 2. Build Generic Host & Register Services
            var builder = Host.CreateApplicationBuilder(desktop.Args);

            builder.Services.AddSerilog();
            builder.Services.AddOptions<OllamaSettings>()
                .BindConfiguration("OllamaSettings")
                .ValidateOnStart();

            builder.Services.AddOptions<GoogleSearchSettings>()
                .BindConfiguration("GoogleSearchSettings");

            builder.Services.AddOptions<QdrantSettings>()
                .BindConfiguration("QdrantSettings");

            // Ollama IChatClient pointing to Gemma 4
            builder.Services.AddSingleton<IChatClient>(sp =>
            {
                var ollamaOpts = sp.GetRequiredService<IOptions<OllamaSettings>>().Value;
                return new OllamaApiClient(ollamaOpts.Endpoint, ollamaOpts.ModelId);
            });

            // Application State & Infrastructure Services
            builder.Services.AddSingleton<AgentState>();
            builder.Services.AddSingleton<DatabaseService>();
            builder.Services.AddHttpClient<GoogleSearchService>();
            builder.Services.AddSingleton<GoogleSearchService>();
            builder.Services.AddSingleton<CodeIndexerService>();
            builder.Services.AddSingleton<CodeQueryService>();
            builder.Services.AddSingleton<SupervisorAgent>();
            builder.Services.AddSingleton<IIntentRouter, IntentRouter>();
            builder.Services.AddSingleton<ToolDispatcher>();
            builder.Services.AddTransient<ContextCompactor>();
            builder.Services.AddSingleton<FileIngestionService>();

            // Register Agents
            builder.Services.AddSingleton<IWorkerAgent, ChatWorker>();
            builder.Services.AddSingleton<IWorkerAgent, CodeWorker>();

            // ViewModel & MainWindow
            builder.Services.AddTransient<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>(sp => new MainWindow
            {
                DataContext = sp.GetRequiredService<MainViewModel>()
            });

            _host = builder.Build();
            await _host.StartAsync();

            // 3. Attach Shutdown Handler (Replaces WPF OnExit)
            desktop.Exit += OnExit;

            // 4. Assign Main Window
            desktop.MainWindow = _host.Services.GetRequiredService<MainWindow>();

            // 5. Trigger Async Model Warmup
            _ = WarmupModelAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task WarmupModelAsync()
    {
        var state = Services.GetRequiredService<AgentState>();
        var chatClient = Services.GetRequiredService<IChatClient>();
        var settings = Services.GetRequiredService<IOptions<OllamaSettings>>().Value;
        var httpClient = Services.GetRequiredService<HttpClient>();
        var logger = Services.GetRequiredService<ILogger<App>>();

        try
        {
            state.IsWarmingUp = true;
            state.StatusMessage = "Verifying LLM connection...";
            logger.LogInformation("Initiating LLM health check at {Endpoint}", settings.Endpoint);

            var baseUri = new Uri(settings.Endpoint).GetLeftPart(UriPartial.Authority);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await httpClient.GetAsync(baseUri, cts.Token);
            response.EnsureSuccessStatusCode();
            logger.LogInformation("LLM host reachable. Triggering model warmup for '{ModelId}'...", settings.ModelId);

            var options = new ChatOptions
            {
                AdditionalProperties = new()
                {
                    ["keep_alive"] = settings.KeepAlive ? "-1m" : "5m"
                }
            };

            await chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, " ")], options);
            logger.LogInformation("Model '{ModelId}' warmed up successfully.", settings.ModelId);
            state.StatusMessage = "Ready";
        }
        catch (HttpRequestException hEX)
        {
            logger.LogError(hEX, "Warmup failed: Unable to reach Ollama {Endpoint}.", settings.Endpoint);
            state.StatusMessage = $"Warmup failed: Unable to reach Ollama server.";
        }
        catch (TaskCanceledException tEX)
        {
            logger.LogError(tEX, "Warmup failed: Ollama connection timed out at endpoint {Endpoint} (5s).", settings.Endpoint);
            state.StatusMessage = "Ollama connection timed out after 5 seconds.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Warmup failed: An unexpected error occurred during model warmup.");
            state.StatusMessage = $"Warmup failed: An unexpected error occurred.";
        }
        finally
        {
            state.IsWarmingUp = false;
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            var chatClient = Services.GetService<IChatClient>();
            var settings = Services.GetRequiredService<IOptions<OllamaSettings>>().Value;

            if (chatClient != null && settings.UnloadOnExit)
            {
                var options = new ChatOptions
                {
                    AdditionalProperties = new()
                    {
                        ["keep_alive"] = "0s"
                    }
                };
                chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, " ")], options).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }

        if (_host is not null)
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        Log.CloseAndFlush();
    }
}