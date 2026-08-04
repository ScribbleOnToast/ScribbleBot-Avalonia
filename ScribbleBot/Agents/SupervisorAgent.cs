using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ScribbleBot.Models;
using ScribbleBot.Serializers;
using ScribbleBot.Services;
using System.Text;

namespace ScribbleBot.Agents
{
    /// <summary>
    /// Coordinates high-level chat workflow: loads threads, routes user messages
    /// to the ChatWorker, updates persistent storage, and manages in-memory
    /// conversation state and summaries.
    /// </summary>
    public class SupervisorAgent
    {
        private readonly Dictionary<string, IWorkerAgent> _agents;
        private readonly DatabaseService _dbService;
        private readonly AgentState _state;
        private readonly ContextCompactor _compactor;
        private readonly IIntentRouter _router;
        private readonly ILogger<SupervisorAgent> _logger;

        /// <summary>
        /// Creates a new SupervisorAgent.
        /// </summary>
        /// <param name="chatWorker">Worker that handles interactions with the chatbot/LLM.</param>
        /// <param name="dbService">Service for persisting and retrieving threads and messages.</param>
        /// <param name="state">Shared application state for threads, messages and UI flags.</param>
        /// <param name="compactor">Component responsible for segmenting and summarizing history.</param>
        public SupervisorAgent(IEnumerable<IWorkerAgent> agents, DatabaseService dbService, AgentState state, ContextCompactor compactor, IIntentRouter router, ILogger<SupervisorAgent> logger)
        {
            _agents = agents.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
            _dbService = dbService;
            _state = state;
            _compactor = compactor;
            _router = router;
            _logger = logger;
        }

        /// <summary>
        /// Loads saved threads from the database into state and selects an initial
        /// thread (either the first existing thread or a newly created one).
        /// </summary>
        public async Task InitializeAsync()
        {
            _logger.LogInformation("Initializing Supervisor Agent");
            await _dbService.InitializeAsync();
            if (!_dbService.IsAvailable)
            {
                _logger.LogCritical("Database is unavailable. Halting initialization.");

                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    // Create a quick native window for the error dialog
                    var errorDialog = new Window
                    {
                        Title = "Critical Error",
                        Width = 400,
                        Height = 160,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        CanResize = false,
                        Content = new StackPanel
                        {
                            Margin = new Thickness(20),
                            Spacing = 15,
                            Children =
                    {
                        new TextBlock
                        {
                            Text = "ScribbleBot cannot start because the local database is unavailable.",
                            TextWrapping = TextWrapping.Wrap
                        },
                        new Button
                        {
                            Content = "OK",
                            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                            Width = 75
                        }
                    }
                        }
                    };

                    // Wire up the OK button to close
                    if (errorDialog.Content is StackPanel panel && panel.Children[1] is Button okButton)
                    {
                        okButton.Click += (_, _) => errorDialog.Close();
                    }

                    // Show dialog modally over main window if it exists, or standalone
                    if (desktop.MainWindow != null)
                    {
                        await errorDialog.ShowDialog(desktop.MainWindow);
                    }
                    else
                    {
                        errorDialog.Show();
                    }

                    desktop.Shutdown(1);
                    return;
                }
            }

            var savedThreads = await _dbService.GetAllThreadsAsync();
            _state.Threads.Clear();
            for(var i = 0; i<savedThreads.Count;i++)
            {
                _state.Threads.Add(savedThreads[i]);
            }

            if (_state.Threads.Any())
            {
                await SwitchThreadAsync(_state.Threads.First());
            }
            else
            {
                await CreateNewThreadAsync();
            }
        }

        /// <summary>
        /// Resolves the best worker agent for the given user request.
        /// Defaults to "ChatWorker" if no specialized agent matches.
        /// </summary>
        private async Task<IWorkerAgent> PickAgentForMessageAsync(ChatMessage message)
        {
            _logger.LogInformation("Selecting agent for message: {Message}", message.Text);
            var descriptors = _agents.Values.Select(a => new AgentDescriptor(a.Name, a.Description));
            string targetAgentName = await _router.DetermineBestAgentAsync(message, descriptors);

            if (_agents.TryGetValue(targetAgentName, out var agent))
            {
                _logger.LogInformation("Selected agent: {AgentName}", agent.Name);
                return agent;
            }
            _logger.LogWarning("No matching agent found for message. Defaulting to ChatWorker.");
            return _agents["ChatWorker"];
        }



