using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using Microsoft.Win32;
using StepParser.Diagnostics;
using StepParser.Parser;

namespace MBDInspector;

public partial class MainWindow : Window
{
    private static readonly Regex EntityIdRegex = new(@"#(?<id>\d+)", RegexOptions.Compiled);

    private LoadedDocument? _document;
    private AppSettings _settings = new([], null, null, false);
    private string? _loadedPath;
    private IReadOnlyList<EntityListItem> _allEntities = [];
    private Dictionary<int, Color> _colorMap = [];
    private readonly Dictionary<int, EntityListItem> _entityLookup = [];
    private readonly Dictionary<Model3D, int> _faceHitMap = [];
    private readonly HashSet<int> _hiddenEntityIds = [];
    private HashSet<int>? _isolatedEntityIds;
    private readonly List<Point3D> _measurementPoints = [];
    private int _loadGeneration;
    private GeometryModel3D? _selectionModel;
    private LinesVisual3D? _selectionLines;
    private LinesVisual3D? _measurementLines;
    private PointsVisual3D? _measurementMarkers;
    private bool _suppressReferenceSelection;
    private bool _isLoaded;
    private Point? _viewportMouseDownPoint;

    private static readonly Color DefaultFaceColor = Color.FromRgb(180, 192, 210);
    private static readonly Color SelectionColor = Color.FromRgb(255, 170, 0);

    public MainWindow()
    {
        RuntimeLog.Info("MainWindow constructor entered.");
        InitializeComponent();
        Viewport.Children.Add(new DefaultLights());
        UpdateEmptyPanels();
        RuntimeLog.Info("MainWindow initialized.");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RuntimeLog.Info("MainWindow loaded event fired.");
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        EnsureVisibleOnPrimaryWorkArea();
        _settings = AppSettingsStore.Load();
        RefreshRecentFilesMenu();
        ApplyProjectionSetting(_settings.UseOrthographicCamera);
        ApplySavedCamera(_settings.LastCamera);

        if (!string.IsNullOrWhiteSpace(_settings.LastOpenedFile) && File.Exists(_settings.LastOpenedFile))
        {
            _ = LoadFileAsync(_settings.LastOpenedFile);
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isLoaded || Viewport is null)
        {
            return;
        }

        _settings = _settings with
        {
            LastOpenedFile = _loadedPath,
            LastCamera = CaptureCameraBookmark(),
            UseOrthographicCamera = Viewport.Camera is OrthographicCamera
        };

        AppSettingsStore.Save(_settings);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => PromptOpen();
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void FitAll_Click(object sender, RoutedEventArgs e) => Viewport.ZoomExtents();
    private void ResetCamera_Click(object sender, RoutedEventArgs e) => Viewport.ResetCamera();
    private void IsometricView_Click(object sender, RoutedEventArgs e) => SetStandardView(new Vector3D(-1, -1, -1), new Vector3D(0, 0, 1));
    private void FrontView_Click(object sender, RoutedEventArgs e) => SetStandardView(new Vector3D(0, -1, 0), new Vector3D(0, 0, 1));
    private void RightView_Click(object sender, RoutedEventArgs e) => SetStandardView(new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1));
    private void TopView_Click(object sender, RoutedEventArgs e) => SetStandardView(new Vector3D(0, 0, -1), new Vector3D(0, 1, 0));

