# MBDInspector

A minimal WPF viewer for ISO 10303-21 STEP files (AP203, AP214, AP242 MBD).

Open a file, see the wireframe. That's it.

## Features

- Open `.stp` / `.step` files via **File → Open** (`Ctrl+O`) or drag-and-drop
- Wireframe rendering with orbit / pan / zoom (mouse drag)
- File info panel: schema, edition, entity count, edge count, diagnostics
- Keyboard shortcuts: **F** fit all, **Home** reset camera

## Rendering

Geometry is extracted from the parsed STEP entity graph using three strategies (in order):

1. **EDGE_CURVE** → vertex endpoints → `CARTESIAN_POINT` coordinates (full B-rep edge list)
2. **POLY_LINE** → connected point sequence
3. **CARTESIAN_POINT** cloud (no connectivity information)

## Dependencies

| Package | Purpose |
|---------|---------|
| [StepParser](https://github.com/MinimalWindowsDev/StepParser) | STEP tokenizer / lexer / parser (git submodule) |
| [HelixToolkit.Wpf](https://github.com/helix-toolkit/helix-toolkit) | 3D viewport, orbit camera, line rendering |

## Build

```powershell
git clone --recurse-submodules https://github.com/MinimalWindowsDev/MBDInspector
cd MBDInspector
$env:DOTNET_CLI_HOME="$PWD\.dotnet_home"
dotnet build MBDInspector.slnx
```

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Windows.

## License

GPL-3.0 — see [LICENSE](LICENSE).
