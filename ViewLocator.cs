using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using LiberaFx.ViewModels.Graph;
using LiberaFx.ViewModels.Main;
using LiberaFx.Views.Graph;

namespace LiberaFx;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    private readonly ConditionalWeakTable<GraphDocumentViewModel, GraphEditorView> _documentViews = new();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        if (param is GraphDocumentViewModel document)
        {
            return _documentViews.GetValue(document, static doc =>
                new GraphEditorView { DataContext = doc.Workspace });
        }

        var name = param.GetType().FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);

        if (type != null)
        {
            return (Control)Activator.CreateInstance(type)!;
        }

        return new TextBlock { Text = "Not Found: " + name };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase or GraphDocumentViewModel;
    }
}
