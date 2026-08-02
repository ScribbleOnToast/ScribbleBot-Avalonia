using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using ScribbleBot.Agents;
using ScribbleBot.Models;
using ScribbleBot.Services;
using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Extensions.Logging;
using Avalonia.Platform.Storage;

namespace ScribbleBot.UI.ViewModels
{
    /// <summary>
    /// ViewModel for the main application window, responsible for handling user interactions, 
    /// managing chat messages, and controlling sidebar navigation.
    /// </summary>
    public partial class MainViewModel : ObservableObject
    {
        private readonly SupervisorAgent _supervisorAgent;
        private readonly ILogger<MainViewModel> _logger;

        /// <summary>
        /// Service used to process and ingest uploaded files.
        /// </summary>
        public FileIngestionService _fileIngestionService;

        /// <summary>
        /// A collection of files currently attached to the user's input message.
        /// </summary>
        public ObservableCollection<IngestedFileContext> AttachedFiles { get; } = new();

        /// <summary>
        /// The global application state, providing access to properties like connectivity or busy status.
        /// </summary>
        public AgentState State { get; }

        /// <summary>
        /// Gets the current text input provided by the user.
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
        private string _userInput = string.Empty;

        /// <summary>
        /// Gets or sets the currently active view visible in the application sidebar.
        /// </summary>
        [ObservableProperty]
        private SidebarViewType _currentSidebarView = SidebarViewType.Threads;

        /// <summary>
        /// Gets or sets a value indicating whether the application is in dark mode.
        /// </summary>
        [ObservableProperty]
        private bool _isDarkMode = true;

        /// <summary>
        /// Initializes a new instance of the <see cref="MainViewModel"/> class.
        /// </summary>
        /// <param name="supervisorAgent">The central agent responsible for routing tasks.</param>
        /// <param name="state">The global application state.</param>
        /// <param name="fileIngestionService">The service for handling file uploads and parsing.</param>
        public MainViewModel(SupervisorAgent supervisorAgent, AgentState state, FileIngestionService fileIngestionService, ILogger<MainViewModel> logger)
        {
            _fileIngestionService = fileIngestionService;
            _supervisorAgent = supervisorAgent;
            State = state;
            _logger = logger;
            // Re-evaluate SendMessageCommand when IsBusy changes
            State.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AgentState.IsBusy))
                {
                    SendMessageCommand.NotifyCanExecuteChanged();
                }
            };
            // Load saved threads from SQLite on startup
            _ = _supervisorAgent.InitializeAsync();
        }

        /// <summary>
        /// Removes a specific file from the list of attachments in the current message composing state.
        /// </summary>
        /// <param name="file">The file context to remove.</param>
        [RelayCommand]
        public void RemoveAttachment(IngestedFileContext? file)
        {
            if (file != null && AttachedFiles.Contains(file))
            {
                AttachedFiles.Remove(file);
            }
        }

        /// <summary>
        /// Determines whether the user is allowed to send a message based on input content and system busy state.
        /// </summary>
        /// <returns>True if there is valid text input and the agent is not currently processing a task.</returns>
        private bool CanSendMessage()
        {
            return !string.IsNullOrWhiteSpace(UserInput) && !State.IsBusy;
        }

        /// <summary>
        /// Processes the current user input and all attached files, constructs an AI-compatible 
        /// multi-modal message, and dispatches it to the <see cref="SupervisorAgent"/>.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        [RelayCommand(CanExecute = nameof(CanSendMessage))]
        private async Task SendMessageAsync()
        {
            var contents = new List<AIContent>();

            if (!string.IsNullOrWhiteSpace(UserInput))
            {
                contents.Add(new TextContent(UserInput));
            }

            foreach (var file in AttachedFiles)
            {
                if (File.Exists(file.FilePath))
                {
                    var dataContent = new DataContent(file.Bytes, file.MimeType)
                    {
                        AdditionalProperties = new() { ["fileName"] = file.FileName }
                    };
                    contents.Add(dataContent);                                     
                }
            }            

            // Clear UI input state
            UserInput = string.Empty;
            AttachedFiles.Clear();

            // Pass rich message down to SupervisorAgent
            await _supervisorAgent.HandleUserRichMessageAsync(new ChatMessage(ChatRole.User, contents));
        }

        public async Task AttachFiles(IStorageItem[]? files)
        {
            if (files != null && files.Length > 0)
            {
                foreach (var file in files) AttachedFiles.Add(await _fileIngestionService.ProcessFileAsync(file.TryGetLocalPath()));
            }
        }

        /// <summary>
        /// Navigates the sidebar view to the Settings section.
        /// </summary>
        [RelayCommand]
        private void Settings()
        {
            CurrentSidebarView = SidebarViewType.Settings;
        }

        [RelayCommand]
        private void SearchChats()
        {
        }

        /// <summary>
        /// Navigates the sidebar view back to the Threads list.
        /// </summary>
        [RelayCommand]
        private void ShowThreads()
        {
            CurrentSidebarView = SidebarViewType.Threads;
        }

        /// <summary>
        /// Initiates the creation of a new conversation thread.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        [RelayCommand]
        private async Task CreateNewThreadAsync()
        {
            await _supervisorAgent.CreateNewThreadAsync();
        }

        /// <summary>
        /// Switches the current active conversation thread.
        /// </summary>
        /// <param name="thread">The thread entity to switch to.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [RelayCommand]
        private async Task SwitchThreadAsync(ChatThreadEntity thread)
        {
            await _supervisorAgent.SwitchThreadAsync(thread);
        }

        /// <summary>
        /// Toggles the visibility of the sidebar.
        /// </summary>
        [RelayCommand]
        private void ToggleSidebar()
        {
            State.IsSidebarOpen = !State.IsSidebarOpen;
        }

        /// <summary>
        /// Deletes the specified conversation thread.
        /// </summary>
        /// <param name="thread">The thread to delete.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        [RelayCommand]
        private async Task DeleteThread(ChatThreadEntity? thread)
        {
            if (thread != null)
            {
                await _supervisorAgent.DeleteThreadAsync(thread);
            }
        }

        /// <summary>
        /// Called when the IsDarkMode property changes to apply the new theme via the ThemeManager.
        /// </summary>
        /// <param name="value">The new dark mode state.</param>
        partial void OnIsDarkModeChanged(bool value)
        {
            ScribbleBot.UI.ThemeManager.ApplyTheme(value);
        }
    }

    /// <summary>
    /// Defines the available view modes for the application sidebar.
    /// </summary>
    public enum SidebarViewType
    {
        /// <summary> The thread history/list view. </summary>
        Threads,
        /// <summary> The application settings view. </summary>
        Settings
    }
}