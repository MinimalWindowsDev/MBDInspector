using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using Microsoft.Win32;
using StepParser.Diagnostics;
using StepParser.Lexer;
using StepParser.Parser;

namespace MBDInspector;

public partial class MainWindow : Window
{
    private StepFile? _loaded;

    // Materials
    private static readonly Material SolidMaterial = new MaterialGroup
    {
        Children =
        {
            new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(180, 192, 210))),
            new SpecularMaterial(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 60)
        }
    };

    public MainWindow() => InitializeComponent();

    // ── Event handlers ────────────────────────────────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e)        => PromptOpen();
    private void Exit_Click(object sender, RoutedEventArgs e)        => Close();
    private void FitAll_Click(object sender, RoutedEventArgs e)      => Viewport.ZoomExtents();
    private void ResetCamera_Click(object sender, RoutedEventArgs e) => Viewport.ResetCamera();

    private void RenderMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded is not null) Render(_loaded);
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loaded is not null) Render(_loaded);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            LoadFile(files[0]);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F)    { Viewport.ZoomExtents();  e.Handled = true; }
        if (e.Key == Key.Home) { Viewport.ResetCamera();  e.Handled = true; }
    }

    // ── File loading ──────────────────────────────────────────────────────

    public void OpenFile(string path) => LoadFile(path);

    private void PromptOpen()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "STEP files|*.stp;*.step|All files|*.*",
            Title  = "Open STEP file"
        };
        if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        StatusText.Text = $"Loading {Path.GetFileName(path)} …";
        try
        {
            var diagnostics = new List<ParseDiagnostic>();
            var raw         = Tokenizer.TokenizeFile(path, diagnostics);
            var tokens      = StepParser.Lexer.Lexer.Lex(raw, diagnostics);
            _loaded         = new StepFileParser(tokens, diagnostics).Parse(path);

            Render(_loaded);
            UpdateInfoPanel(path, _loaded, diagnostics);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Failed to load file.";
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void Render(StepFile stepFile)
    {
        // Remove previous geometry (keep DefaultLights)
        for (int i = Viewport.Children.Count - 1; i >= 0; i--)
        {
            if (Viewport.Children[i] is ModelVisual3D or LinesVisual3D)
                Viewport.Children.RemoveAt(i);
        }

        bool showSolid = ModeSolid.IsChecked == true || ModeSolidEdges.IsChecked == true;
        bool showEdges = ModeWireframe.IsChecked == true || ModeSolidEdges.IsChecked == true;
        double opacity = OpacitySlider?.Value ?? 1.0;

        if (showSolid)
        {
            StatusText.Text = "Tessellating …";
            MeshGeometry3D mesh = StepTessellator.Tessellate(stepFile.Data);

            if (mesh.Positions.Count > 0)
            {
                Material mat = opacity >= 1.0
                    ? SolidMaterial
                    : MakeTransparent(opacity);

                Viewport.Children.Add(new ModelVisual3D
                {
                    Content = new GeometryModel3D
                    {
                        Geometry     = mesh,
                        Material     = mat,
                        BackMaterial = mat
                    }
                });
            }
        }

        if (showEdges)
        {
            var edges  = StepGeometryExtractor.Extract(stepFile.Data);
            var pts    = new Point3DCollection(edges.Count * 2);
            foreach (StepGeometryExtractor.Edge edge in edges)
            {
                pts.Add(edge.Start);
                pts.Add(edge.End);
            }

            if (pts.Count > 0)
            {
                Viewport.Children.Add(new LinesVisual3D
                {
                    Points    = pts,
                    Color     = showSolid ? Color.FromRgb(30, 30, 30) : Colors.LimeGreen,
                    Thickness = showSolid ? 0.5 : 1.0
                });
            }
        }

        Viewport.ZoomExtents();

        // Update status with face/edge counts
        if (_loaded is not null)
        {
            int faceCount = _loaded.Data.Values.Count(e =>
                string.Equals(e.Name, "ADVANCED_FACE", StringComparison.OrdinalIgnoreCase));
            int edgeCount = _loaded.Data.Values.Count(e =>
                string.Equals(e.Name, "EDGE_CURVE", StringComparison.OrdinalIgnoreCase));
            StatusText.Text = $"{Path.GetFileName(stepFile.Source)}  ·  "
                            + $"{stepFile.Data.Count} entities  ·  "
                            + $"{faceCount} faces  ·  {edgeCount} edges";
        }
    }

    private static Material MakeTransparent(double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(opacity * 255), 180, 192, 210));
        return new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(brush),
                new SpecularMaterial(new SolidColorBrush(
                    Color.FromArgb((byte)(opacity * 200), 255, 255, 255)), 60)
            }
        };
    }

    // ── Info panel ────────────────────────────────────────────────────────

    private void UpdateInfoPanel(
        string path, StepFile stepFile, List<ParseDiagnostic> diagnostics)
    {
        int errors   = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        int faceCount = stepFile.Data.Values.Count(e =>
            string.Equals(e.Name, "ADVANCED_FACE", StringComparison.OrdinalIgnoreCase));

        InfoPanel.ItemsSource = new[]
        {
            KV("File",     Path.GetFileName(path)),
            KV("Size",     $"{new FileInfo(path).Length / 1024.0:F1} KB"),
            KV("Schema",   Shorten(stepFile.FileSchema)),
            KV("Edition",  stepFile.DetectedEdition?.ToString() ?? "—"),
            KV("Entities", stepFile.Data.Count.ToString()),
            KV("Faces",    faceCount.ToString()),
            KV("Errors",   errors.ToString()),
            KV("Warnings", warnings.ToString()),
        };

        Title = $"MBD Inspector — {Path.GetFileName(path)}";
    }

    private static KeyValuePair<string, string> KV(string k, string v) => new(k, v);

    private static string Shorten(string? s)
    {
        if (s is null) return "—";
        int brace = s.IndexOf('{');
        return brace > 0 ? s[..brace].Trim() : s;
    }
}
