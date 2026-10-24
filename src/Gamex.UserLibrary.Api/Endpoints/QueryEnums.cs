namespace Gamex.UserLibrary.Api.Endpoints;

/// <summary>
/// Query-string enums in the API's snake_case form ("want_to_buy", "like_new"). Minimal API binding uses
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>, which does not know these names; the JSON converter
/// only covers request bodies.
/// </summary>
internal static class QueryEnums
{
    /// <returns><c>false</c> if <paramref name="value"/> is set but not a known value.</returns>
    public static bool TryParse<TEnum>(string? value, out TEnum? result)
        where TEnum : struct, Enum
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var compact = value.Trim().Replace("_", "", StringComparison.Ordinal);
        if (!int.TryParse(compact, out _) && Enum.TryParse<TEnum>(compact, ignoreCase: true, out var parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    public static string Allowed<TEnum>()
        where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>().Select(n => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(n)));
}
