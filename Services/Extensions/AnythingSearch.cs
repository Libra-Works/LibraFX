namespace LiberaFx.Services.Extensions;

public sealed record AnythingSearchItem(string Id, string Category, string Name, string Detail,
    string Aliases, Func<Task> ExecuteAsync, bool IsFavorite = false);

public static class AnythingSearch
{
    public static IReadOnlyList<AnythingSearchItem> Find(IEnumerable<AnythingSearchItem> items, string? query,
        int limit = 100)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var terms = (query ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return items.Select((item, index) => (Item: item, Index: index, Score: Score(item, terms)))
            .Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Item.IsFavorite).ThenByDescending(item => item.Score).ThenBy(item => item.Index)
            .Take(limit).Select(item => item.Item).ToArray();
    }

    private static int Score(AnythingSearchItem item, string[] terms)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var name = Match(item.Name, term);
            var other = Match(item.Detail + " " + item.Category + " " + item.Aliases, term);
            if (name < 0 && other < 0) return -1;
            total += Math.Max(name < 0 ? -1 : name + 100, other);
        }
        return total;
    }

    private static int Match(string text, string term)
    {
        var exact = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0) return 1000 + (exact == 0 ? 100 : 0) - Math.Min(exact, 100);
        var position = 0;
        var score = 0;
        var previous = -2;
        foreach (var character in term)
        {
            var found = text.IndexOf(character.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return -1;
            score += found == previous + 1 ? 12 : 1;
            if (found == 0 || !char.IsLetterOrDigit(text[found - 1])) score += 8;
            previous = found;
            position = found + 1;
        }
        return score;
    }
}
