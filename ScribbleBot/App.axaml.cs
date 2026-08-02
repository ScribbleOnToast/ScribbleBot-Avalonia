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
using ScribbleBot.Models;
using ScribbleBot.Services;
using ScribbleBot.Settings;
using ScribbleBot.UI;
using ScribbleBot.UI.ViewModels;
using Serilog;
using Serilog.Events;

namespace ScribbleBot;

public partial class App : Application
{
    private IHost? _host;

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("Host services are not initialized yet.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
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

            builder.Services.AddOptions<EmbeddingSettings>()
                .BindConfiguration("EmbeddingSettings");

            // Ollama IChatClient
            builder.Services.AddSingleton<IChatClient>(sp =>
            {
                var ollamaOpts = sp.GetRequiredService<IOptions<OllamaSettings>>().Value;
                return new OllamaApiClient(ollamaOpts.Endpoint, ollamaOpts.ModelId);
            });

            // Application State & Services
            builder.Services.AddSingleton<AgentState>();
            builder.Services.AddSingleton<DatabaseService>();

            // Register HttpClient typed client correctly (DO NOT add AddSingleton after this)
            builder.Services.AddHttpClient<GoogleSearchService>();
            builder.Services.AddHttpClient("WarmupClient");

            builder.Services.AddSingleton<EmbeddingService>();
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

            // ViewModels & Views
            builder.Services.AddTransient<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>(sp => new MainWindow
            {
                DataContext = sp.GetRequiredService<MainViewModel>()
            });

            _host = builder.Build();

            // Attach Teardown Handler
            desktop.Exit += OnExit;

            // Assign Main Window first
            desktop.MainWindow = _host.Services.GetRequiredService<MainWindow>();

            // Start host and warmup asynchronously in background task without blocking UI thread
            Task.Run(async () =>
            {
                await _host.StartAsync();
                await WarmupModelAsync();
            });
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task WarmupModelAsync()
    {
        var state = Services.GetRequiredService<AgentState>();
        var chatClient = Services.GetRequiredService<IChatClient>();
        var settings = Services.GetRequiredService<IOptions<OllamaSettings>>().Value;
        var httpFactory = Services.GetRequiredService<IHttpClientFactory>();
        var logger = Services.GetRequiredService<ILogger<App>>();

        try
        {
            state.IsWarmingUp = true;
            state.StatusMessage = "Verifying LLM connection...";
            logger.LogInformation("Initiating LLM health check at {Endpoint}", settings.Endpoint);

            if (Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var parsedUri))
            {
                var client = httpFactory.CreateClient("WarmupClient");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await client.GetAsync(parsedUri.GetLeftPart(UriPartial.Authority), cts.Token);
                response.EnsureSuccessStatusCode();
            }

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
        catch (HttpRequestException hEx)
        {
            logger.LogError(hEx, "Warmup failed: Unable to reach Ollama at {Endpoint}.", settings.Endpoint);
            state.StatusMessage = "Warmup failed: Unable to reach Ollama server.";
        }
        catch (TaskCanceledException tEx)
        {
            logger.LogError(tEx, "Warmup failed: Ollama connection timed out (5s).");
            state.StatusMessage = "Ollama connection timed out after 5 seconds.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Warmup failed: Unexpected error during model warmup.");
            state.StatusMessage = "Warmup failed: An unexpected error occurred.";
        }
        finally
        {
            state.IsWarmingUp = false;
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        try
        {
            var chatClient = Services.GetService<IChatClient>();
            var settings = Services.GetService<IOptions<OllamaSettings>>()?.Value;

            if (chatClient != null && settings is { UnloadOnExit: true })
            {
                var options = new ChatOptions
                {
                    AdditionalProperties = new()
                    {
                        ["keep_alive"] = "0s"
                    }
                };

                // Pass cancellation token to avoid hanging indefinitely on exit
                chatClient.GetResponseAsync([new ChatMessage(ChatRole.User, " ")], options, shutdownCts.Token)
                          .GetAwaiter()
                          .GetResult();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Model unload on exit failed or timed out.");
        }

        if (_host is not null)
        {
            try
            {
                _host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Host shutdown encountered an error.");
            }
            finally
            {
                _host.Dispose();
            }
        }

        Log.CloseAndFlush();
    }
}