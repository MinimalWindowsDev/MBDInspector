using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
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
    public MainWindow() => InitializeComponent();

    // ── Event handlers ───────────────────────────────────────────────────

    private void Open_Click(object sender, RoutedEventArgs e) => PromptOpen();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void FitAll_Click(object sender, RoutedEventArgs e) => Viewport.ZoomExtents();
    private void ResetCamera_Click(object sender, RoutedEventArgs e) => Viewport.ResetCamera();

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
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
        if (e.Key == Key.F)    { Viewport.ZoomExtents(); e.Handled = true; }
        if (e.Key == Key.Home) { Viewport.ResetCamera(); e.Handled = true; }
    }

    // ── Core load logic ──────────────────────────────────────────────────

    public void OpenFile(string path) => LoadFile(path);

    private void PromptOpen()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "STEP files|*.stp;*.step|All files|*.*",
            Title  = "Open STEP file"
        };
        if (dlg.ShowDialog(this) == true)
            LoadFile(dlg.FileName);
    }

    private void LoadFile(string path)
    {
        StatusText.Text = $"Loading {Path.GetFileName(path)} …";
        try
        {
            var diagnostics = new List<ParseDiagnostic>();
            var rawTokens   = Tokenizer.TokenizeFile(path, diagnostics);
            var tokens      = StepParser.Lexer.Lexer.Lex(rawTokens, diagnostics);
            var stepFile    = new StepFileParser(tokens, diagnostics).Parse(path);

            var edges = StepGeometryExtractor.Extract(stepFile.Data);
            RenderWireframe(edges);
            UpdateInfoPanel(path, stepFile, diagnostics, edges.Count);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Load error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Failed to load file.";
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────

    private void RenderWireframe(IReadOnlyList<StepGeometryExtractor.Edge> edges)
    {
        // Remove previous wireframe, keep DefaultLights
        for (int i = Viewport.Children.Count - 1; i >= 0; i--)
            if (Viewport.Children[i] is LinesVisual3D)
                Viewport.Children.RemoveAt(i);

        if (edges.Count == 0) return;

        var pts = new Point3DCollection(edges.Count * 2);
        foreach (StepGeometryExtractor.Edge edge in edges)
        {
            pts.Add(edge.Start);
            pts.Add(edge.End);
        }

        Viewport.Children.Add(new LinesVisual3D
        {
            Points    = pts,
            Color     = Colors.LimeGreen,
            Thickness = 1
        });

        Viewport.ZoomExtents();
    }

    // ── Info panel ───────────────────────────────────────────────────────

    private void UpdateInfoPanel(
        string path, StepFile stepFile,
        List<ParseDiagnostic> diagnostics, int edgeCount)
    {
        int errors   = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        InfoPanel.ItemsSource = new[]
        {
            KV("File",     Path.GetFileName(path)),
            KV("Size",     $"{new FileInfo(path).Length / 1024.0:F1} KB"),
            KV("Schema",   Shorten(stepFile.FileSchema)),
            KV("Edition",  stepFile.DetectedEdition?.ToString() ?? "—"),
            KV("Entities", stepFile.Data.Count.ToString()),
            KV("Edges",    edgeCount.ToString()),
            KV("Errors",   errors.ToString()),
            KV("Warnings", warnings.ToString()),
        };

        Title = $"MBD Inspector — {Path.GetFileName(path)}";
        StatusText.Text = $"{Path.GetFileName(path)}  ·  {stepFile.Data.Count} entities  ·  {edgeCount} edges"
                        + (errors   > 0 ? $"  ·  {errors} error(s)"   : string.Empty)
                        + (warnings > 0 ? $"  ·  {warnings} warning(s)" : string.Empty);
    }

    private static KeyValuePair<string, string> KV(string k, string v) => new(k, v);

    private static string Shorten(string? s)
    {
        if (s is null) return "—";
        int brace = s.IndexOf('{');
        return brace > 0 ? s[..brace].Trim() : s;
    }
}
