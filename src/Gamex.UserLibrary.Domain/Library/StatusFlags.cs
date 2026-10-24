namespace Gamex.UserLibrary.Domain.Library;

/// <summary>The five library statuses of an entry, or the aggregate over all of a user's entries for one game.</summary>
public readonly record struct StatusFlags(bool Owned, bool Wishlist, bool ForExchange, bool ForSale, bool WantToBuy)
{
    public static StatusFlags None => default;

    /// <summary>A flag is set if any entry has it (SPEC-user-library §6: stats count users, not copies).</summary>
    public static StatusFlags Aggregate(IEnumerable<StatusFlags> entries) =>
        entries.Aggregate(None, (acc, f) => new StatusFlags(
            acc.Owned || f.Owned,
            acc.Wishlist || f.Wishlist,
            acc.ForExchange || f.ForExchange,
            acc.ForSale || f.ForSale,
            acc.WantToBuy || f.WantToBuy));

    public static StatusDelta Delta(StatusFlags before, StatusFlags after) => new(
        Diff(before.Owned, after.Owned),
        Diff(before.Wishlist, after.Wishlist),
        Diff(before.ForExchange, after.ForExchange),
        Diff(before.ForSale, after.ForSale),
        Diff(before.WantToBuy, after.WantToBuy));

    private static int Diff(bool before, bool after) => (after ? 1 : 0) - (before ? 1 : 0);
}

/// <summary>Per-status change (-1, 0, +1) published as <c>library.game.status.changed</c>.</summary>
public readonly record struct StatusDelta(int Owned, int Wishlist, int ForExchange, int ForSale, int WantToBuy)
{
    public bool IsZero => Owned == 0 && Wishlist == 0 && ForExchange == 0 && ForSale == 0 && WantToBuy == 0;
}
