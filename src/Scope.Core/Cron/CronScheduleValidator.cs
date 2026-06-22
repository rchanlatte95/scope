namespace Scope.Core.Cron;

using System.Globalization;

/// <summary>
/// Validates the MVP cron schedule grammar: five UTC fields plus @hourly/@daily sugar.
/// </summary>
public static class CronScheduleValidator
{
    private static readonly (int Min, int Max)[] FieldRanges =
    [
        (0, 59),
        (0, 23),
        (1, 31),
        (1, 12),
        (0, 7)
    ];

    public static CronResult<string> Validate(string? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule))
            return CronResult.Fail<string>("Schedule is required.");

        var trimmed = schedule.Trim();
        if (trimmed is "@hourly" or "@daily")
            return CronResult.Ok(trimmed);

        var fields = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return CronResult.Fail<string>("Schedule must be a 5-field cron expression, @hourly, or @daily.");

        for (var i = 0; i < fields.Length; i++)
        {
            var error = ValidateField(fields[i], FieldRanges[i].Min, FieldRanges[i].Max);
            if (error is not null)
                return CronResult.Fail<string>($"Schedule field {i + 1} is invalid: {error}");
        }

        return CronResult.Ok(trimmed);
    }

    private static string? ValidateField(string field, int min, int max)
    {
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
                return "empty list item.";

            var error = ValidatePart(part, min, max);
            if (error is not null)
                return error;
        }

        return null;
    }

    private static string? ValidatePart(string part, int min, int max)
    {
        var pieces = part.Split('/');
        if (pieces.Length > 2)
            return "too many step separators.";

        if (pieces.Length == 2 && (!int.TryParse(pieces[1], CultureInfo.InvariantCulture, out var step) || step < 1))
            return "step must be a positive integer.";

        return ValidateBase(pieces[0], min, max);
    }

    private static string? ValidateBase(string value, int min, int max)
    {
        if (string.Equals(value, "*", StringComparison.Ordinal))
            return null;

        var range = value.Split('-');
        if (range.Length == 2)
        {
            if (!TryParseInRange(range[0], min, max, out var start)
                || !TryParseInRange(range[1], min, max, out var end))
                return $"range values must be between {min} and {max}.";

            return start <= end ? null : "range start must be less than or equal to range end.";
        }

        if (range.Length > 2)
            return "too many range separators.";

        return TryParseInRange(value, min, max, out _)
            ? null
            : $"value must be '*', a number between {min} and {max}, a range, a list, or a step.";
    }

    private static bool TryParseInRange(string value, int min, int max, out int parsed)
        => int.TryParse(value, CultureInfo.InvariantCulture, out parsed) && parsed >= min && parsed <= max;
}
