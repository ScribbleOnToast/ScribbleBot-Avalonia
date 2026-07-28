using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.AI;
using ScribbleBot.Models;
using System.Collections.ObjectModel;

namespace ScribbleBot.Agents;

/// <summary>
/// Represents the runtime state of the AI agent, including conversation history, 
/// active threads, and operational status markers.
/// </summary>
public partial class AgentState : ObservableObject
{
    /// <summary>
    /// The raw input currently being processed by the agent.
    /// </summary>
    [ObservableProperty]
    private string _currentInput = string.Empty;

    /// <summary>
    /// Indicates whether the active agent is currently executing a task or processing a request.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy = false;

    /// <summary>
    /// A human-readable description of the agent's current status or activity.
    /// </summary>
    [ObservableProperty]
    private string _statusMessage = "Ready";

    /// <summary>
    /// Indicates whether the system is currently initializing models or resources.
    /// </summary>
    [ObservableProperty]
    private bool _isWarmingUp;

    /// <summary>
    /// The collection of all available conversation threads.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ChatThreadEntity> _threads = [];

    /// <summary>
    /// The currently active and selected conversation thread.
    /// </summary>
    [ObservableProperty]
    private ChatThreadEntity? _currentThread;

    /// <summary>
    /// The collection of messages associated with the current thread's context aware history.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ChatMessage> _activeMessages = [];

    /// <summary>
    /// Indicates whether the application's navigation sidebar is expanded.
    /// </summary>
    [ObservableProperty]
    private bool _isSidebarOpen;

    /// <summary>
    /// A persistent collection of all messages processed across the current thread's complete history.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ChatMessage> _messages = [];
}