namespace LiberaFx.Services;

internal static class MessageLocalization
{
    public static string Error(string message)
    {
        var key = ErrorKey(message);
        return key is null ? message : LocalizationService.Shared.Get(key);
    }

    public static string? ErrorKey(string message) =>
        message switch
        {
            "Both nodes must belong to this graph." => "Messages.Error.NodesNotInGraph",
            "A node cannot connect to itself." => "Messages.Error.SelfConnection",
            "Group boundary ports are not implemented yet." => "Messages.Error.GroupPortsUnavailable",
            "Comment nodes do not have ports." => "Messages.Error.CommentPorts",
            "The target input port does not exist." => "Messages.Error.InputPortMissing",
            "The target input is already connected." => "Messages.Error.InputAlreadyConnected",
            "Port categories and data types must match." => "Messages.Error.PortTypeMismatch",
            "These ports are already connected." => "Messages.Error.AlreadyConnected",
            "Connections cannot create a cycle." => "Messages.Error.ConnectionCycle",
            var value when value.StartsWith("The value is invalid for this parameter.", StringComparison.Ordinal) => "Messages.Error.InvalidParameterValue",
            "Choose a .lfxb file that is not an open project." => "Messages.Error.ChooseBinaryFile",
            "Select one Graph Group to save." => "Messages.Error.SelectGroup",
            "Select a saved group asset." => "Messages.Error.SelectSavedGroup",
            "The group library is unavailable." => "Messages.Error.GroupLibraryUnavailable",
            "The graph or group library changed; check for updates again." => "Messages.Error.GraphOrLibraryChanged",
            "The graph changed; check for updates again." => "Messages.Error.GraphChanged",
            "The group asset is locked." => "Editor.Error.GroupLocked",
            "The group asset changed; reload it first." => "Editor.Error.GroupChanged",
            "The saved group revision conflicts with the current asset." => "Editor.Error.GroupRevisionConflict",
            "Group asset contains invalid JSON." => "Editor.Error.GroupInvalid",
            "Group asset is too large." => "Editor.Error.GroupTooLarge",
            _ => null
        };

    public static void Observe<T>(T target, Action<T> refresh) where T : class
    {
        var localization = LocalizationService.Shared;
        var weakTarget = new WeakReference<T>(target);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (weakTarget.TryGetTarget(out var instance))
                refresh(instance);
            else
                localization.LanguageChanged -= handler;
        };
        localization.LanguageChanged += handler;
    }
}
