namespace Scope.Core.Cron.Launch;

using System.Globalization;

/// <summary>
/// Builds the up-front token-budget context note prepended to a cron run's prompt (T-2026-06-17-06,
/// confirmed decision 2). This is the COOPERATIVE layer of the runaway guard: it tells the agent its
/// hard token cap so a well-behaved harness plans to finish within it. It is deliberately supplemental
/// (the ambient-observability rule): enforcement does NOT depend on the agent honoring this note. The
/// proxy refuses new API calls at the budget and the supervisor force-kills past it regardless.
/// <para>
/// The note is host-shape neutral plain text. Each launcher decides whether and how to surface it
/// (prepend to the stdin prompt, a system flag, etc.) so the platform-independence rule holds; this
/// helper only owns the wording, not the delivery.
/// </para>
/// </summary>
public static class BudgetNotice
{
    /// <summary>
    /// Returns the prompt with the budget note prepended when a positive token budget is present, or
    /// the original prompt unchanged when the run is unbudgeted. Pure and allocation-light: no I/O.
    /// </summary>
    public static string PrependTo(string prompt, int? tokenBudget)
    {
        var note = Build(tokenBudget);
        return note is null ? prompt : note + "\n\n" + prompt;
    }

    /// <summary>
    /// Builds the budget note text for a positive token budget, or null when the run is unbudgeted.
    /// </summary>
    public static string? Build(int? tokenBudget)
    {
        if (tokenBudget is not int budget || budget <= 0)
            return null;

        var formatted = budget.ToString("N0", CultureInfo.InvariantCulture);
        return
            $"[Scope] You have a total token budget of {formatted} tokens (input + output + cache combined) " +
            "for this run. Plan your work to finish within it. When the budget is reached, Scope stops the " +
            "run: new model calls are refused with a terminal error and the process is terminated. Prioritize " +
            "completing and reporting your most important result before the budget is exhausted.";
    }
}
