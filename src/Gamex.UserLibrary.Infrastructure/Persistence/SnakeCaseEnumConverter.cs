using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Gamex.UserLibrary.Infrastructure.Persistence;

/// <summary>Stores enums as snake_case strings ("like_new", "want_to_buy"), matching the spec's ENUM values and the API.</summary>
internal sealed class SnakeCaseEnumConverter<TEnum>() : ValueConverter<TEnum, string>(
    v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()),
    v => Enum.Parse<TEnum>(v.Replace("_", "", StringComparison.Ordinal), ignoreCase: true))
    where TEnum : struct, Enum;
