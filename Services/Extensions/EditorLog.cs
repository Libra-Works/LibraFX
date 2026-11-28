using System.Collections.ObjectModel;

namespace LiberaFx.Services.Extensions;

public sealed record EditorLogEntry(string Time, string Category, string Message);

public sealed class EditorLog
{
    private const int Capacity = 2000;
    public ObservableCollection<EditorLogEntry> Entries { get; } = [];

    public void Add(string category, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        Entries.Add(new EditorLogEntry(DateTime.Now.ToString("HH:mm:ss.fff"), category, message));
        if (Entries.Count > Capacity) Entries.RemoveAt(0);
    }

    public void Clear() => Entries.Clear();
}
