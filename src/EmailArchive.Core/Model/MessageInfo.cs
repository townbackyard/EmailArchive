namespace EmailArchive.Core.Model;

/// <summary>Headers of a message as listed from Gmail All Mail.</summary>
public sealed record SourceMessageInfo(
    uint Uid,
    string? MessageId,
    DateTimeOffset InternalDate,
    DateTimeOffset? HeaderDate,
    string From,
    string Subject,
    long Size,
    bool IsSent,
    bool IsChat,
    bool IsSeen,
    bool IsFlagged);

/// <summary>Headers of a message as listed from a Microsoft 365 target folder.</summary>
public sealed record TargetMessageInfo(
    TargetFolder Folder,
    uint Uid,
    string? MessageId,
    DateTimeOffset? HeaderDate,
    string From,
    string Subject,
    long Size);
