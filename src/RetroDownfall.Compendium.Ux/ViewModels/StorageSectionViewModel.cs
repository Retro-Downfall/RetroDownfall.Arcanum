using CommunityToolkit.Mvvm.ComponentModel;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Logging;

namespace RetroDownfall.Compendium.Ux.ViewModels;

public sealed partial class StorageSectionViewModel : ObservableObject
{

    [ObservableProperty] private int _grimoireMaxMessagesPerConversationLoad;

    [ObservableProperty] private int _grimoireWorkspaceContextRetentionCount;

    [ObservableProperty] private int _grimoireDefaultLoreListLimit;

    [ObservableProperty] private int _sessionsDefaultQueryLimit;

    [ObservableProperty] private int _sessionsMaxStreamReplayEntries;

    [ObservableProperty] private int _sessionsMaxEntriesPerSession;

    [ObservableProperty] private int _sessionsMaxEntryContentBytes;

    [ObservableProperty] private int _eventBusChannelCapacity;

    [ObservableProperty] private int _eventBusHeartbeatSeconds;

    [ObservableProperty] private int _eventBusMaxSseConnections;

    [ObservableProperty] private int _eventBusMaxSseConnectionsPerType;

    [ObservableProperty] private int _logsRingBufferCapacity;

    [ObservableProperty] private LogLevel _logsMinLevelInBuffer;

    [ObservableProperty] private long _workspacesMaxFileReadSizeBytes;

    [ObservableProperty] private int _workspacesListDirectoryMaxDepth;

    private GrimoireSettings _grimoireSnapshot = new();

    private SessionSettings _sessionsSnapshot = new();

    private EventBusSettings _eventBusSnapshot = new();

    private LogSettings _logsSnapshot = new();

    private WorkspaceSettings _workspacesSnapshot = new();

    public void LoadFrom(
        GrimoireSettings grimoire,
        SessionSettings sessions,
        EventBusSettings eventBus,
        LogSettings logs,
        WorkspaceSettings workspaces)
    {

        _grimoireSnapshot = grimoire;

        _sessionsSnapshot = sessions;

        _eventBusSnapshot = eventBus;

        _logsSnapshot = logs;

        _workspacesSnapshot = workspaces;

        GrimoireMaxMessagesPerConversationLoad = grimoire.MaxMessagesPerConversationLoad;

        GrimoireWorkspaceContextRetentionCount = grimoire.WorkspaceContextRetentionCount;

        GrimoireDefaultLoreListLimit = grimoire.DefaultLoreListLimit;

        SessionsDefaultQueryLimit = sessions.DefaultQueryLimit ?? 100;

        SessionsMaxStreamReplayEntries = sessions.MaxStreamReplayEntries;

        SessionsMaxEntriesPerSession = sessions.MaxEntriesPerSession;

        SessionsMaxEntryContentBytes = sessions.MaxEntryContentBytes;

        EventBusChannelCapacity = eventBus.ChannelCapacity;

        EventBusHeartbeatSeconds = eventBus.HeartbeatSeconds;

        EventBusMaxSseConnections = eventBus.MaxSseConnections;

        EventBusMaxSseConnectionsPerType = eventBus.MaxSseConnectionsPerType;

        LogsRingBufferCapacity = logs.RingBufferCapacity;

        LogsMinLevelInBuffer = logs.MinLevelInBuffer;

        WorkspacesMaxFileReadSizeBytes = workspaces.MaxFileReadSizeBytes;

        WorkspacesListDirectoryMaxDepth = workspaces.ListDirectoryMaxDepth;

    }

    public GrimoireSettings BuildGrimoire() => _grimoireSnapshot with
    {

        MaxMessagesPerConversationLoad = GrimoireMaxMessagesPerConversationLoad,

        WorkspaceContextRetentionCount = GrimoireWorkspaceContextRetentionCount,

        DefaultLoreListLimit = GrimoireDefaultLoreListLimit,

    };

    public SessionSettings BuildSessions() => _sessionsSnapshot with
    {

        DefaultQueryLimit = SessionsDefaultQueryLimit,

        MaxStreamReplayEntries = SessionsMaxStreamReplayEntries,

        MaxEntriesPerSession = SessionsMaxEntriesPerSession,

        MaxEntryContentBytes = SessionsMaxEntryContentBytes,

    };

    public EventBusSettings BuildEventBus() => _eventBusSnapshot with
    {

        ChannelCapacity = EventBusChannelCapacity,

        HeartbeatSeconds = EventBusHeartbeatSeconds,

        MaxSseConnections = EventBusMaxSseConnections,

        MaxSseConnectionsPerType = EventBusMaxSseConnectionsPerType,

    };

    public LogSettings BuildLogs() => _logsSnapshot with
    {

        RingBufferCapacity = LogsRingBufferCapacity,

        MinLevelInBuffer = LogsMinLevelInBuffer,

    };

    public WorkspaceSettings BuildWorkspaces() => _workspacesSnapshot with
    {

        MaxFileReadSizeBytes = WorkspacesMaxFileReadSizeBytes,

        ListDirectoryMaxDepth = WorkspacesListDirectoryMaxDepth,

    };

}
