using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCAROC_Analysis.Services.LitigationModule.Store;

/// <summary>What a consumer may change on its own association with a case (plan section 12.3). Each field is optional: null leaves it as it is.</summary>
public sealed record ScopeOverlayChange(
    bool? IsHidden = null, bool? IsExcluded = null, string? ExclusionReason = null, string? AnalystNote = null, bool? IncludedInReport = null,
    string? PinnedFieldsJson = null);

public enum OverlayWriteResult { Applied, VersionConflict, NotFound }

/// <summary>The module's write side for scopes and associations: the identity of scopes and subjects, the per-scope alias counter, and the scope-versioned
/// overlay write. It touches the module's own tables and nothing else. The read side (the facade over today's tables, paging, parity) comes in later steps of M1.</summary>
public sealed class LitigationModuleStore(AppDbContext db, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private const int AliasRetries = 8;

    public async Task<ModuleSubject> CreateSubjectAsync(ModuleSubjectKind kind, string name, string? identifiersJson, CancellationToken ct)
    {
        var subject = new ModuleSubject { Kind = kind, Name = name, IdentifiersJson = identifiersJson, CreatedUtc = _time.GetUtcNow().UtcDateTime };
        db.Set<ModuleSubject>().Add(subject);
        await db.SaveChangesAsync(ct);
        return subject;
    }

    /// <summary>The scope for a consumer instance, created on first use. Two scopes may share one subject.</summary>
    public async Task<ModuleScope> EnsureScopeAsync(ModuleScopeKind kind, long externalId, long subjectId, CancellationToken ct)
    {
        var existing = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.Kind == kind && s.ExternalId == externalId, ct);
        if (existing is not null) return existing;
        var scope = new ModuleScope { Kind = kind, ExternalId = externalId, SubjectId = subjectId, CreatedUtc = _time.GetUtcNow().UtcDateTime };
        db.Set<ModuleScope>().Add(scope);
        try { await db.SaveChangesAsync(ct); return scope; }
        catch (DbUpdateException)
        {
            // Two callers created the same scope at once: the unique index let one win; return that one.
            db.Entry(scope).State = EntityState.Detached;
            return await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.Kind == kind && s.ExternalId == externalId, ct);
        }
    }

    /// <summary>A registry case, found by its legacy request-side id or created. The legacy reference is permanent.</summary>
    public Task<ModuleCase> EnsureCaseForLegacyAsync(long legacyLitigationCaseId, CancellationToken ct) =>
        EnsureCaseAsync(c => c.LegacyLitigationCaseId == legacyLitigationCaseId, () => new ModuleCase { LegacyLitigationCaseId = legacyLitigationCaseId }, ct);

    /// <summary>A registry case for a pre-login record, found by (job, <c>CaseRowId</c>) or created.</summary>
    public Task<ModuleCase> EnsureCaseForLegacyRowAsync(long legacyJobId, string caseRowId, CancellationToken ct) =>
        EnsureCaseAsync(c => c.LegacyJobId == legacyJobId && c.LegacyCaseRowId == caseRowId, () => new ModuleCase { LegacyJobId = legacyJobId, LegacyCaseRowId = caseRowId }, ct);

    /// <summary>Find-or-create under a unique index: when two callers create the same case at once, the loser's insert fails on the index and it returns the winner's row.</summary>
    private async Task<ModuleCase> EnsureCaseAsync(System.Linq.Expressions.Expression<Func<ModuleCase, bool>> match, Func<ModuleCase> create, CancellationToken ct)
    {
        var existing = await db.Set<ModuleCase>().FirstOrDefaultAsync(match, ct);
        if (existing is not null) return existing;
        var created = create();
        created.CreatedUtc = _time.GetUtcNow().UtcDateTime;
        db.Set<ModuleCase>().Add(created);
        try { await db.SaveChangesAsync(ct); return created; }
        catch (DbUpdateException)
        {
            db.Entry(created).State = EntityState.Detached;
            return await db.Set<ModuleCase>().AsNoTracking().FirstAsync(match, ct);   // the winner of the race; a real failure would not find a row and throws here
        }
    }

    /// <summary>Puts a case in a scope and gives it the scope's next alias (<c>C-n</c>), from a counter that is monotonic per scope and safe under concurrent callers.
    /// A case already in the scope keeps its alias. <paramref name="existingAlias"/> imports a legacy alias verbatim (a pre-login <c>CaseRowId</c>) and moves the counter past it.</summary>
    public async Task<ModuleScopeCase> AddCaseToScopeAsync(long scopeId, long caseId, string? existingAlias, CancellationToken ct)
    {
        if (existingAlias is null) return await AddWithNextAliasAsync(scopeId, caseId, ct);
        for (var attempt = 0; attempt < AliasRetries; attempt++)
        {
            var already = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeId == scopeId && x.CaseId == caseId, ct);
            if (already is not null) return already;

            var scope = await db.Set<ModuleScope>().FirstAsync(s => s.ScopeId == scopeId, ct);
            int index;
            string alias;
            if (existingAlias is not null)
            {
                alias = existingAlias;
                index = TryAliasIndex(existingAlias);
                if (index >= scope.NextAliasIndex) scope.NextAliasIndex = index + 1;
            }
            else { index = scope.NextAliasIndex; alias = "C-" + index; scope.NextAliasIndex = index + 1; }
            scope.Version++;

            var link = new ModuleScopeCase { ScopeId = scopeId, CaseId = caseId, Alias = alias, AliasIndex = index, CreatedUtc = _time.GetUtcNow().UtcDateTime };
            db.Set<ModuleScopeCase>().Add(link);
            try { await db.SaveChangesAsync(ct); return link; }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or DbUpdateException)
            {
                // A concurrent writer took the counter or the alias: forget our attempt and try again from the stored state.
                db.ChangeTracker.Clear();
                if (attempt == AliasRetries - 1) throw;
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>The generated-alias path. The counter is advanced by ONE atomic statement (<c>UPDATE ... OUTPUT</c>) inside the same transaction as the insert of the link, so concurrent callers
    /// queue on the scope row and each gets its own number: there is no retry race to lose, however many callers there are. If the transaction fails, the counter moves back with it.</summary>
    private async Task<ModuleScopeCase> AddWithNextAliasAsync(long scopeId, long caseId, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        var already = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeId == scopeId && x.CaseId == caseId, ct);
        if (already is not null) return already;

        var index = await AllocateAliasIndexAsync(scopeId, ct);
        var link = new ModuleScopeCase { ScopeId = scopeId, CaseId = caseId, Alias = "C-" + index, AliasIndex = index, CreatedUtc = _time.GetUtcNow().UtcDateTime };
        db.Set<ModuleScopeCase>().Add(link);
        try
        {
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return link;
        }
        catch (DbUpdateException)
        {
            // Either the same case was added by a concurrent caller between our check and our insert (the unique index let one win), or something else failed.
            db.ChangeTracker.Clear();
            if (tx is not null) await tx.RollbackAsync(ct);
            var winner = await db.Set<ModuleScopeCase>().AsNoTracking().FirstOrDefaultAsync(x => x.ScopeId == scopeId && x.CaseId == caseId, ct);
            if (winner is not null) return winner;   // the winner's link
            throw;                                   // not a duplicate: a real failure is not hidden
        }
    }

    private async Task<int> AllocateAliasIndexAsync(long scopeId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandText = "UPDATE [litigation].[Scopes] SET [NextAliasIndex] = [NextAliasIndex] + 1, [Version] = [Version] + 1 OUTPUT deleted.[NextAliasIndex] WHERE [ScopeId] = @id";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@id"; parameter.Value = scopeId; command.Parameters.Add(parameter);
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? throw new InvalidOperationException($"Scope {scopeId} does not exist.") : Convert.ToInt32(value);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    /// <summary>The only write a consumer makes: change its own association with a case, checked against the scope's version the caller last saw.
    /// A stale version is a conflict, not an overwrite; the caller re-reads and retries. Other scopes are never touched.</summary>
    public async Task<OverlayWriteResult> SetOverlayAsync(long scopeId, long caseId, ScopeOverlayChange change, long expectedScopeVersion, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var bumped = await db.Set<ModuleScope>().Where(s => s.ScopeId == scopeId && s.Version == expectedScopeVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (bumped == 0)
        {
            var exists = await db.Set<ModuleScope>().AnyAsync(s => s.ScopeId == scopeId, ct);
            return exists ? OverlayWriteResult.VersionConflict : OverlayWriteResult.NotFound;
        }

        var link = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeId == scopeId && x.CaseId == caseId, ct);
        if (link is null) { await tx.RollbackAsync(ct); return OverlayWriteResult.NotFound; }
        await db.Entry(link).ReloadAsync(ct);   // a tracked copy may be stale; apply the change to what is stored now, so another writer's fields survive

        if (change.IsHidden is { } hidden) link.IsHidden = hidden;
        if (change.IsExcluded is { } excluded) link.IsExcluded = excluded;
        if (change.ExclusionReason is not null) link.ExclusionReason = change.ExclusionReason;
        if (change.AnalystNote is not null) link.AnalystNote = change.AnalystNote;
        if (change.IncludedInReport is { } included) link.IncludedInReport = included;
        if (change.PinnedFieldsJson is not null) link.PinnedFieldsJson = change.PinnedFieldsJson;
        link.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        // What a request's pages, counts, report and analysis include depends on these flags: a change to one is a publication for that request (same transaction).
        if (change.IsHidden is not null || change.IsExcluded is not null || change.IncludedInReport is not null)
        {
            var scope = await db.Set<ModuleScope>().AsNoTracking().Where(s => s.ScopeId == scopeId).Select(s => new { s.Kind, s.ExternalId }).FirstAsync(ct);
            if (scope.Kind == ModuleScopeKind.Request)
                await MCAROC_Analysis.Services.LitigationModule.Refresh.RequestLiveData.BumpAsync(db, scope.ExternalId, ct);
        }
        await tx.CommitAsync(ct);
        return OverlayWriteResult.Applied;
    }

    private static int TryAliasIndex(string alias) =>
        alias.StartsWith("C-", StringComparison.Ordinal) && int.TryParse(alias.AsSpan(2), out var n) && n > 0 ? n : 0;
}
