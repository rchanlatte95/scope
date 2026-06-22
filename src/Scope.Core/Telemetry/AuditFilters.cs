namespace Scope.Core.Telemetry;

/// <summary>
/// Filters for the <c>scope audit</c> read-only query interface over observability data.
/// </summary>
public record AuditFilters(
    string? Surface = null,
    string? Operation = null,
    string? Agent = null,
    string? Since = null,
    string? RequestId = null,
    string? Direction = null,
    int Limit = 20,
    bool FullPayloads = false,
    bool Follow = false,
    string? RunKey = null,
    string? DispatchIdFilter = null,
    string? Identifier = null,
    string? HtmlOutputPath = null);
