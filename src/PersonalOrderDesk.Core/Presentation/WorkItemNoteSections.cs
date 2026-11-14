using PersonalOrderDesk.Domain;

namespace PersonalOrderDesk.Presentation;

public sealed record WorkItemNoteSections(string CurrentNotes, string ImportHints)
{
    private static readonly string[] ImportPrefixes = ["历史物流要求待核：", "历史结款待核：", "历史结款标记待核：", "历史成本待核："];

    public static string? MergeCurrentNotes(string? current, string? existing, SourceKind source)
    {
        var hints = From(existing, source).ImportHints;
        var result = string.Join("\n", new[] { current?.Trim(), hints }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return result.Length == 0 ? null : result;
    }

    public static WorkItemNoteSections From(string? notes, SourceKind source)
    {
        if (string.IsNullOrWhiteSpace(notes)) return new("", "");
        if (source != SourceKind.HistoricalImport) return new(notes, "");
        var current = new List<string>();
        var imported = new List<string>();
        foreach (var line in notes.Replace("\r\n", "\n").Split('\n'))
            (ImportPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)) ? imported : current).Add(line);
        return new(string.Join("\n", current).Trim(), string.Join("\n", imported).Trim());
    }
}
