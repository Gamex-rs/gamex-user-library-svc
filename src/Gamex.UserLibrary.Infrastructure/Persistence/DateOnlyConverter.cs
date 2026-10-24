using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Gamex.UserLibrary.Infrastructure.Persistence;

/// <summary>MySql.EntityFrameworkCore reads DATE columns as <see cref="DateTime"/> and cannot cast them to <see cref="DateOnly"/>.</summary>
internal sealed class DateOnlyConverter() : ValueConverter<DateOnly, DateTime>(
    v => v.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified),
    v => DateOnly.FromDateTime(v));
