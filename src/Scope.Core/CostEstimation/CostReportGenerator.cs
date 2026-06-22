namespace Scope.Core.CostEstimation;

using System.Text;
using Scope.Core.Utilities;
using System.Globalization;

/// <summary>
/// Generates cost reports from benchmark metrics and pricing configuration.
/// </summary>
public sealed class CostReportGenerator(ModelPricingConfig pricingConfig)
{
    /// <summary>
    /// Builds a cost report from benchmark metric results.
    /// </summary>
    public CostReport Generate(IReadOnlyList<BenchmarkMetricInput> benchmarkCases, int? outputTokens = null, int? agentLoops = null, bool? useCachedInput = null)
    {
        var estimator = new CostEstimator(pricingConfig);
        var pricingDefaults = pricingConfig.Defaults ?? new CostDefaults();
        var estOutput = outputTokens ?? pricingDefaults.EstimatedOutputTokens;
        var estLoops = agentLoops ?? pricingDefaults.EstimatedAgentLoops;
        var useCached = useCachedInput ?? pricingDefaults.IncludeCachedInput;

        var cases = benchmarkCases.Select(bc =>
        {
            var estimates = estimator.Estimate(bc.InputTokens, estOutput, estLoops, useCached);
            return new CostReportCase
            {
                BenchmarkId = bc.BenchmarkId,
                TargetSymbol = bc.TargetSymbol,
                InputTokens = bc.InputTokens,
                QualityScore = bc.QualityScore,
                CostPerQualityPoint = bc.QualityScore.HasValue
                    ? [.. estimates.Select(e => new CostPerQualityEntry
                    {
                        ModelId = e.ModelId,
                        CostPerQualityPoint = bc.QualityScore.Value == 0
                            ? null
                            : e.EstimatedTotalCostUsd / bc.QualityScore.Value
                    })]
                    : null,
                Estimates = estimates
            };
        }).ToList();

        var shockResults = pricingConfig.ShockScenarios.Select(shock => new CostReportShockResult
        {
            ScenarioName = shock.Name,
            InputMultiplier = shock.InputMultiplier,
            OutputMultiplier = shock.OutputMultiplier,
            LoopMultiplier = shock.LoopMultiplier,
            Cases = [.. benchmarkCases.Select(bc => new CostReportCase
            {
                BenchmarkId = bc.BenchmarkId,
                TargetSymbol = bc.TargetSymbol,
                InputTokens = bc.InputTokens,
                Estimates = estimator.EstimateWithShock(bc.InputTokens, shock, estOutput, estLoops, useCached)
            })]
        }).ToList();

        return new CostReport
        {
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            Assumptions = new CostReportAssumptions
            {
                EstimatedOutputTokens = estOutput,
                EstimatedAgentLoops = estLoops,
                IncludeCachedInput = useCached
            },
            Cases = cases,
            AggregateSummary = BuildAggregate(cases),
            ShockScenarios = shockResults
        };
    }

    private static CostAggregateSummary BuildAggregate(List<CostReportCase> cases)
    {
        var allEstimates = cases.SelectMany(c => c.Estimates.Select(e => new { c.BenchmarkId, e.ModelId, e.EstimatedTotalCostUsd })).ToList();
        if (allEstimates.IsNullOrEmpty)
            return new CostAggregateSummary();

        var avgByModel = allEstimates
            .GroupBy(e => e.ModelId, StringComparer.Ordinal)
            .Select(g => new ModelAggregateCost { ModelId = g.Key, CostUsd = Math.Round(g.Average(x => x.EstimatedTotalCostUsd), 6) })
            .OrderBy(m => m.ModelId, StringComparer.Ordinal)
            .ToList();

        var totalByModel = allEstimates
            .GroupBy(e => e.ModelId, StringComparer.Ordinal)
            .Select(g => new ModelAggregateCost { ModelId = g.Key, CostUsd = g.Sum(x => x.EstimatedTotalCostUsd) })
            .OrderBy(m => m.ModelId, StringComparer.Ordinal)
            .ToList();

        var highest = allEstimates.OrderByDescending(e => e.EstimatedTotalCostUsd).First();
        var lowest = allEstimates.OrderBy(e => e.EstimatedTotalCostUsd).First();

        return new CostAggregateSummary
        {
            AverageCostByModel = avgByModel,
            TotalCostByModel = totalByModel,
            HighestCostCase = new CaseHighlight { BenchmarkId = highest.BenchmarkId, ModelId = highest.ModelId, CostUsd = highest.EstimatedTotalCostUsd },
            LowestCostCase = new CaseHighlight { BenchmarkId = lowest.BenchmarkId, ModelId = lowest.ModelId, CostUsd = lowest.EstimatedTotalCostUsd }
        };
    }

