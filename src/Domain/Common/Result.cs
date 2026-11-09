namespace Reservation.Domain.Common;

/// <summary>Error kinds; the API maps each to an HTTP status (ADR-004, Design §9.1).</summary>
public enum ErrorKind
{
    /// <summary>400 <c>validation_failed</c>.</summary>
    Validation,

    /// <summary>404 <c>not_found</c>.</summary>
    NotFound,

    /// <summary>409: <c>slot_taken</c>, <c>version_conflict</c>, <c>hold_expired</c>, <c>invalid_transition</c>.</summary>
    Conflict,

    /// <summary>422 <c>rule_broken</c>, with a more specific code where useful.</summary>
    Rule,

    /// <summary>403 <c>forbidden</c>.</summary>
    Forbidden,

    /// <summary>A dependency outside the system failed in an expected way.</summary>
    External,
}

/// <summary>Stable error codes the front end switches on (ADR-004).</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Forbidden = "forbidden";
    public const string SlotTaken = "slot_taken";
    public const string VersionConflict = "version_conflict";
    public const string HoldExpired = "hold_expired";
    public const string InvalidTransition = "invalid_transition";
    public const string RuleBroken = "rule_broken";

    // More specific rule_broken codes (422).
    public const string PartyTooLarge = "party_too_large";
    public const string OutsideServiceHours = "outside_service_hours";
    public const string DepositAmountMismatch = "deposit_amount_mismatch";
    public const string SecondBill = "second_bill";
    public const string OutsideLateMatchWindow = "outside_late_match_window";
    public const string ReminderNotAllowed = "reminder_not_allowed";
}

/// <summary>An expected failure: stable code, translation key, kind and optional field errors.</summary>
public sealed record Error(
    string Code,
    string MessageKey,
    ErrorKind Kind,
    IReadOnlyDictionary<string, string[]>? Fields = null)
{
    public static Error InvalidTransition { get; } = Conflict(ErrorCodes.InvalidTransition);

    /// <summary>A field-level validation failure: code <c>validation_failed</c>, the specific rule in <see cref="Fields"/>.</summary>
    public static Error Validation(string field, string ruleCode) =>
        new(ErrorCodes.ValidationFailed, Key(ruleCode), ErrorKind.Validation,
            new Dictionary<string, string[]> { [field] = [ruleCode] });

    public static Error NotFound(string code = ErrorCodes.NotFound) => new(code, Key(code), ErrorKind.NotFound);

    public static Error Conflict(string code) => new(code, Key(code), ErrorKind.Conflict);

    public static Error Rule(string code) => new(code, Key(code), ErrorKind.Rule);

    public static Error Forbidden(string code = ErrorCodes.Forbidden) => new(code, Key(code), ErrorKind.Forbidden);

    public static Error External(string code) => new(code, Key(code), ErrorKind.External);

    private static string Key(string code) => $"errors.{code}";
}

/// <summary>Success, or failure with an <see cref="Common.Error"/> (ADR-004).</summary>
public class Result
{
    private static readonly Result _success = new(null);

    protected Result(Error? error) => Error = error;

    public Error? Error { get; }

    public bool IsSuccess => Error is null;

    public bool IsFailure => !IsSuccess;

    public static Result Success() => _success;

    public static Result Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(error);
    }

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<T> : Result
{
    private readonly T? _value;

    private Result(T value)
        : base(null) => _value = value;

    private Result(Error error)
        : base(error)
    {
    }

    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result failed with '{Error!.Code}'; it has no value.");

    public static Result<T> Success(T value) => new(value);

    public static new Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(error);
    }

    public static implicit operator Result<T>(T value) => Success(value);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
