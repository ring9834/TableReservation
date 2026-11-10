using Reservation.Domain.Common;

namespace Reservation.Api.Common;

/// <summary>
/// Maps a handler's <see cref="Result{T}"/> to HTTP (Design §9.1): success through the endpoint's callback, failure
/// to Problem Details with the status for its <see cref="ErrorKind"/> and its stable code (ADR-004).
/// </summary>
internal static class ResultToHttp
{
    public static IResult ToProblem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return ApiProblems.Result(
            ApiProblems.StatusFor(error.Kind),
            error.Code,
            error.Kind == ErrorKind.Validation ? error.Fields : null);
    }

    public static IResult ToHttp<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : ToProblem(result.Error!);
    }
}