    private void ToggleProjection_Click(object sender, RoutedEventArgs e)
    {
        ApplyProjectionSetting(!(Viewport.Camera is OrthographicCamera));
        _settings = _settings with { UseOrthographicCamera = Viewport.Camera is OrthographicCamera };
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_loadedPath))
        {
            _ = LoadFileAsync(_loadedPath);
        }
    }

    private void HideSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (int entityId in GetSelectedEntityIds())
        {
            _hiddenEntityIds.Add(entityId);
        }

        _isolatedEntityIds = null;
        RerenderIfLoaded();
    }

    private void IsolateSelected_Click(object sender, RoutedEventArgs e)
    {
        HashSet<int> selected = GetSelectedEntityIds();
        _isolatedEntityIds = selected.Count == 0 ? null : selected;
        RerenderIfLoaded();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        _hiddenEntityIds.Clear();
        _isolatedEntityIds = null;
        RerenderIfLoaded();
    }

    private void RenderMode_Changed(object sender, RoutedEventArgs e) => RerenderIfLoaded();
    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => RerenderIfLoaded();
    private void EntitySearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyEntityFilter();
    private void DiagnosticSeverityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyDiagnosticFilter();
    private void SectionControl_Changed(object sender, RoutedEventArgs e)
    {
        if (SectionControlHelper.TryUpdateStatusText(SectionText, SectionSlider?.Value, out string? text))
        {
            SectionText.Text = text;
        }
        RerenderIfLoaded();
    }

    private void DiagnosticsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DiagnosticsGrid.SelectedItem is DiagnosticItem item && item.EntityId.HasValue)
        {
            NavigateToEntity(item.EntityId.Value);
        }
    }

    private void EntityList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedEntityDetails();
        UpdateSelectionVisual();
    }

    private void OutboundReferenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReferenceSelection || OutboundReferenceList.SelectedItem is not EntityReferenceItem item)
        {
            return;
        }

        NavigateToEntity(item.Id);
        OutboundReferenceList.SelectedItem = null;
    }

    private void InboundReferenceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressReferenceSelection || InboundReferenceList.SelectedItem is not EntityReferenceItem item)
        {
            return;
        }

        NavigateToEntity(item.Id);
        InboundReferenceList.SelectedItem = null;
    }

    private void StructureList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StructureList.SelectedItem is StructureItem item)
        {
            NavigateToEntity(item.Id);
            StructureList.SelectedItem = null;
        }
    }

    private void PmiList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PmiList.SelectedItem is StructureItem item)
        {
            NavigateToEntity(item.Id);
            PmiList.SelectedItem = null;
        }
    }

    private void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string path } && File.Exists(path))
        {
            _ = LoadFileAsync(path);
        }
    }

    private void Viewport_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewportMouseDownPoint = e.GetPosition(Viewport);
    }

    private void Viewport_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_document is null || _viewportMouseDownPoint is null)
        {
            return;
        }

        Point currentPoint = e.GetPosition(Viewport);
        Vector delta = currentPoint - _viewportMouseDownPoint.Value;
        _viewportMouseDownPoint = null;
        if (delta.Length > 4)
        {
            return;
        }

        foreach (Viewport3DHelper.HitResult hit in Viewport3DHelper.FindHits(Viewport.Viewport, currentPoint))
        {
            if (_faceHitMap.TryGetValue(hit.Model, out int entityId))
            {
                if (MeasureModeButton.IsChecked == true && TryCaptureMeasurementPoint(currentPoint))
                {
                    e.Handled = true;
                    return;
                }

                if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    ToggleEntitySelection(entityId);
                }
                else
                {
                    NavigateToEntity(entityId);
                }

                e.Handled = true;
                return;
            }
        }

        if (MeasureModeButton.IsChecked == true)
        {
            TryCaptureMeasurementPoint(currentPoint);
        }
    }

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
        {
            _ = LoadFileAsync(files[0]);
        }
    }

    private void ExportScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            FileName = $"{Path.GetFileNameWithoutExtension(_document.File.Source)}.png",
            Title = "Export Viewport Screenshot"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            BitmapSource bitmap = Viewport3DHelper.RenderBitmap(Viewport.Viewport, Brushes.Transparent, 1);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(dialog.FileName);
            encoder.Save(stream);
            StatusText.Text = $"Saved screenshot to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        if (_document is null || string.IsNullOrWhiteSpace(_loadedPath))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "JSON file|*.json",
            FileName = $"{Path.GetFileNameWithoutExtension(_loadedPath)}.summary.json",
            Title = "Export Summary JSON"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var payload = new
            {
                Source = _document.File.Source,
                Schema = _document.File.FileSchema,
                Edition = _document.File.DetectedEdition,
                FileSize = _document.File.FileSize,
                EntityCount = _document.File.Data.Count,
                EntityTypes = _document.EntityTypes,
                HiddenEntities = _hiddenEntityIds.OrderBy(id => id).ToList(),
                IsolatedEntities = _isolatedEntityIds?.OrderBy(id => id).ToList(),
                Diagnostics = _document.Diagnostics.Select(diagnostic => new
                {
                    Severity = diagnostic.Severity.ToString(),
                    diagnostic.Line,
                    diagnostic.Column,
                    diagnostic.Message
                })
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
            StatusText.Text = $"Saved summary to {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Export error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            PromptOpen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F)
        {
            Viewport.ZoomExtents();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Home)
        {
            Viewport.ResetCamera();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && !string.IsNullOrWhiteSpace(_loadedPath))
        {
            _ = LoadFileAsync(_loadedPath);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _hiddenEntityIds.Clear();
            _isolatedEntityIds = null;
            RerenderIfLoaded();
            e.Handled = true;
        }
    }

    public void OpenFile(string path) => _ = LoadFileAsync(path);

    private void PromptOpen()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "STEP files|*.stp;*.step|All files|*.*",
            Title = "Open STEP file"
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = LoadFileAsync(dialog.FileName);
        }
    }

    private async Task LoadFileAsync(string path)
    {
        int generation = ++_loadGeneration;
        SetBusy(true, $"Loading {Path.GetFileName(path)} …");

        try
        {
            LoadedDocument document = await Task.Run(() => StepDocumentBuilder.Load(path));
            if (generation != _loadGeneration)
            {
                return;
            }

            _document = document;
            _loadedPath = document.Path;
            _allEntities = document.Entities;
            _colorMap = StepColorExtractor.Extract(document.File.Data);
            _entityLookup.Clear();
            foreach (EntityListItem item in document.Entities)
            {
                _entityLookup[item.Id] = item;
            }

            AddRecentFile(path);
            UpdateInfoPanel(document);
            UpdateEntityTypePanel(document.EntityTypes);
            StructureList.ItemsSource = document.StructureItems;
            PmiList.ItemsSource = document.PmiItems;
            ApplyEntityFilter();
            ApplyDiagnosticFilter();
            Render(document);

            Title = $"MBD Inspector — {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            RuntimeLog.Error($"Failed to load file: {path}", ex);
            MessageBox.Show(ex.Message, "Load error", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Failed to load file.";
        }
        finally
        {
            BusyIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void Render(LoadedDocument document)
    {
        ClearSceneVisuals();

        bool showSolid = ModeSolid.IsChecked == true || ModeSolidEdges.IsChecked == true;
        bool showEdges = ModeWireframe.IsChecked == true || ModeSolidEdges.IsChecked == true;
        double opacity = OpacitySlider?.Value ?? 1.0;

        if (showSolid)
        {
            foreach (FaceMeshItem faceMesh in StepTessellator.TessellateFaces(document.File.Data, _colorMap))
            {
                if (!IsEntityVisible(faceMesh.EntityId) || faceMesh.Mesh.Positions.Count == 0)
                {
                    continue;
                }

                Material material = MakeMaterial(faceMesh.FaceColor, opacity);
                var model = new GeometryModel3D
                {
                    Geometry = faceMesh.Mesh,
                    Material = material,
                    BackMaterial = material
                };

                _faceHitMap[model] = faceMesh.EntityId;
                Viewport.Children.Add(new ModelVisual3D { Content = model });
            }
        }

        if (showEdges && _isolatedEntityIds is null && _hiddenEntityIds.Count == 0)
        {
            IReadOnlyList<StepGeometryExtractor.Edge> edges = StepGeometryExtractor.Extract(document.File.Data);
            if (edges.Count > 0)
            {
                Viewport.Children.Add(CreateLineVisual(
                    edges,
                    showSolid ? Color.FromRgb(30, 30, 30) : Colors.LimeGreen,
                    showSolid ? 0.5 : 1.0));
            }
        }

        UpdateSelectionVisual();
        UpdateMeasurementVisual();
        Viewport.ZoomExtents();

        int faceCount = document.File.Data.Values.Count(entity =>
            string.Equals(entity.Name, "ADVANCED_FACE", StringComparison.OrdinalIgnoreCase));
        int edgeCount = document.File.Data.Values.Count(entity =>
            string.Equals(entity.Name, "EDGE_CURVE", StringComparison.OrdinalIgnoreCase));
        string visibilityState = _isolatedEntityIds is not null
            ? $"  ·  isolated {_isolatedEntityIds.Count}"
            : _hiddenEntityIds.Count > 0 ? $"  ·  hidden {_hiddenEntityIds.Count}" : string.Empty;

        StatusText.Text = $"{Path.GetFileName(document.File.Source)}  ·  "
                        + $"{document.File.Data.Count} entities  ·  "
                        + $"{faceCount} faces  ·  {edgeCount} edges{visibilityState}";
    }

    private void ApplyEntityFilter()
    {
        if (!UiInitializationGuard.AreReady(EntitySearchBox, EntityList, EntityListStatus))
        {
            return;
        }

        string filter = EntitySearchBox.Text?.Trim() ?? string.Empty;
        HashSet<int> selectedIds = GetSelectedEntityIds();
        IEnumerable<EntityListItem> entities = _allEntities;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            entities = entities.Where(item =>
                item.Header.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Preview.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.Id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        List<EntityListItem> filtered = entities.ToList();
        EntityList.ItemsSource = filtered;
        EntityListStatus.Text = _document is null
            ? "No document loaded."
            : $"{filtered.Count} of {_allEntities.Count} entities shown.";

        EntityList.SelectedItems.Clear();
        foreach (EntityListItem item in filtered.Where(item => selectedIds.Contains(item.Id)))
        {
            EntityList.SelectedItems.Add(item);
        }
    }

    private void ApplyDiagnosticFilter()
    {
        if (!UiInitializationGuard.AreReady(DiagnosticsGrid, DiagnosticsStatus))
        {
            return;
        }

        if (_document is null)
        {
            DiagnosticsGrid.ItemsSource = Array.Empty<DiagnosticItem>();
            DiagnosticsStatus.Text = "No diagnostics.";
            return;
        }

        string filter = (DiagnosticSeverityFilter?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "All";
        IEnumerable<ParseDiagnostic> diagnostics = _document.Diagnostics;
        if (!string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics = diagnostics.Where(diagnostic =>
                string.Equals(diagnostic.Severity.ToString(), filter, StringComparison.OrdinalIgnoreCase));
        }

        List<DiagnosticItem> items = diagnostics
            .Select(diagnostic => new DiagnosticItem(
                diagnostic.Severity.ToString(),
                diagnostic.Line,
                diagnostic.Column,
                diagnostic.Message,
                ExtractEntityId(diagnostic.Message)))
            .ToList();

        DiagnosticsGrid.ItemsSource = items;
        int errors = items.Count(item => item.Severity == nameof(DiagnosticSeverity.Error));
        int warnings = items.Count(item => item.Severity == nameof(DiagnosticSeverity.Warning));
        int info = items.Count(item => item.Severity == nameof(DiagnosticSeverity.Info));
        DiagnosticsStatus.Text = $"{items.Count} diagnostics shown  ·  {errors} errors  ·  {warnings} warnings  ·  {info} info";
    }

    private void UpdateInfoPanel(LoadedDocument document)
    {
        int errors = document.Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        int warnings = document.Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        int faceCount = document.File.Data.Values.Count(entity =>
            string.Equals(entity.Name, "ADVANCED_FACE", StringComparison.OrdinalIgnoreCase));

        InfoPanel.ItemsSource = new[]
        {
            KV("File", Path.GetFileName(document.Path)),
            KV("Size", $"{new FileInfo(document.Path).Length / 1024.0:F1} KB"),
            KV("Schema", Shorten(document.File.FileSchema)),
            KV("Edition", document.File.DetectedEdition?.ToString() ?? "—"),
            KV("Entities", document.File.Data.Count.ToString()),
            KV("Faces", faceCount.ToString()),
            KV("Errors", errors.ToString()),
            KV("Warnings", warnings.ToString())
        };
    }

    private void UpdateEntityTypePanel(IReadOnlyList<EntityTypeItem> entityTypes) => EntityTypeList.ItemsSource = entityTypes;

    private void UpdateSelectedEntityDetails()
    {
        List<int> selectedIds = GetSelectedEntityIds().OrderBy(id => id).ToList();
        if (_document is null || selectedIds.Count == 0)
        {
            SelectedEntityTitle.Text = "Selected Entity";
            SelectionHint.Text = "Select entities, click a face, or double-click diagnostics to navigate.";
            EntityDetailPanel.ItemsSource = Array.Empty<KeyValuePair<string, string>>();
            OutboundReferenceList.ItemsSource = Array.Empty<EntityReferenceItem>();
            InboundReferenceList.ItemsSource = Array.Empty<EntityReferenceItem>();
            SelectedEntityRaw.Text = string.Empty;
            return;
        }

        if (selectedIds.Count > 1)
        {
            HashSet<int> unionOutbound = [];
            HashSet<int> unionInbound = [];
            foreach (int id in selectedIds)
            {
                if (_document.OutboundReferences.TryGetValue(id, out IReadOnlyList<int>? outbound))
                {
                    unionOutbound.UnionWith(outbound);
                }

                if (_document.InboundReferences.TryGetValue(id, out IReadOnlyList<int>? inbound))
                {
                    unionInbound.UnionWith(inbound);
                }
            }

            SelectedEntityTitle.Text = $"Selected Entities  ·  {selectedIds.Count}";
            SelectionHint.Text = "Multiple entities selected. Hide or isolate the selection from the toolbar.";
            EntityDetailPanel.ItemsSource = new[]
            {
                KV("Count", selectedIds.Count.ToString()),
                KV("Ids", string.Join(", ", selectedIds.Take(12)) + (selectedIds.Count > 12 ? "…" : string.Empty)),
                KV("Out", unionOutbound.Count.ToString()),
                KV("In", unionInbound.Count.ToString()),
                KV("Hidden", selectedIds.Count(id => _hiddenEntityIds.Contains(id)).ToString())
            };
            _suppressReferenceSelection = true;
            OutboundReferenceList.ItemsSource = BuildReferenceItems(unionOutbound.OrderBy(id => id).ToList());
            InboundReferenceList.ItemsSource = BuildReferenceItems(unionInbound.OrderBy(id => id).ToList());
            _suppressReferenceSelection = false;
            SelectedEntityRaw.Text = string.Join(Environment.NewLine, selectedIds.Take(8).Select(id => StepParameterFormatter.FormatEntity(_document.File.Data[id])));
            return;
        }

        int entityId = selectedIds[0];
        EntityListItem item = _entityLookup[entityId];
        EntityInstance entity = _document.File.Data[item.Id];
        int parameterCount = entity.Parameters.Count;
        int componentCount = entity.Components?.Count ?? 0;
        int referenceCount = StepParameterFormatter.CountReferences(entity);
        string? color = _colorMap.TryGetValue(item.Id, out Color value)
            ? $"#{value.R:X2}{value.G:X2}{value.B:X2}"
            : null;

        int outboundCount = _document.OutboundReferences.TryGetValue(item.Id, out IReadOnlyList<int>? outboundIds) ? outboundIds.Count : 0;
        int inboundCount = _document.InboundReferences.TryGetValue(item.Id, out IReadOnlyList<int>? inboundIds) ? inboundIds.Count : 0;

        SelectedEntityTitle.Text = $"Selected Entity  ·  #{item.Id}  {item.Name}";
        SelectionHint.Text = BuildSelectionHint(item.Name);
        KeyValuePair<string, string>[] baseItems =
        [
            KV("Id", item.Id.ToString()),
            KV("Type", item.Name),
            KV("Complex", entity.IsComplex ? "Yes" : "No"),
            KV("Params", parameterCount.ToString()),
            KV("Components", componentCount.ToString()),
            KV("Refs", referenceCount.ToString()),
            KV("Out", outboundCount.ToString()),
            KV("In", inboundCount.ToString()),
            KV("Hidden", _hiddenEntityIds.Contains(item.Id) ? "Yes" : "No"),
            KV("Color", color ?? "—")
        ];
        EntityDetailPanel.ItemsSource = baseItems
            .Concat(StepParameterFormatter.DescribeEntity(entity))
            .ToArray();

        _suppressReferenceSelection = true;
        OutboundReferenceList.ItemsSource = BuildReferenceItems(outboundIds);
        InboundReferenceList.ItemsSource = BuildReferenceItems(inboundIds);
        _suppressReferenceSelection = false;

        SelectedEntityRaw.Text = StepParameterFormatter.FormatEntity(entity);
    }

    private List<EntityReferenceItem> BuildReferenceItems(IReadOnlyList<int>? ids)
    {
        if (ids is null)
        {
            return [];
        }

        return ids
            .Where(id => _entityLookup.ContainsKey(id))
            .Select(id =>
            {
                EntityListItem item = _entityLookup[id];
                return new EntityReferenceItem(id, item.Header, item.Preview);
            })
            .ToList();
    }

    private void UpdateSelectionVisual()
    {
        RemoveSelectionVisuals();

        if (_document is null)
        {
            return;
        }

        List<int> selectedIds = GetSelectedEntityIds().ToList();
        if (selectedIds.Count == 0)
        {
            return;
        }

        var allEdges = new List<StepGeometryExtractor.Edge>();
        foreach (int entityId in selectedIds)
        {
            if (StepTessellator.TryTessellateFace(entityId, _document.File.Data, out MeshGeometry3D? faceMesh))
            {
                var brush = new SolidColorBrush(Color.FromArgb(110, SelectionColor.R, SelectionColor.G, SelectionColor.B));
                var material = new DiffuseMaterial(brush);
                _selectionModel = new GeometryModel3D
                {
                    Geometry = faceMesh,
                    Material = material,
                    BackMaterial = material
                };
                Viewport.Children.Add(new ModelVisual3D { Content = _selectionModel });
            }

            allEdges.AddRange(StepGeometryExtractor.ExtractEntityEdges(entityId, _document.File.Data));
        }

        if (allEdges.Count > 0)
        {
            _selectionLines = CreateLineVisual(allEdges, SelectionColor, selectedIds.Count > 1 ? 3.0 : 2.5);
            Viewport.Children.Add(_selectionLines);
        }
    }

    private static string BuildSelectionHint(string entityName) => entityName switch
    {
        "ADVANCED_FACE" => "Selected face highlighted with solid overlay and outline. Clicking rendered faces also selects them.",
        "EDGE_CURVE" => "Selected edge highlighted in the viewport.",
        "POLY_LINE" => "Selected polyline highlighted in the viewport.",
        _ => "Selection details shown in the sidebar. Face picking, hide, and isolate are available."
    };

    private void NavigateToEntity(int entityId)
    {
        if (EntityList.ItemsSource is not IEnumerable<EntityListItem> items)
        {
            return;
        }

        EntityListItem? target = items.FirstOrDefault(item => item.Id == entityId);
        if (target is null)
        {
            EntitySearchBox.Text = string.Empty;
            ApplyEntityFilter();
            target = _allEntities.FirstOrDefault(item => item.Id == entityId);
        }

        if (target is not null)
        {
            EntityList.SelectedItems.Clear();
            EntityList.SelectedItem = target;
            EntityList.ScrollIntoView(target);
        }
    }

    private void ToggleEntitySelection(int entityId)
    {
        if (EntityList.ItemsSource is not IEnumerable<EntityListItem> items)
        {
            return;
        }

        EntityListItem? target = items.FirstOrDefault(item => item.Id == entityId);
        if (target is null)
        {
            NavigateToEntity(entityId);
            return;
        }

        if (EntityList.SelectedItems.Contains(target))
        {
            EntityList.SelectedItems.Remove(target);
        }
        else
        {
            EntityList.SelectedItems.Add(target);
        }
    }

    private bool TryCaptureMeasurementPoint(Point clickPoint)
    {
        if (!Viewport.FindNearest(clickPoint, out Point3D nearestPoint, out Vector3D _, out DependencyObject _))
        {
            return false;
        }

        if (_measurementPoints.Count == 2)
        {
            _measurementPoints.Clear();
        }

        _measurementPoints.Add(nearestPoint);
        UpdateMeasurementVisual();
        return true;
    }

    private void UpdateMeasurementVisual()
    {
        RemoveMeasurementVisuals();

        if (_measurementPoints.Count == 0)
        {
            MeasurementText.Text = MeasureModeButton.IsChecked == true ? "Pick 2 points" : "Measure off";
            return;
        }

        _measurementMarkers = new PointsVisual3D
        {
            Color = Colors.Gold,
            Size = 6,
            Points = new Point3DCollection(_measurementPoints)
        };
        Viewport.Children.Add(_measurementMarkers);

        if (_measurementPoints.Count == 1)
        {
            MeasurementText.Text = "Point 1 captured";
            return;
        }

        var edge = new StepGeometryExtractor.Edge(_measurementPoints[0], _measurementPoints[1]);
        _measurementLines = CreateLineVisual([edge], Colors.Gold, 3.0);
        Viewport.Children.Add(_measurementLines);

        double distance = (_measurementPoints[1] - _measurementPoints[0]).Length;
        MeasurementText.Text = $"Distance: {distance:F3}";
    }

    private void RemoveMeasurementVisuals()
    {
        for (int i = Viewport.Children.Count - 1; i >= 0; i--)
        {
            object child = Viewport.Children[i];
            if (_measurementLines is not null && ReferenceEquals(child, _measurementLines))
            {
                Viewport.Children.RemoveAt(i);
            }
            else if (_measurementMarkers is not null && ReferenceEquals(child, _measurementMarkers))
            {
                Viewport.Children.RemoveAt(i);
            }
        }

        _measurementLines = null;
        _measurementMarkers = null;
    }

    private static LinesVisual3D CreateLineVisual(
        IReadOnlyList<StepGeometryExtractor.Edge> edges,
        Color color,
        double thickness)
    {
        var points = new Point3DCollection(edges.Count * 2);
        foreach (StepGeometryExtractor.Edge edge in edges)
        {
            points.Add(edge.Start);
            points.Add(edge.End);
        }

        return new LinesVisual3D
        {
            Points = points,
            Color = color,
            Thickness = thickness
        };
    }

    private void ClearSceneVisuals()
    {
        for (int i = Viewport.Children.Count - 1; i >= 0; i--)
        {
            if (Viewport.Children[i] is DefaultLights)
            {
                continue;
            }

            if (Viewport.Children[i] is ModelVisual3D or LinesVisual3D or PointsVisual3D)
            {
                Viewport.Children.RemoveAt(i);
            }
        }

        _faceHitMap.Clear();
        _selectionModel = null;
        _selectionLines = null;
    }

    private void RemoveSelectionVisuals()
    {
        for (int i = Viewport.Children.Count - 1; i >= 0; i--)
        {
            object child = Viewport.Children[i];
            if (_selectionLines is not null && ReferenceEquals(child, _selectionLines))
            {
                Viewport.Children.RemoveAt(i);
            }
            else if (child is ModelVisual3D modelVisual &&
                     _selectionModel is not null &&
                     ReferenceEquals(modelVisual.Content, _selectionModel))
            {
                Viewport.Children.RemoveAt(i);
            }
        }

        _selectionModel = null;
        _selectionLines = null;
    }

    private void SetBusy(bool isBusy, string message)
    {
        BusyIndicator.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
    }

    private void UpdateEmptyPanels()
    {
        InfoPanel.ItemsSource = new[]
        {
            KV("File", "—"),
            KV("Schema", "—"),
            KV("Edition", "—"),
            KV("Entities", "0"),
            KV("Errors", "0"),
            KV("Warnings", "0")
        };

        EntityTypeList.ItemsSource = Array.Empty<EntityTypeItem>();
        StructureList.ItemsSource = Array.Empty<StructureItem>();
        PmiList.ItemsSource = Array.Empty<StructureItem>();
        DiagnosticsGrid.ItemsSource = Array.Empty<DiagnosticItem>();
        EntityList.ItemsSource = Array.Empty<EntityListItem>();
        EntityDetailPanel.ItemsSource = Array.Empty<KeyValuePair<string, string>>();
        OutboundReferenceList.ItemsSource = Array.Empty<EntityReferenceItem>();
        InboundReferenceList.ItemsSource = Array.Empty<EntityReferenceItem>();
        SelectedEntityRaw.Text = string.Empty;
        EntityListStatus.Text = "No document loaded.";
        DiagnosticsStatus.Text = "No diagnostics.";
        MeasurementText.Text = "Measure off";
        SectionText.Text = SectionControlHelper.FormatStatusText(SectionSlider.Value);
        RefreshRecentFilesMenu();
    }

    private void EnsureVisibleOnPrimaryWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        if (!WindowPlacementHelper.NeedsPrimaryWorkAreaReset(Left, Top, Width, Height, workArea))
        {
            return;
        }

        Point centered = WindowPlacementHelper.GetCenteredPosition(Width, Height, workArea);
        Left = centered.X;
        Top = centered.Y;
        RuntimeLog.Info($"Window moved to primary work area at ({Left:F0}, {Top:F0}).");
    }

    private HashSet<int> GetSelectedEntityIds() =>
        EntityList.SelectedItems.Cast<EntityListItem>().Select(item => item.Id).ToHashSet();

    private bool IsEntityVisible(int entityId)
    {
        if (_hiddenEntityIds.Contains(entityId))
        {
            return false;
        }

        if (_isolatedEntityIds is not null && !_isolatedEntityIds.Contains(entityId))
        {
            return false;
        }

        if (_document is null || SectionEnabledCheckBox.IsChecked != true)
        {
            return true;
        }

        if (!_document.EntityCenters.TryGetValue(entityId, out Point3D center))
        {
            return true;
        }

        var centers = _document.EntityCenters.Values.ToList();
        if (centers.Count == 0)
        {
            return true;
        }

        char axis = ((SectionAxisComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "X")[0];
        double value = GetAxisValue(center, axis);
        double min = centers.Min(point => GetAxisValue(point, axis));
        double max = centers.Max(point => GetAxisValue(point, axis));
        double threshold = min + ((max - min) * (SectionSlider.Value / 100.0));
        return value <= threshold;
    }

    private void RerenderIfLoaded()
    {
        if (_document is not null)
        {
            Render(_document);
        }
    }

    private void AddRecentFile(string path)
    {
        List<string> recent = _settings.RecentFiles
            .Where(existing => !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            .Prepend(path)
            .Take(8)
            .ToList();

        _settings = _settings with
        {
            RecentFiles = recent,
            LastOpenedFile = path
        };

        RefreshRecentFilesMenu();
    }

    private void RefreshRecentFilesMenu()
    {
        RecentFilesMenu.Items.Clear();

        List<string> recentFiles = _settings.RecentFiles.Where(File.Exists).ToList();
        if (recentFiles.Count == 0)
        {
            RecentFilesMenu.Items.Add(new MenuItem
            {
                Header = "(empty)",
                IsEnabled = false
            });
        }
        else
        {
            foreach (string path in recentFiles)
            {
                MenuItem item = new()
                {
                    Header = path,
                    Tag = path
                };
                item.Click += RecentFile_Click;
                RecentFilesMenu.Items.Add(item);
            }
        }
    }

    private void ApplyProjectionSetting(bool useOrthographic)
    {
        if (Viewport.Camera is not ProjectionCamera current)
        {
            return;
        }

        if (useOrthographic && current is PerspectiveCamera perspective)
        {
            double distance = perspective.LookDirection.Length;
            double width = Math.Max(1.0, distance * Math.Tan(perspective.FieldOfView * Math.PI / 360.0) * 2.0);
            Viewport.Camera = new OrthographicCamera
            {
                Position = perspective.Position,
                LookDirection = perspective.LookDirection,
                UpDirection = perspective.UpDirection,
                NearPlaneDistance = perspective.NearPlaneDistance,
                FarPlaneDistance = perspective.FarPlaneDistance,
                Width = width
            };
        }
        else if (!useOrthographic && current is OrthographicCamera ortho)
        {
            Viewport.Camera = new PerspectiveCamera
            {
                Position = ortho.Position,
                LookDirection = ortho.LookDirection,
                UpDirection = ortho.UpDirection,
                NearPlaneDistance = ortho.NearPlaneDistance,
                FarPlaneDistance = ortho.FarPlaneDistance,
                FieldOfView = 45
            };
        }

        ProjectionToggleMenu.IsChecked = Viewport.Camera is OrthographicCamera;
    }

    private void SetStandardView(Vector3D direction, Vector3D upDirection)
    {
        if (Viewport.Camera is not ProjectionCamera camera)
        {
            return;
        }

        Point3D target = camera.Position + camera.LookDirection;
        double distance = camera.LookDirection.Length;
        Vector3D lookDirection = direction;
        lookDirection.Normalize();
        lookDirection *= distance;
        camera.Position = target - lookDirection;
        camera.LookDirection = lookDirection;
        camera.UpDirection = upDirection;
    }

    private CameraBookmark? CaptureCameraBookmark()
    {
        if (Viewport is null || Viewport.Camera is not ProjectionCamera camera)
        {
            return null;
        }

        return new CameraBookmark(
            camera.Position.X,
            camera.Position.Y,
            camera.Position.Z,
            camera.LookDirection.X,
            camera.LookDirection.Y,
            camera.LookDirection.Z,
            camera.UpDirection.X,
            camera.UpDirection.Y,
            camera.UpDirection.Z,
            camera is OrthographicCamera ortho ? ortho.Width : null);
    }

    private void ApplySavedCamera(CameraBookmark? bookmark)
    {
        if (bookmark is null || Viewport.Camera is not ProjectionCamera camera)
        {
            return;
        }

        camera.Position = new Point3D(bookmark.PositionX, bookmark.PositionY, bookmark.PositionZ);
        camera.LookDirection = new Vector3D(bookmark.LookDirectionX, bookmark.LookDirectionY, bookmark.LookDirectionZ);
        camera.UpDirection = new Vector3D(bookmark.UpDirectionX, bookmark.UpDirectionY, bookmark.UpDirectionZ);

        if (camera is OrthographicCamera ortho && bookmark.Width.HasValue)
        {
            ortho.Width = bookmark.Width.Value;
        }
    }

    private static int? ExtractEntityId(string message)
    {
        Match match = EntityIdRegex.Match(message);
        return match.Success && int.TryParse(match.Groups["id"].Value, out int entityId)
            ? entityId
            : null;
    }

    private static double GetAxisValue(Point3D point, char axis) => axis switch
    {
        'X' => point.X,
        'Y' => point.Y,
        'Z' => point.Z,
        _ => point.X
    };

    private static Material MakeMaterial(Color? faceColor, double opacity)
    {
        Color baseColor = faceColor ?? DefaultFaceColor;
        byte alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        var diffuse = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        var specular = new SolidColorBrush(Color.FromArgb((byte)(alpha * 200 / 255), 255, 255, 255));

        return new MaterialGroup
        {
            Children =
            {
                new DiffuseMaterial(diffuse),
                new SpecularMaterial(specular, 60)
            }
        };
    }

    private static KeyValuePair<string, string> KV(string key, string value) => new(key, value);

    private static string Shorten(string? schema)
    {
        if (schema is null)
        {
            return "—";
        }

        int brace = schema.IndexOf('{');
        return brace > 0 ? schema[..brace].Trim() : schema;
    }
}
