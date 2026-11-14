using System.Globalization;
using PersonalOrderDesk.Services;

namespace PersonalOrderDesk.Presentation;

public sealed record UnsettledCopyList(string Text, int LineCount, long TotalCents, int MissingNumberCount, int MissingAmountCount)
{
    private sealed record Balance(string? BusinessOrderNo,long? OutstandingCents);
    public static UnsettledCopyList From(IEnumerable<WorkItemFinanceDto> items) => Build(items.Select(x=>new Balance(x.BusinessOrderNo,x.OutstandingCents)));
    public static UnsettledCopyList From(IEnumerable<PersonalOrderDesk.Client.OrderMoneySummary> items) => Build(items.Select(x=>new Balance(x.Number,x.Due)));
    private static UnsettledCopyList Build(IEnumerable<Balance> items)
    {
        var rows = items.ToArray();
        var due = rows.Where(x => x.OutstandingCents > 0).ToArray();
        var lines = due.Where(x => !string.IsNullOrWhiteSpace(x.BusinessOrderNo))
            .GroupBy(x => string.Join(" ", x.BusinessOrderNo!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Number = g.Key, Amount = g.Sum(x => x.OutstandingCents!.Value) }).ToArray();
        return new(string.Join("\n", lines.Select(x => $"{x.Number} {(x.Amount / 100m).ToString("0.00", CultureInfo.InvariantCulture)}")),
            lines.Length, lines.Sum(x => x.Amount), due.Count(x => string.IsNullOrWhiteSpace(x.BusinessOrderNo)), rows.Count(x => x.OutstandingCents is null));
    }
}
