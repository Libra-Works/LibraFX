using LiberaFx.Models.Graph;
using LiberaFx.Services.Settings;

namespace LiberaFx.Services.Project;

public static class GraphConversionPresentation
{
    public static string Describe(GraphConversionResolution resolution) =>
        LocalizationService.Shared.Get(resolution.Status switch
        {
            GraphConversionStatus.Missing => "Conversions.Missing",
            GraphConversionStatus.Unique => "Conversions.Required",
            _ => "Conversions.Ambiguous"
        }, string.Join(", ", resolution.Candidates.Select(rule => rule.Identifier)));

    public static string Describe(GraphNodeDefinition definition) =>
        string.Join(Environment.NewLine, definition.Conversions.Select(conversion =>
            LocalizationService.Shared.Get("Conversions.Signature",
                GraphNodeDefinition.DisplayPortType(definition.GetInputPortType(conversion.InputPortId)!),
                GraphNodeDefinition.DisplayPortType(definition.GetOutputPortType(conversion.OutputPortId)!),
                conversion.OutputPortId is null ? "" : $" [{conversion.OutputPortId}]")));
}