    /// <summary>
    /// Renders a cost report as a Markdown table.
    /// </summary>
    public static string ToMarkdown(CostReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cost Estimation Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {report.GeneratedAt}");
        sb.AppendLine();
        sb.AppendLine("## Assumptions");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Estimated output tokens: {report.Assumptions.EstimatedOutputTokens}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Estimated agent loops: {report.Assumptions.EstimatedAgentLoops}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Using cached input pricing: {report.Assumptions.IncludeCachedInput}");
        sb.AppendLine();

        sb.AppendLine("## Base Estimates");
        sb.AppendLine();
        AppendCostTable(sb, report.Cases);

        if (report.Cases.Any(c => c.CostPerQualityPoint != null))
        {
            sb.AppendLine();
            sb.AppendLine("## Cost per Quality Point");
            sb.AppendLine();
            sb.AppendLine("| Case | Model | Quality Score | Cost/Quality Point |");
            sb.AppendLine("|------|-------|--------------|-------------------|");
            foreach (var c in report.Cases.Where(c => c.CostPerQualityPoint != null))
            {
                foreach (var entry in c.CostPerQualityPoint!)
                {
                    var cpq = entry.CostPerQualityPoint.HasValue
                        ? $"${entry.CostPerQualityPoint.Value:F6}"
                        : "N/A (grade 0)";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"| {c.BenchmarkId} | {entry.ModelId} | {c.QualityScore:F2} | {cpq} |");
                }
            }
        }

        if (report.AggregateSummary != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Aggregate Summary");
            sb.AppendLine();

            sb.AppendLine("### Average Cost per Task by Model");
            sb.AppendLine();
            sb.AppendLine("| Model | Average Cost |");
            sb.AppendLine("|-------|-------------|");
            foreach (var m in report.AggregateSummary.AverageCostByModel)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {m.ModelId} | ${m.CostUsd:F4} |");

            sb.AppendLine();
            sb.AppendLine("### Total Benchmark Cost by Model");
            sb.AppendLine();
            sb.AppendLine("| Model | Total Cost |");
            sb.AppendLine("|-------|-----------|");
            foreach (var m in report.AggregateSummary.TotalCostByModel)
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {m.ModelId} | ${m.CostUsd:F4} |");

            if (report.AggregateSummary.HighestCostCase != null)
            {
                sb.AppendLine();
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Highest-cost case:** {report.AggregateSummary.HighestCostCase.BenchmarkId} on {report.AggregateSummary.HighestCostCase.ModelId} (${report.AggregateSummary.HighestCostCase.CostUsd:F4})");
            }
            if (report.AggregateSummary.LowestCostCase != null)
                sb.AppendLine(CultureInfo.InvariantCulture, $"**Lowest-cost case:** {report.AggregateSummary.LowestCostCase.BenchmarkId} on {report.AggregateSummary.LowestCostCase.ModelId} (${report.AggregateSummary.LowestCostCase.CostUsd:F4})");
        }

        if (report.ShockScenarios.IsInhabited)
        {
            sb.AppendLine();
            sb.AppendLine("## Shock Scenarios");
            sb.AppendLine();

            foreach (var shock in report.ShockScenarios)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"### {shock.ScenarioName} (input: {shock.InputMultiplier}x, output: {shock.OutputMultiplier}x, loops: {shock.LoopMultiplier}x)");
                sb.AppendLine();
                AppendCostTable(sb, shock.Cases);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void AppendCostTable(StringBuilder sb, List<CostReportCase> cases)
    {
        sb.AppendLine("| Case | Model | Input Tokens | Output Tokens | Loops | Estimated Cost |");
        sb.AppendLine("|------|-------|-------------|---------------|-------|----------------|");

        foreach (var c in cases)
        {
            foreach (var est in c.Estimates)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {c.BenchmarkId} | {est.ModelId} | {est.InputTokens} | {est.EstimatedOutputTokens} | {est.EstimatedAgentLoops} | ${est.EstimatedTotalCostUsd:F4} |");
            }
        }
    }
}