        /// <summary>
        /// Creates and persists a new chat thread, inserts it into the state list,
        /// and switches the UI context to the new thread.
        /// </summary>
        public async Task CreateNewThreadAsync()
        {
            _logger.LogInformation("Creating new chat thread");
            var newThread = new ChatThreadEntity
            {
                Id = Guid.NewGuid().ToString(),
                Title = "New Conversation",
                CreatedAt = DateTime.Now,
                LastUpdatedAt = DateTime.Now
            };

            await _dbService.SaveThreadAsync(newThread);
            _state.Threads.Insert(0, newThread);
            await SwitchThreadAsync(newThread);
        }

        /// <summary>
        /// Switches the active thread context to the provided thread. Clears
        /// in-memory message lists and loads the full message history for UI
        /// rendering. Also constructs the ChatMessage list used by the LLM.
        /// </summary>
        /// <param name="thread">Thread to switch to. If null, the call is ignored.</param>
        public async Task SwitchThreadAsync(ChatThreadEntity thread)
        {
            _logger.LogInformation("Switching to thread: {ThreadTitle}", thread?.Title ?? "null");

            _state.CurrentThread = thread;
            _state.Messages.Clear();
            _state.ActiveMessages.Clear();

            var messages = await _dbService.GetMessagesForThreadAsync(thread.Id);
            foreach (var message in messages)
            {
                _state.ActiveMessages.Add(new ChatMessage
                {
                    Role = message.Role.ToLower() switch 
                    {
                        "assistant" => ChatRole.Assistant,
                        "system" => ChatRole.System,
                        _ => ChatRole.User,
                    },
                    CreatedAt = message.Timestamp,
                    Contents = ChatMessageSerializer.DeserializeContents(message.RichContentJson)
                });
            }

            _logger.LogInformation("Loaded {MessageCount} messages for thread.", messages.Count);
            var (activeWindow, _) = _compactor.SegmentHistory(_state.ActiveMessages);

            _logger.LogInformation("Hydrating thread with {ActiveCount} active messages.", activeWindow.Count);
            foreach (var activeMsg in activeWindow)
            {
                _state.Messages.Add(activeMsg);
            }
        }

        /// <summary>
        /// Handles a user-submitted message: updates UI state, persists the
        /// message, sends the composed context to the ChatWorker, persists the
        /// assistant response, and triggers background checkpointing.
        /// </summary>
        /// <param name="userMessage">Raw user message text to process.</param>
        public async Task HandleUserRichMessageAsync(ChatMessage userMessage)
        {
            if (!userMessage.Contents.Any() || _state.CurrentThread == null || _state.IsBusy) return;
            _state.IsBusy = true;

            _state.Messages.Add(userMessage);
            _state.ActiveMessages.Add(userMessage);

            await _dbService.AddMessageAsync(_state.CurrentThread.Id,
                new ChatMessageEntity
                {
                    ThreadId = _state.CurrentThread.Id,
                    Role = "user",
                    Timestamp = DateTime.Now,
                    RichContentJson = ChatMessageSerializer.SerializeContents(userMessage.Contents)
                });

            if (_state.ActiveMessages.Count == 1 && _state.CurrentThread.Title == "New Conversation")
            {
                string titlePrompt = !string.IsNullOrWhiteSpace(userMessage.Text)
                    ? userMessage.Text
                    : "Document Conversation";

                _state.CurrentThread.Title = titlePrompt.Length > 25 ? titlePrompt[..25] + "..." : titlePrompt;
                _state.CurrentThread.LastUpdatedAt = DateTime.Now;
                await _dbService.SaveThreadAsync(_state.CurrentThread);
            }

            try
            {
                _state.StatusMessage = "Supervisor: Routing request...";

                var agentPayloadContents = new List<AIContent>();
                var textPromptBuilder = new StringBuilder();

                foreach (var content in userMessage.Contents)
                {
                    if (content is TextContent textContent)
                    {
                        textPromptBuilder.AppendLine(textContent.Text);
                    }
                    else if (content is DataContent dataContent)
                    {
                        if (dataContent.MediaType.StartsWith("image/"))
                        {
                            // Pass images directly
                            agentPayloadContents.Add(dataContent);
                        }
                        else
                        {
                            // Unpack extracted PDF / Text File bytes into text prompt ONLY for the LLM
                            string fileName = dataContent.AdditionalProperties?.TryGetValue("fileName", out var name) == true
                                ? name?.ToString() ?? "file.txt"
                                : "file.txt";

                            string extractedText = Encoding.UTF8.GetString(dataContent.Data.ToArray());

                            textPromptBuilder.AppendLine($"\n\n[ATTACHMENT: {fileName}]");
                            textPromptBuilder.AppendLine("```");
                            textPromptBuilder.AppendLine(extractedText);
                            textPromptBuilder.AppendLine("```");
                            textPromptBuilder.AppendLine("[/ATTACHMENT]");
                        }
                    }
                }
                string fullPromptText = textPromptBuilder.ToString().Trim();
                agentPayloadContents.Insert(0, new TextContent(fullPromptText));

                var worker = await PickAgentForMessageAsync(userMessage);
                _state.StatusMessage = $"{worker.Name}: Processing...";

                var transientHistory = new List<ChatMessage>(_state.Messages);
                transientHistory[transientHistory.Count - 1] = new ChatMessage(ChatRole.User, agentPayloadContents);

                string summary = _state.CurrentThread.SystemSummary ?? string.Empty;

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var botResponse = await worker.ProcessAsync(transientHistory, summary);
                stopwatch.Stop();
                _logger.LogInformation("Worker {WorkerName} completed request in {ElapsedMs}ms", worker.Name, stopwatch.ElapsedMilliseconds);

                var assistantResponse = botResponse.Messages.Last();
                _state.Messages.Add(assistantResponse);
                _state.ActiveMessages.Add(assistantResponse);

                await _dbService.AddMessageAsync(_state.CurrentThread.Id,
                    new ChatMessageEntity
                    {
                        ThreadId = _state.CurrentThread.Id,
                        Role = "assistant",
                        Timestamp = DateTime.Now,
                        RichContentJson = ChatMessageSerializer.SerializeContents(botResponse.Messages.Last().Contents)
                    });

                //await CheckpointMemoryAsync(); - Need to rework this because we're not using the model anymore, we're using actual SDK classes
                _state.StatusMessage = "Ready";

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while handling user message in thread {ThreadId}", _state.CurrentThread.Id);
                //Remove the bad message 
                if (_state.Messages.Last().Role == ChatRole.User){
                    _state.Messages.RemoveAt(_state.Messages.Count - 1);
                }
                if (_state.ActiveMessages.Last().Role == ChatRole.User) {
                    _state.ActiveMessages.RemoveAt(_state.ActiveMessages.Count - 1);
                }
                _state.StatusMessage = "Error occurred";
            }
            finally
            {
                _state.IsBusy = false;
            }
        }

