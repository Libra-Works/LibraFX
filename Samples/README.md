# Standalone inspector examples

The two projects in this directory reference only `LiberaFx.Inspector.Abstractions`
and Avalonia UI packages. They do not reference the LIBERA FX application or its
ViewModels. Build each project independently, then add its output folder from
Settings → Inspectors. The output folder contains `liberafx-inspector.json`,
the plugin DLL, dependency DLLs and the dependency manifest.

```powershell
dotnet build Samples\Inspector.ColorRgb\Inspector.ColorRgb.csproj
dotnet build Samples\Inspector.ColorHsv\Inspector.ColorHsv.csproj
```

The sample inspectors edit the four-component Color JSON value through the SDK
context; the host validates and records each edit in the document's existing
undo history. The samples target .NET 10, Inspector SDK API 1 and Avalonia 12.1.6.

## Matching the editor appearance

Controls inside the editor inherit its Avalonia theme. For custom inspector or
extension UI, opt into the shared styles with `Classes="editor-card"` (section
border), `editor-heading` / `editor-label` (text), `editor-tool` (button), or
`editor-field` (text box). These use the active color scheme and retain the
standard control focus, hover, and disabled states. Standalone previews without
the editor styles can still use the standard Avalonia controls.
