using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiberaFx.Services;

public sealed record TeamProfile(Guid Id, string Name, string GroupDirectory);

public sealed class TeamProfilesService : ObservableObject
{
    private readonly string _path;
    private IReadOnlyList<TeamProfile> _profiles;
    private TeamProfile _active;
    private string? _statusKey;
    private object?[] _statusArgs = [];
    private bool _loadFailed;

    public IReadOnlyList<TeamProfile> Profiles => _profiles;
    public TeamProfile Active => _active;
    public string Status => _statusKey is null ? string.Empty : LocalizationService.Shared.Get(_statusKey, _statusArgs);
    public event EventHandler? Changed;

    public TeamProfilesService(string? path = null)
    {
        _path = path ?? Path.Combine(AppDataDirectory.Root, "LiberaFx", "team-profiles.json");
        MessageLocalization.Observe(this, static service => service.OnPropertyChanged(nameof(Status)));
        var fallback = new TeamProfile(Guid.NewGuid(), LocalizationService.Shared.Get("Messages.DefaultProject"), string.Empty);
        _profiles = [fallback];
        _active = fallback;
        try
        {
            if (!File.Exists(_path)) return;
            var file = JsonSerializer.Deserialize<ProfilesFile>(File.ReadAllText(_path));
            if (file is null || file.Version != 1 || file.Profiles is not { Count: > 0 } ||
                file.Profiles.Any(profile => !IsValid(profile)) ||
                file.Profiles.Select(profile => profile.Id).Distinct().Count() != file.Profiles.Count ||
                file.Profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != file.Profiles.Count ||
                file.Profiles.FirstOrDefault(profile => profile.Id == file.ActiveId) is not { } active)
                throw new InvalidDataException("Invalid team profiles.");
            _profiles = file.Profiles.AsReadOnly();
            _active = active;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            _loadFailed = true;
            SetStatus("Messages.LoadProfilesFailed", ex.Message);
        }
    }

    public bool Select(Guid id)
    {
        var selected = Profiles.FirstOrDefault(profile => profile.Id == id);
        if (selected is null) return false;
        if (selected.Id == Active.Id) return true;
        return Commit(Profiles, selected, "Messages.ActiveProject", selected.Name);
    }

    public bool Save(Guid? id, string name, string directory)
    {
        name = (name ?? string.Empty).Trim();
        directory = (directory ?? string.Empty).Trim();
        if (name.Length is 0 or > 100 || directory.Length > 4096 ||
            (directory.Length != 0 && !Path.IsPathFullyQualified(directory)))
        {
            SetStatus("Messages.EnterProjectDetails");
            return false;
        }
        if (id is not null && Profiles.All(profile => profile.Id != id))
        {
            SetStatus("Messages.ProjectMissing");
            return false;
        }
        if (Profiles.Any(profile => profile.Id != id && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Messages.UniqueProjectNames");
            return false;
        }
        var profile = new TeamProfile(id ?? Guid.NewGuid(), name, directory);
        var profiles = id is null
            ? Profiles.Append(profile).ToArray()
            : Profiles.Select(item => item.Id == id ? profile : item).ToArray();
        return Commit(profiles, profile, "Messages.TeamProjectSaved", profile.Name);
    }

    public bool Remove(Guid id)
    {
        if (Profiles.Count <= 1)
        {
            SetStatus("Messages.ProjectRequired");
            return false;
        }
        if (Profiles.All(profile => profile.Id != id)) return false;
        var profiles = Profiles.Where(profile => profile.Id != id).ToArray();
        var active = Active.Id == id ? profiles[0] : Active;
        return Commit(profiles, active, "Messages.ActiveProject", active.Name);
    }

    private bool Commit(IReadOnlyList<TeamProfile> profiles, TeamProfile active, string statusKey, params object?[] statusArgs)
    {
        if (_loadFailed)
        {
            SetStatus("Messages.ProfilesNeedRepair");
            return false;
        }
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $".team-profiles-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new ProfilesFile(1, active.Id, profiles.ToList()),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, true);
            temporary = null;
            _profiles = Array.AsReadOnly(profiles.ToArray());
            _active = active;
            OnPropertyChanged(nameof(Profiles));
            OnPropertyChanged(nameof(Active));
            SetStatus(statusKey, statusArgs);
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetStatus("Messages.SaveProfilesFailed", ex.Message);
            return false;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        OnPropertyChanged(nameof(Status));
    }

    private static bool IsValid(TeamProfile? profile) =>
        profile is not null && profile.Id != Guid.Empty && !string.IsNullOrWhiteSpace(profile.Name) &&
        profile.Name.Length <= 100 && profile.GroupDirectory is { Length: <= 4096 } directory &&
        (directory.Length == 0 || Path.IsPathFullyQualified(directory));

    private sealed record ProfilesFile(int Version, Guid ActiveId, List<TeamProfile>? Profiles);
}