        /// <summary>
        /// Performs background checkpointing: if the conversation exceeds the
        /// active window budget, compacts overflow into the thread's
        /// SystemSummary, persists the summary, and trims the in-memory
        /// Messages list to the active window.
        /// </summary>
        private async Task CheckpointMemoryAsync()
        {
            if (_state.CurrentThread == null) return;

            var (activeWindow, overflow) = _compactor.SegmentHistory(_state.Messages);

            if (overflow.Any())
            {
                _logger.LogInformation("Checkpointing memory: {OverflowCount} messages overflowed, updating system summary.", overflow.Count);
                var updatedSummary = await _compactor.UpdateSummaryAsync(_state.CurrentThread.SystemSummary ?? string.Empty, overflow);

                _state.CurrentThread.SystemSummary = updatedSummary;
                await _dbService.UpdateThreadSummaryAsync(_state.CurrentThread.Id, updatedSummary);

                _state.Messages.Clear();
                foreach (var msg in activeWindow)
                {
                    _state.Messages.Add(msg);
                }
            }
        }

        public async Task DeleteThreadAsync(ChatThreadEntity threadToDelete)
        {
            _logger.LogInformation("Deleting thread: {ThreadTitle}", threadToDelete?.Title ?? "null");
            if (threadToDelete == null) return;

            int deletedIndex = _state.Threads.IndexOf(threadToDelete);
            if (deletedIndex == -1) return;

            bool isDeletingCurrent = _state.CurrentThread?.Id == threadToDelete.Id;

            await _dbService.DeleteThreadAsync(threadToDelete.Id);
            _state.Threads.Remove(threadToDelete);

            if (isDeletingCurrent)
            {
                if (_state.Threads.Count == 0)
                {
                    // List is completely empty, spin up a fresh one
                    await CreateNewThreadAsync();
                }
                else
                {
                    // Select thread directly "above" it (index - 1). 
                    // If the deleted item was at index 0, take the new index 0.
                    int nextIndex = Math.Max(0, deletedIndex - 1);
                    await SwitchThreadAsync(_state.Threads[nextIndex]);
                }
            }
        }
    }
}