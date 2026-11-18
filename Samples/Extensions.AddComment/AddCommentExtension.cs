using System.Text.Json;
using LiberaFx.Extensions.Abstractions;

namespace LiberaFx.Samples;

public sealed class AddCommentExtension : IEditorExtension
{
    public void Register(IExtensionRegistration registration) => registration.AddCommand(new(
        "add-comment", "Add extension comment", "Adds one comment through a validated undoable edit plan.",
        (context, cancellation) =>
        {
            cancellation.ThrowIfCancellationRequested();
            using var plan = JsonDocument.Parse("""
                {"Nodes":[{"Key":"note","Kind":"Comment","Name":"Extension comment","X":100,"Y":100}],"Connections":[]}
                """);
            if (!context.TryApplyPlan(plan.RootElement, out var error))
                throw new InvalidOperationException(error);
            return Task.CompletedTask;
        }));

    public void Dispose() { }
}
