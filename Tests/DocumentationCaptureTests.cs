using LiberaFx.Services.Mcp;
using ModelContextProtocol;

internal static class DocumentationCaptureTests
{
    public static void Run()
    {
        if (!GraphDocumentationMcpTools.SamplePath("Fireworks.lfxproj").EndsWith(
                Path.Combine("Assets", "Samples", "Fireworks.lfxproj"), StringComparison.Ordinal))
            throw new InvalidOperationException("Documentation samples must resolve only within bundled samples.");
        foreach (var name in new[] { @"..\Fireworks.lfxproj", "UserProject.lfxproj", @"C:\Private\Fireworks.lfxproj" })
        {
            try { GraphDocumentationMcpTools.SamplePath(name); }
            catch (McpException) { continue; }
            throw new InvalidOperationException("Arbitrary project capture must not be accepted.");
        }
    }
}
