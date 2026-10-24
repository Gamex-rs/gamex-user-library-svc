using Gamex.UserLibrary.Application.Common;

namespace Gamex.UserLibrary.Api.Endpoints;

/// <summary>RFC 7807 responses with Gamex error type URIs.</summary>
internal static class Problems
{
    private const string TypeBase = "https://gamex.rs/errors/";

    public static IResult Validation(IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, type: TypeBase + "validation", title: "One or more fields are invalid.");

    public static IResult Validation(string field, string message) =>
        Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static IResult From<T>(OperationResult<T> result, Func<T, IResult> onOk) => result.Status switch
    {
        OperationStatus.Ok => onOk(result.Value!),
        OperationStatus.Invalid => Validation(result.Errors!.ToDictionary()),
        OperationStatus.NotFound => Problem(StatusCodes.Status404NotFound, "not-found", "Not found", result),
        OperationStatus.Conflict => Conflict(result),
        OperationStatus.Unauthorized => Problem(StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", result),
        OperationStatus.Forbidden => Problem(StatusCodes.Status403Forbidden, "forbidden", "Access denied", result),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null),
    };

    public static IResult From<T>(OperationResult<T> result) => From(result, value => Results.Ok(value));

    private static IResult Conflict<T>(OperationResult<T> result)
    {
        // A conflict may carry its own problem type (e.g. game-not-synced with Retry-After).
        var extensions = result.Extensions?.ToDictionary() ?? [];
        var type = extensions.Remove("type", out var custom) ? (string)custom! : "conflict";
        var problem = Results.Problem(
            type: TypeBase + type,
            title: "Conflict",
            detail: result.Detail,
            statusCode: StatusCodes.Status409Conflict,
            extensions: extensions);

        return extensions.TryGetValue("retryAfter", out var seconds) ? new RetryAfterResult(problem, Convert.ToInt32(seconds, System.Globalization.CultureInfo.InvariantCulture)) : problem;
    }

    private static IResult Problem<T>(int status, string type, string title, OperationResult<T> result) =>
        Results.Problem(type: TypeBase + type, title: title, detail: result.Detail, statusCode: status);

    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}
