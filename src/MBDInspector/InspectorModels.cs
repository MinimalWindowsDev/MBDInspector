using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StepParser.Diagnostics;
using StepParser.Parser;

namespace MBDInspector;

internal sealed record LoadedDocument(
    string Path,
    StepFile File,
    IReadOnlyList<ParseDiagnostic> Diagnostics,
    IReadOnlyList<EntityListItem> Entities,
    IReadOnlyList<EntityTypeItem> EntityTypes,
    IReadOnlyList<StructureItem> StructureItems,
    IReadOnlyList<StructureItem> PmiItems,
    IReadOnlyDictionary<int, IReadOnlyList<int>> OutboundReferences,
    IReadOnlyDictionary<int, IReadOnlyList<int>> InboundReferences,
    IReadOnlyDictionary<int, Point3D> EntityCenters);

internal sealed record EntityListItem(
    int Id,
    string Name,
    string Header,
    string Preview);

internal sealed record EntityTypeItem(
    string Name,
    int Count);

internal sealed record DiagnosticItem(
    string Severity,
    int Line,
    int Column,
    string Message,
    int? EntityId = null);

internal sealed record EntityReferenceItem(
    int Id,
    string Header,
    string Preview);

internal sealed record FaceMeshItem(
    int EntityId,
    MeshGeometry3D Mesh,
    Color? FaceColor);

internal sealed record StructureItem(
    int Id,
    string Header,
    string Preview,
    string Category);

internal sealed record AppSettings(
    IReadOnlyList<string> RecentFiles,
    string? LastOpenedFile,
    CameraBookmark? LastCamera,
    bool UseOrthographicCamera);

internal sealed record CameraBookmark(
    double PositionX,
    double PositionY,
    double PositionZ,
    double LookDirectionX,
    double LookDirectionY,
    double LookDirectionZ,
    double UpDirectionX,
    double UpDirectionY,
    double UpDirectionZ,
    double? Width);
