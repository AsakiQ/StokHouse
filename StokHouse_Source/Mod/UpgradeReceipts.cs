using StokHouse.Core;

namespace StokHouse;

internal sealed class UpgradeReceipts
{
    private readonly Dictionary<Guid, (string Code, DateTimeOffset Expires)> completed = new();

    internal (string Code, bool Replay) Run(UpgradeRequest request, DateTimeOffset now, Func<string> start)
    {
        if (now >= request.Expires)
            return ("expired", false);
        if (completed.TryGetValue(request.Id, out var result))
            return (result.Code, true);

        // Record the grant before calling the native purchase so the same grant cannot charge again.
        completed.Add(request.Id, ("upgrade-error", request.Expires));
        string code = start();
        completed[request.Id] = (code, request.Expires);
        return (code, false);
    }

    internal void Expire(DateTimeOffset now)
    {
        foreach (var id in completed.Where(entry => entry.Value.Expires <= now).Select(entry => entry.Key).ToArray())
            completed.Remove(id);
    }

    internal void Clear() => completed.Clear();
}
