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
