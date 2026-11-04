namespace Merit.Application.Data;

// Rows of the sign-in event log (#191). Property names match the procedures' column names.

/// <summary>What logs.uspRecordSignInEvent stores: never a password, code, token, cookie or the scope key.</summary>
public sealed record SignInEventInput(string EventType, string? Step, string? Method, string? Reason, Guid? AppUserId, Guid? ActorUserId,
                                      byte[]? AttemptedNameHash, string? AttemptedNameProtected, string? ClientAddress, string? ForwardedFor,
                                      string? UserAgent, Guid? SessionScopeId, Guid? AttemptId, string? TraceId);

/// <summary>One event in an account's sign-in history.</summary>
public sealed class SignInEventRow
{
    public DateTime EventUtc { get; init; }

    public string EventType { get; init; } = string.Empty;

    public string? Step { get; init; }

    public string? Method { get; init; }

    public string? Reason { get; init; }

    public string? ClientAddress { get; init; }

    public string? ForwardedFor { get; init; }

    public string? UserAgent { get; init; }

    public Guid? SessionReference { get; init; }

    public Guid? AttemptId { get; init; }

    public string? TraceId { get; init; }

    public int? FailedSignInCount { get; init; }

    public DateTime? LockoutEndUtc { get; init; }

    public Guid? ActorUserId { get; init; }

    public string? ActorDisplayName { get; init; }
}

public sealed class SignInAddressRow
{
    public string ClientAddress { get; init; } = string.Empty;

    public int Failures { get; init; }

    public int DistinctAccounts { get; init; }

    public int DistinctUnknownNames { get; init; }

    public int Lockouts { get; init; }

    public int RateLimited { get; init; }

    public DateTime FirstUtc { get; init; }

    public DateTime LastUtc { get; init; }
}

public sealed class SignInAccountRow
{
    public Guid AppUserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public int Failures { get; init; }

    public int DistinctAddresses { get; init; }

    public int Lockouts { get; init; }

    public DateTime LastUtc { get; init; }
}

public sealed class SignInSprayRow
{
    public string ClientAddress { get; init; } = string.Empty;

    public int Targets { get; init; }

    public int Failures { get; init; }

    public DateTime FirstUtc { get; init; }

    public DateTime LastUtc { get; init; }
}

public sealed class SignInStuffingRow
{
    public Guid? AppUserId { get; init; }

    public string? UserName { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Hex of the keyed hash, for an unknown name (holders of security.investigate only).</summary>
    public string? AttemptedNameHash { get; init; }

    public int Addresses { get; init; }

    public int Failures { get; init; }

    public DateTime LastUtc { get; init; }
}

public sealed class SignInSuccessAfterFailuresRow
{
    public Guid AppUserId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public DateTime SuccessUtc { get; init; }

    public string? SuccessAddress { get; init; }

    public int Failures { get; init; }

    public int Addresses { get; init; }
}

public sealed class SignInLockoutRow
{
    public DateTime EventUtc { get; init; }

    public string EventType { get; init; } = string.Empty;

    public string? Reason { get; init; }

    public Guid? AppUserId { get; init; }

    public string? UserName { get; init; }

    public string? ClientAddress { get; init; }
}

public sealed record SignInActivity(IReadOnlyList<SignInAddressRow> ByAddress, IReadOnlyList<SignInAccountRow> ByAccount,
                                    IReadOnlyList<SignInSprayRow> Spraying, IReadOnlyList<SignInStuffingRow> Stuffing,
                                    IReadOnlyList<SignInSuccessAfterFailuresRow> SuccessAfterFailures, IReadOnlyList<SignInLockoutRow> LockoutsAndLimits);

/// <summary>Attempts at one unknown user name, grouped by its keyed hash.</summary>
public sealed class UnknownUserNameRow
{
    public string AttemptedNameHash { get; init; } = string.Empty;

    public int Attempts { get; init; }

    public int Addresses { get; init; }

    public DateTime FirstUtc { get; init; }

    public DateTime LastUtc { get; init; }

    /// <summary>One encrypted copy of the name, only when the caller asked to reveal.</summary>
    public string? AttemptedNameProtected { get; init; }

    public string? TopAddresses { get; init; }
}

public sealed class SignInAttemptRow
{
    public DateTime EventUtc { get; init; }

    public string EventType { get; init; } = string.Empty;

    public string? ClientAddress { get; init; }

    public string? ForwardedFor { get; init; }

    public string? UserAgent { get; init; }

    public Guid? AttemptId { get; init; }

    public string? TraceId { get; init; }
}

/// <summary>Why a session check failed: auth.uspGetSessionScopeState.</summary>
public sealed class SessionScopeStateRow
{
    public Guid AppUserId { get; init; }

    public DateTime ExpiresUtc { get; init; }

    public DateTime? RevokedUtc { get; init; }

    public Guid SecurityStamp { get; init; }
}
