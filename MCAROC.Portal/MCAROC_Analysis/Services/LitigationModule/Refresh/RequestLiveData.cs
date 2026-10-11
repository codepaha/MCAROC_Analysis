using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>The request-level live-data version (plan section 4.2a). Anything that changes what a request's litigation views show - a live publication to its cases, a discovery
/// insert, a merge or un-merge, a hide or un-hide, a new order or hearing - bumps it in the same transaction as the change, so the page fence and every litigation-derived cache
/// miss and rebuild. The bump is a single atomic UPDATE, never a read-modify-write.</summary>
public static class RequestLiveData
{
    /// <summary>Adds one to the request's live-data version and returns the value <b>this call</b> produced, or null when the request does not exist. One statement does
    /// both (<c>UPDATE ... OUTPUT inserted</c>), so two overlapping bumps return two different values. Uses the context's current transaction, if any.</summary>
    public static async Task<long?> BumpAsync(AppDbContext db, long requestId, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE [Requests] SET [LiveDataVersion] = [LiveDataVersion] + 1 OUTPUT inserted.[LiveDataVersion] WHERE [RequestId] = @id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = requestId;
            command.Parameters.Add(parameter);
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            var result = await command.ExecuteScalarAsync(ct);
            return result is null or DBNull ? null : Convert.ToInt64(result);
        }
        finally { if (opened) await connection.CloseAsync(); }
    }

    public static Task<long> ReadAsync(AppDbContext db, long requestId, CancellationToken ct) =>
        db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.LiveDataVersion).FirstOrDefaultAsync(ct);
}
