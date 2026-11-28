using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;
using LiberaFx.ViewModels.Graph;

namespace LiberaFx.Services.Extensions;

public interface IInspectorEditorProvider
{
    int Priority { get; }
    bool Supports(string type);
    Control CreateEditor(NodeParameterEditorViewModel parameter);
}

public interface INamedInspectorEditorProvider : IInspectorEditorProvider
{
    bool SupportsEditor(string editor, string type);
}

public static class InspectorEditors
{
    private static readonly Lazy<IReadOnlyList<IInspectorEditorProvider>> Providers = new(Load);
    private static readonly List<string> Errors = [];
    public static IReadOnlyList<string> Warnings { get { _ = Providers.Value; return Errors; } }

    public static Control? CreateEditor(NodeParameterEditorViewModel parameter)
    {
        foreach (var provider in Providers.Value)
        {
            try
            {
                if (provider.Supports(parameter.Type))
                    return provider.CreateEditor(parameter) ?? throw new InvalidOperationException("The editor was null.");
            }

            catch (Exception ex)
            {
                Errors.Add($"{provider.GetType().FullName}: {ex.Message}");
            }
        }
        return null;
    }

    public static Control? CreateNamedEditor(NodeParameterEditorViewModel parameter, string editor)
    {
        foreach (var provider in Providers.Value.OfType<INamedInspectorEditorProvider>())
        {
            try
            {
                if (provider.SupportsEditor(editor, parameter.Type))
                    return provider.CreateEditor(parameter) ?? throw new InvalidOperationException("The editor was null.");
            }
            catch (Exception ex)
            {
                Errors.Add($"{provider.GetType().FullName}: {ex.Message}");
            }
        }
        return null;
    }

    private static IReadOnlyList<IInspectorEditorProvider> Load()
    {
        var result = new List<IInspectorEditorProvider>();
        if (Environment.GetEnvironmentVariable("LIBERAFX_INSPECTOR_PLUGINS") != "1") return result;
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Inspectors");
        if (!Directory.Exists(directory)) return result;
        string[] paths;
        try { paths = Directory.GetFiles(directory, "*.dll"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Errors.Add($"Inspector directory: {ex.Message}");
            return result;
        }
        foreach (var path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
                foreach (var type in assembly.GetTypes().Where(type => !type.IsAbstract && type.IsPublic &&
                             typeof(IInspectorEditorProvider).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null))
                    result.Add((IInspectorEditorProvider)Activator.CreateInstance(type)!);
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or FileLoadException or
                                       ReflectionTypeLoadException or TargetInvocationException or MemberAccessException)
            {
                Errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return result.Select(provider =>
        {
            try { return (Provider: provider, Priority: provider.Priority); }
            catch (Exception ex)
            {
                Errors.Add($"{provider.GetType().FullName}: {ex.Message}");
                return (Provider: provider, Priority: int.MinValue);
            }
        }).OrderByDescending(item => item.Priority).Select(item => item.Provider).ToArray();
    }
}
