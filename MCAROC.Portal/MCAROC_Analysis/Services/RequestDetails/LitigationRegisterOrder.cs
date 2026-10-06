using System.Linq.Expressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.RequestDetails;

/// <summary>The order the case register lists cases in, matching the litigation report: pending cases first, then disposed, then
/// status not reported; within a section the forum by seniority (Supreme Court, High Court, District Court, then the tribunals), cases
/// against the company before ones it brought, newest filing first, and the case id to keep paging stable.
///
/// Every key is a SQL expression, so the order is applied before paging. Two of them are close approximations of what a row shows,
/// because the exact answer needs the party names or the court name, which SQL cannot read: the forum is the vendor's type (with its
/// court category as a fallback for the three court tiers), and the side is the vendor's direction.</summary>
public static class LitigationRegisterOrder
{
    public static IOrderedQueryable<LitigationCase> Apply(IQueryable<LitigationCase> query) =>
        query.OrderBy(Section).ThenBy(Forum).ThenBy(Side).ThenByDescending(FilingKey).ThenBy(c => c.LitigationCaseId);

    /// <summary>0 pending, 1 disposed, 2 status not reported — the classifier's own definitions, inlined so SQL can order by them.</summary>
    public static readonly Expression<Func<LitigationCase, int>> Section = BuildSection();

    /// <summary>The position of the forum in <see cref="LitigationInstitution"/>.</summary>
    public static readonly Expression<Func<LitigationCase, int>> Forum = c =>
        c.Type != null && (c.Type.ToLower() == "supreme" || c.Type.ToLower() == "supremecourt" || c.Type.ToLower() == "supreme_court")
            || c.CourtCategory == "supreme_court" ? (int)LitigationInstitution.SupremeCourt
        : c.Type != null && (c.Type.ToLower() == "highcourt" || c.Type.ToLower() == "high_court" || c.Type.ToLower() == "high")
            || c.CourtCategory == "high_court" ? (int)LitigationInstitution.HighCourt
        : c.Type != null && (c.Type.ToLower() == "district" || c.Type.ToLower() == "districtcourt" || c.Type.ToLower() == "district_court")
            || c.CourtCategory == "district_court" ? (int)LitigationInstitution.DistrictCourt
        : c.Type != null && c.Type.ToLower() == "consumer" ? (int)LitigationInstitution.ConsumerCourt
        : c.Type != null && c.Type.ToLower() == "cestat" ? (int)LitigationInstitution.Cestat
        : c.Type != null && c.Type.ToLower() == "nclt" ? (int)LitigationInstitution.Nclt
        : c.Type != null && c.Type.ToLower() == "nclat" ? (int)LitigationInstitution.Nclat
        : c.Type != null && c.Type.ToLower() == "itat" ? (int)LitigationInstitution.Itat
        : c.Type != null && c.Type.ToLower() == "drt" ? (int)LitigationInstitution.Drt
        : c.Type != null && c.Type.ToLower() == "drat" ? (int)LitigationInstitution.Drat
        : (int)LitigationInstitution.Others;

    /// <summary>0 against the company, 1 by the company, 2 not stated.</summary>
    public static readonly Expression<Func<LitigationCase, int>> Side = c =>
        c.Direction != null && c.Direction.ToLower() == "against" ? 0 : c.Direction != null && c.Direction.ToLower() == "by" ? 1 : 2;

    /// <summary>The filing date as sortable text: "dd-MM-yyyy" becomes "yyyy-MM-dd", an ISO date is kept, anything else sorts last.</summary>
    public static readonly Expression<Func<LitigationCase, string>> FilingKey = c =>
        c.FilingDate == null || c.FilingDate.Length < 10 ? ""
        : c.FilingDate.Substring(4, 1) == "-" ? c.FilingDate.Substring(0, 10)
        : c.FilingDate.Substring(6, 4) + "-" + c.FilingDate.Substring(3, 2) + "-" + c.FilingDate.Substring(0, 2);

    private static Expression<Func<LitigationCase, int>> BuildSection()
    {
        var c = Expression.Parameter(typeof(LitigationCase), "c");
        Expression Inline(Expression<Func<LitigationCase, bool>> predicate) => new ParameterReplacer(predicate.Parameters[0], c).Visit(predicate.Body);
        var body = Expression.Condition(Inline(LitigationCaseStatusClassifier.IsPendingExpr), Expression.Constant(0),
            Expression.Condition(Inline(LitigationCaseStatusClassifier.IsDisposedExpr), Expression.Constant(1), Expression.Constant(2)));
        return Expression.Lambda<Func<LitigationCase, int>>(body, c);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
