# Repository Guidelines

## Project Structure & Module Organization
`src/MBDInspector/` contains the WPF application: `App.xaml` and `MainWindow.xaml` define the UI, while `StepGeometryExtractor.cs`, `StepTessellator.cs`, and `StepColorExtractor.cs` handle STEP-derived rendering data. The solution entry point is [`MBDInspector.slnx`](C:\dev\Csharp\20260316\MBDInspector\MBDInspector.slnx), which currently includes only the app project.

`submodules/StepParser/` is a git submodule dependency, not an app folder. Its parser source lives under `submodules/StepParser/src/StepParser/`, and its test harness lives under `submodules/StepParser/tests/StepParser.Tests/`.

## Build, Test, and Development Commands
Use the .NET 10 SDK on Windows.

```powershell
git submodule update --init --recursive
$env:DOTNET_CLI_HOME="$PWD\.dotnet_home"
dotnet build MBDInspector.slnx
dotnet run --project src/MBDInspector/MBDInspector.csproj -- .\sample.step
dotnet run --project submodules/StepParser/tests/StepParser.Tests/StepParser.Tests.csproj
```

`dotnet build` compiles the WPF viewer. `dotnet run --project ...MBDInspector.csproj` launches the desktop app and can accept an optional STEP file path. The `StepParser.Tests` project is a custom console test runner; there is no top-level test project in this repository yet.

## Coding Style & Naming Conventions
Follow the existing C# style: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and `ImplicitUsings` disabled. Use `PascalCase` for types, methods, XAML element names, and public members; use `_camelCase` for private fields such as `_loaded`.

Keep code direct and local. This codebase prefers small helper methods over abstraction-heavy patterns. Match existing comments: brief, only where intent is not obvious.

## Testing Guidelines
For parser changes, add or update focused regression coverage in `submodules/StepParser/tests/StepParser.Tests/Program.cs`. Name tests by behavior, for example `Parser_ParsesMinimalSample` or `Cli_StrictMode_PrintsAnnotationAndExits2OnWarnings`.

For viewer changes, verify manually with representative `.stp` or `.step` files: file open, drag-and-drop, render mode toggles, camera reset, and diagnostics display.

## Commit & Pull Request Guidelines
Recent history uses Conventional Commit prefixes such as `feat:`. Continue with `feat:`, `fix:`, `refactor:`, and `docs:` plus a concise subject.

Pull requests should describe the user-visible change, list build/test evidence, and note whether changes touch the app, the `StepParser` submodule, or both. Include screenshots for UI changes.
