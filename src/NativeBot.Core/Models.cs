using System.Text.Json.Nodes;

namespace NativeBot.Core;

public enum BotStatus { Starting, Running, Paused, Offline, NeedsAttention, AuthorizationError, CriticalError, Stopped }
public enum BotUpdateMode { Webhook, LongPolling }
public enum NodeKind { Event, Condition, Action, Wait, Branch, Transition, End }
public enum NodeOutcome { Success, Error, Timeout, Alternative }
public enum FlowVersionStatus { Draft, Published }
public enum ExecutionStatus { Created, Running, Waiting, Scheduled, Completed, Failed, Cancelled }
public enum VariableScope { System, Event, Flow, User, Chat, Bot, Secret }
public enum VariableType { Text, Number, Boolean, DateTime, List, Object }
public enum WaitingStateStatus { Active, Completed, Expired, Cancelled }
public enum ScheduledActionStatus { Pending, Running, Completed, Failed, Cancelled }
public enum OutboxStatus { Pending, Scheduled, Sending, Sent, Retry, Failed, Cancelled }
public enum BroadcastStatus { Draft, Scheduled, Running, Paused, Completed, Failed }
public enum TelegramFailureKind { Offline, Temporary, RateLimited, Unauthorized, Forbidden, InvalidRequest, UnavailableChat, Unknown }

public sealed record Bot(
    Guid Id,
    string Name,
    string Username,
    BotStatus Status = BotStatus.Stopped,
    bool AutoStart = false,
    long KnownUsers = 0,
    DateTimeOffset? LastEventAt = null,
    BotUpdateMode UpdateMode = BotUpdateMode.Webhook);

public sealed record BotUser(
    Guid BotId,
    long TelegramId,
    string? Username,
    string? FirstName,
    string? LastName,
    string? Language,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    string Status = "Активен");

public sealed record UserEvent(DateTimeOffset At, string Kind, string Description);
public sealed record UserCard(
    BotUser User,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string?> Fields,
    IReadOnlyList<string> WaitingStates,
    string? CurrentFlow,
    IReadOnlyList<UserEvent> History);

public sealed record CustomField(Guid Id, Guid BotId, string Name, string Key, VariableType Type);
public sealed record BotTag(Guid Id, Guid BotId, string Name);
public sealed record UserFilter(
    string? Search = null,
    IReadOnlyList<Guid>? TagIds = null,
    IReadOnlyDictionary<string, string?>? FieldEquals = null,
    string? Language = null,
    string? Status = null,
    DateTimeOffset? FirstSeenAfter = null,
    DateTimeOffset? FirstSeenBefore = null,
    DateTimeOffset? ActiveAfter = null,
    DateTimeOffset? ActiveBefore = null);
public sealed record Segment(Guid Id, Guid BotId, string Name, UserFilter Filter);
public sealed record Asset(Guid Id, Guid BotId, string Name, string ContentType, long Size, string Hash, string Path);
public sealed record WaitingState(Guid Id, Guid BotId, long UserId, long ChatId, long? ThreadId, string ExpectedEvent, Guid FlowVersionId, Guid ResumeNodeId, DateTimeOffset? ExpiresAt, WaitingStateStatus Status = WaitingStateStatus.Active);
public sealed record ScheduledAction(Guid Id, Guid BotId, Guid FlowId, Guid FlowVersionId, long UserId, long ChatId, Guid ResumeNodeId, DateTimeOffset DueAt, ScheduledActionStatus Status = ScheduledActionStatus.Pending);
public sealed record Broadcast(Guid Id, Guid BotId, string Name, Guid? SegmentId, string Text, DateTimeOffset? StartsAt, BroadcastStatus Status, int Total = 0, int Sent = 0, int Failed = 0, int Skipped = 0);
public sealed record OutboxAction(Guid Id, Guid BotId, long ChatId, string Kind, string Payload, OutboxStatus Status, DateTimeOffset DueAt, int Attempts = 0, string? DeduplicationKey = null);
public sealed record DeadLetter(Guid Id, Guid BotId, long? UserId, long? ChatId, Guid? FlowId, Guid? FlowVersionId, Guid? NodeId, string Event, string Reason, int Attempts, string? TechnicalData, DateTimeOffset CreatedAt);
public sealed record AppLog(long Id, Guid? BotId, string Level, string Message, DateTimeOffset CreatedAt, long? UpdateId = null, Guid? ExecutionId = null, Guid? NodeId = null, string? Method = null, int? DurationMs = null, int? ResponseCode = null, string? TechnicalMessage = null);

public sealed record FlowDefinition(
    Guid Id,
    string Name,
    Guid EntryNodeId,
    IReadOnlyList<FlowNode> Nodes,
    IReadOnlyList<VariableDefinition>? Variables = null);

public sealed record FlowNode(
    Guid Id,
    string Name,
    NodeKind Kind,
    string Handler,
    IReadOnlyDictionary<NodeOutcome, Guid>? Next = null,
    JsonObject? Parameters = null);

public sealed record FlowVersion(
    Guid Id,
    Guid FlowId,
    int Version,
    FlowVersionStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    FlowDefinition Definition);

public sealed record VariableDefinition(string Name, VariableScope Scope, VariableType Type, bool IsReadOnly = false);

public sealed record Execution(
    Guid Id,
    Guid BotId,
    Guid FlowId,
    Guid FlowVersionId,
    long UserId,
    long ChatId,
    long OriginUpdateId,
    Guid CurrentNodeId,
    ExecutionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null);

public sealed record BlockResult(NodeOutcome Outcome, string? Message = null)
{
    public static readonly BlockResult Success = new(NodeOutcome.Success);
}
