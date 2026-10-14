using System.Data.Common;

namespace Pos.Modules.Finance;

internal sealed record FinanceReceiptReplay(Guid Id, string IdempotencyHash, DateTime? CompletedAtUtc, DateTime? ReversedAtUtc);

internal static class FinanceReceiptReplayReader
{
    public static async Task<FinanceReceiptReplay?> ReadAndDisposeAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new(reader.GetGuid(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3));
        }
    }
}
