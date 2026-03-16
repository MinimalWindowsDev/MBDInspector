using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Media3D;
using StepParser.Diagnostics;
using StepParser.Lexer;
using StepParser.Parser;

namespace MBDInspector;

internal static class StepDocumentBuilder
{
    public static LoadedDocument Load(string path)
    {
        var diagnostics = new List<ParseDiagnostic>();
        var raw = Tokenizer.TokenizeFile(path, diagnostics);
        var tokens = StepParser.Lexer.Lexer.Lex(raw, diagnostics);
        StepFile file = new StepFileParser(tokens, diagnostics).Parse(path);

        List<EntityListItem> entities = file.Data.Values
            .OrderBy(entity => entity.Id)
            .Select(entity => new EntityListItem(
                entity.Id,
                entity.Name ?? "<complex>",
                $"#{entity.Id}  {entity.Name ?? "<complex>"}",
                StepParameterFormatter.CompactPreview(entity)))
            .ToList();

        List<EntityTypeItem> entityTypes = file.Data.Values
            .GroupBy(entity => entity.Name ?? "<complex>", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EntityTypeItem(group.Key, group.Count()))
            .ToList();

        List<StructureItem> structureItems = file.Data.Values
            .Where(entity => IsStructureEntity(entity.Name))
            .OrderBy(entity => StructureCategory(entity.Name))
            .ThenBy(entity => entity.Id)
            .Select(entity => new StructureItem(
                entity.Id,
                $"#{entity.Id}  {entity.Name ?? "<complex>"}",
                StepParameterFormatter.CompactPreview(entity),
                StructureCategory(entity.Name)))
            .ToList();

        List<StructureItem> pmiItems = file.Data.Values
            .Where(entity => IsPmiEntity(entity.Name))
            .OrderBy(entity => entity.Id)
            .Select(entity => new StructureItem(
                entity.Id,
                $"#{entity.Id}  {entity.Name ?? "<complex>"}",
                StepParameterFormatter.CompactPreview(entity),
                "PMI"))
            .ToList();

        BuildReferenceGraph(file.Data, out Dictionary<int, IReadOnlyList<int>> outbound, out Dictionary<int, IReadOnlyList<int>> inbound);
        Dictionary<int, Point3D> centers = BuildEntityCenters(file.Data);
        Dictionary<int, System.Windows.Media.Color> colorMap = StepColorExtractor.Extract(file.Data);
        List<FaceMeshItem> faceMeshes = StepTessellator.TessellateFaces(file.Data, colorMap);
        Dictionary<int, FaceMeshItem> faceMeshLookup = faceMeshes.ToDictionary(item => item.EntityId);
        IReadOnlyList<StepGeometryExtractor.Edge> allEdges = StepGeometryExtractor.Extract(file.Data);
        LogFaceBuildFailures(file.Data, faceMeshLookup);

        return new LoadedDocument(
            Path.GetFullPath(path),
            file,
            diagnostics,
            entities,
            entityTypes,
            structureItems,
            pmiItems,
            outbound,
            inbound,
            centers,
            colorMap,
            faceMeshes,
            faceMeshLookup,
            allEdges);
    }

    private static void BuildReferenceGraph(
        IReadOnlyDictionary<int, EntityInstance> data,
        out Dictionary<int, IReadOnlyList<int>> outbound,
        out Dictionary<int, IReadOnlyList<int>> inbound)
    {
        var outboundMutable = new Dictionary<int, HashSet<int>>();
        var inboundMutable = new Dictionary<int, HashSet<int>>();

        foreach ((int id, EntityInstance entity) in data)
        {
            HashSet<int> outboundRefs = StepParameterFormatter.EnumerateReferences(entity).ToHashSet();
            outboundMutable[id] = outboundRefs;

            foreach (int targetId in outboundRefs)
            {
                if (!inboundMutable.TryGetValue(targetId, out HashSet<int>? referrers))
                {
                    referrers = [];
                    inboundMutable[targetId] = referrers;
                }

                referrers.Add(id);
            }
        }

        outbound = outboundMutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.OrderBy(id => id).ToList());

        inbound = inboundMutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.OrderBy(id => id).ToList());
    }

    private static Dictionary<int, Point3D> BuildEntityCenters(IReadOnlyDictionary<int, EntityInstance> data)
    {
        var centers = new Dictionary<int, Point3D>();
        foreach ((int id, EntityInstance entity) in data)
        {
            if (StepGeometryExtractor.TryGetEntityCenter(id, data, out Point3D center))
            {
                centers[id] = center;
            }
        }

        return centers;
    }

    private static bool IsStructureEntity(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return name.Contains("PRODUCT", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ASSEMB", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("SHAPE", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("REPRESENTATION", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("BREP", StringComparison.OrdinalIgnoreCase);
    }

    private static string StructureCategory(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Other";
        }

        if (name.Contains("PRODUCT", StringComparison.OrdinalIgnoreCase))
        {
            return "Product";
        }

        if (name.Contains("SHAPE", StringComparison.OrdinalIgnoreCase))
        {
            return "Shape";
        }

        if (name.Contains("REPRESENTATION", StringComparison.OrdinalIgnoreCase))
        {
            return "Representation";
        }

        if (name.Contains("BREP", StringComparison.OrdinalIgnoreCase))
        {
            return "Geometry";
        }

        return "Other";
    }

    private static bool IsPmiEntity(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string[] keywords =
        [
            "DATUM", "TOLER", "DIMENSION", "ANNOT", "PRESENTATION", "GEOMETRIC", "SURFACE_STYLE", "STYLE"
        ];

        return keywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static void LogFaceBuildFailures(
        IReadOnlyDictionary<int, EntityInstance> data,
        IReadOnlyDictionary<int, FaceMeshItem> faceMeshLookup)
    {
        List<StepTessellator.FaceBuildFailure> failures = data.Values
            .Where(entity => string.Equals(entity.Name, "ADVANCED_FACE", StringComparison.OrdinalIgnoreCase))
            .Where(entity => !faceMeshLookup.ContainsKey(entity.Id))
            .Select(entity => StepTessellator.DescribeFaceFailure(entity.Id, data))
            .Where(failure => failure is not null)
            .Select(failure => failure!.Value)
            .ToList();

        if (failures.Count == 0)
        {
            return;
        }

        RuntimeLog.Info($"Skipped {failures.Count} face(s) during tessellation.");
        foreach (StepTessellator.FaceBuildFailure failure in failures.Take(20))
        {
            RuntimeLog.Info($"Skipped face #{failure.FaceId} [{failure.SurfaceType}]: {failure.Reason}");
        }
    }
}
