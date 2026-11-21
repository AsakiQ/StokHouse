using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StokHouse.FamilyBudget.Spec;

internal sealed record ShippingClaim(Guid Farm, int Day, long Player, int Gold);

internal static class FamilyShipping
{
    // The game adapter supplies evidence of one native sale of the combined main and mini
    // shipping bins. The online credit and host fallback must use the same farmer and day.
    internal static BudgetBook RecordClearedBin(BudgetBook book, ShippingClaim claim)
    {
        if (claim.Farm != book.Farm || claim.Player == 0 || claim.Day < 1 || claim.Day > book.Day || claim.Gold < 0)
            throw new InvalidOperationException("The shipping claim does not match this farm or day.");
        Guid id = Key(claim);
        if (book.Postings.TryGetValue(id, out var recorded))
        {
            if (recorded.Actor != claim.Player || recorded.Kind != PostingKind.Shipping ||
                recorded.State != PostingState.Applied || recorded.Amount != claim.Gold)
                throw new InvalidOperationException("The shipping result conflicts with a recorded sale.");
            return book;
        }
        if (claim.Day != book.Day)
            throw new InvalidOperationException("The recorded shipping day has already closed.");
        if (claim.Gold == 0 || book.ForPlayer(claim.Player) is not { } account)
            return book;

        return book.Post(id, account.Id, claim.Player, claim.Gold, PostingKind.Shipping)
            .Resolve(new ActionReceipt(id, claim.Player, account.Id, PostingState.Applied));
    }

    private static Guid Key(ShippingClaim claim)
    {
        Span<byte> identity = stackalloc byte[32];
        claim.Farm.TryWriteBytes(identity);
        BinaryPrimitives.WriteInt64LittleEndian(identity[16..], claim.Player);
        BinaryPrimitives.WriteInt32LittleEndian(identity[24..], claim.Day);
        BinaryPrimitives.WriteInt32LittleEndian(identity[28..], (int)PostingKind.Shipping);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(identity, digest);
        return new Guid(digest[..16]);
    }
}
