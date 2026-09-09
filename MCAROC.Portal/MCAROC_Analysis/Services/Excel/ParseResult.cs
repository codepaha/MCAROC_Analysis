using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel;

public record ParseIssue(
    IssueSeverity Severity,
    string ParserName,
    string? FieldName,
    string? RawValue,
    string IssueCode,
    string Message,
    int? RowNumber = null);

public class ParseResult<T>
{
    public List<T> Items { get; } = [];
    public List<ParseIssue> Warnings { get; } = [];
    public List<ParseIssue> Errors { get; } = [];

    public void AddWarning(ParseIssue issue) => Warnings.Add(issue);
    public void AddError(ParseIssue issue) => Errors.Add(issue);
}
