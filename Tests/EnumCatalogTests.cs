using LiberaFx.Models.Commands;
using LiberaFx.Models.Graph;
using LiberaFx.Services;
using LiberaFx.ViewModels;

namespace LiberaFx.Tests;

internal static class EnumCatalogTests
{
    public static void Run()
    {
        var transformId = Guid.Parse("b0af61d2-29f6-4cc9-a71e-000000000201");
        var compareId = Guid.Parse("e9778c40-209b-4ce8-918d-35c000000011");
        var transform = GraphNodeDefinition.All.Single(item =>
            item.Kind == new GraphNodeKind(GraphEnumBranchSchema.ValueKind(transformId)));
        var compare = GraphNodeDefinition.All.Single(item =>
            item.Kind == new GraphNodeKind(GraphEnumBranchSchema.ValueKind(compareId)));
        var localization = LocalizationService.Shared;
        var previous = localization.SelectedLanguage;
        try
        {
            foreach (var language in new[] { "en", "ja" })
            {
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == language);
                var transformEntry = new NodeCatalogEntryViewModel(transform);
                var compareEntry = new NodeCatalogEntryViewModel(compare);
                Require(transformEntry.Name != compareEntry.Name && transformEntry.Group != compareEntry.Group,
                    "Enum types must be distinguishable in catalog names and groups.");
                Require(transformEntry.SearchTerms.Contains("TransformOrder", StringComparison.Ordinal) &&
                        compareEntry.SearchTerms.Contains("CompareMode", StringComparison.Ordinal),
                    "Enum search must retain stable English type names in either language.");
                if (language == "ja")
                    Require(transformEntry.Name.Contains("変換順序", StringComparison.Ordinal) &&
                            compareEntry.Name.Contains("比較モード", StringComparison.Ordinal),
                        "Enum type names must be localized in the Japanese catalog.");

                var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
                var catalog = new NodeCatalogViewModel(new ActiveGraphProvider(workspace));
                catalog.SearchText = "TransformOrder";
                Require(catalog.Groups.SelectMany(group => group.Nodes).Any(entry => entry.KindName == transform.Kind.Value),
                    "The catalog must find the type by its original English name.");
                if (language == "ja")
                {
                    catalog.SearchText = "変換順序";
                    Require(catalog.Groups.SelectMany(group => group.Nodes).Any(entry => entry.KindName == transform.Kind.Value),
                        "The catalog must find the localized Enum type name.");
                }
            }
        }
        finally { localization.SelectedLanguage = previous; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
