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
        var previousNodeLanguage = localization.SelectedNodeLanguage;
        try
        {
            foreach (var language in new[] { "en", "ja" })
            {
                localization.SelectedLanguage = localization.Languages.Single(item => item.Id == language);
                localization.SelectedNodeLanguage = localization.Languages.Single(item => item.Id == "en");
                var transformEntry = new NodeCatalogEntryViewModel(transform);
                var compareEntry = new NodeCatalogEntryViewModel(compare);
                Require(transformEntry.Name != compareEntry.Name && transformEntry.Group != compareEntry.Group,
                    "Enum types must be distinguishable in catalog names and groups.");
                Require(transformEntry.SearchTerms.Contains("TransformOrder", StringComparison.Ordinal) &&
                        compareEntry.SearchTerms.Contains("CompareMode", StringComparison.Ordinal),
                    "Enum search must retain stable English type names in either language.");
                Require(transformEntry.Name.Contains("Transform Order", StringComparison.Ordinal) &&
                        compareEntry.Name.Contains("Compare Mode", StringComparison.Ordinal),
                    "Enum node labels must use English in both catalog languages.");

                var workspace = new GraphWorkspaceViewModel(new UndoRedoManager());
                var catalog = new NodeCatalogViewModel(new ActiveGraphProvider(workspace));
                catalog.SearchText = "TransformOrder";
                Require(catalog.Groups.SelectMany(group => group.Nodes).Any(entry => entry.KindName == transform.Kind.Value),
                    "The catalog must find the type by its original English name.");
                catalog.SearchText = "変換順序";
                Require(catalog.Groups.SelectMany(group => group.Nodes).Any(entry => entry.KindName == transform.Kind.Value),
                    "Japanese search must work independently of the display languages.");
                localization.SelectedNodeLanguage = localization.Languages.Single(item => item.Id == "ja");
                Require(localization.SelectedLanguage.Id == language &&
                        new NodeCatalogEntryViewModel(transform).Name.Contains("変換順序", StringComparison.Ordinal),
                    "Changing the node language must translate the node name without changing the UI language.");
                var curve = workspace.CreateNode(GraphNodeKind.FloatFCurve);
                var savedName = curve.Name;
                Require(curve.DisplayName == "Fカーブ（スカラー）", "Default canvas labels must use the node language.");
                curve.Name = "My curve";
                localization.SelectedNodeLanguage = localization.Languages.Single(item => item.Id == "en");
                Require(curve.DisplayName == "My curve" && workspace.History.Undo(),
                    "Node language changes must preserve edited names and stay out of undo history.");
                var restored = workspace.GetNode(curve.Id);
                Require(restored.Name == savedName && restored.DisplayName == "Float F-Curve",
                    "Node language changes must preserve edited names and stay out of undo history.");
            }
        }
        finally
        {
            localization.SelectedNodeLanguage = previousNodeLanguage;
            localization.SelectedLanguage = previous;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
