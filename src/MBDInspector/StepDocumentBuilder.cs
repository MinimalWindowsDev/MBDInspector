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

        List<PmiDetail> pmiDetails = ExtractPmiDetails(file.Data);

        BuildReferenceGraph(file.Data, out Dictionary<int, IReadOnlyList<int>> outbound, out Dictionary<int, IReadOnlyList<int>> inbound);
        Dictionary<int, Point3D> centers = BuildEntityCenters(file.Data);
        Dictionary<int, StepColorExtractor.AppearanceInfo> appearances = StepColorExtractor.ExtractAppearance(file.Data);
        Dictionary<int, System.Windows.Media.Color> colorMap = appearances
            .Where(pair => pair.Value.Color.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Color!.Value);
        Dictionary<int, double> opacityMap = appearances
            .Where(pair => pair.Value.Opacity < 1.0)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Opacity);
        List<FaceMeshItem> faceMeshes = StepTessellator.TessellateFaces(file.Data, colorMap, opacityMap);
        Dictionary<int, FaceMeshItem> faceMeshLookup = faceMeshes.ToDictionary(item => item.EntityId);
        IReadOnlyList<StepGeometryExtractor.Edge> allEdges = StepGeometryExtractor.Extract(file.Data);
        LogFaceBuildFailures(file.Data, faceMeshLookup);

        return new LoadedDocument(
            Path.GetFullPath(path),
            file,
            ExtractLengthUnit(file.Data),
            diagnostics,
            entities,
            entityTypes,
            structureItems,
            pmiItems,
            pmiDetails,
            outbound,
            inbound,
            centers,
            colorMap,
            opacityMap,
            faceMeshes,
            faceMeshLookup,
            allEdges);
    }

    internal static string ExtractLengthUnit(IReadOnlyDictionary<int, EntityInstance> data)
    {
        foreach (EntityInstance entity in data.Values.OrderBy(item => item.Id))
        {
            IReadOnlyList<Parameter>? siParameters = TryGetParameters(entity, "SI_UNIT");
            if (siParameters is not null)
            {
                string prefix = siParameters.Count > 0 && siParameters[0] is Parameter.EnumValue prefixValue
                    ? prefixValue.Name
                    : string.Empty;
                string unitName = siParameters.Count > 1 && siParameters[1] is Parameter.EnumValue unitValue
                    ? unitValue.Name
                    : string.Empty;

                if (string.Equals(unitName, "METRE", StringComparison.OrdinalIgnoreCase))
                {
                    return string.Equals(prefix, "MILLI", StringComparison.OrdinalIgnoreCase) ? "mm" : "m";
                }
            }

            IReadOnlyList<Parameter>? conversionParameters = TryGetParameters(entity, "CONVERSION_BASED_UNIT");
            if (conversionParameters is not null &&
                conversionParameters.Count > 0 &&
                conversionParameters[0] is Parameter.StringValue unitNameValue &&
                unitNameValue.Value.Contains("inch", StringComparison.OrdinalIgnoreCase))
            {
                return "in";
            }
        }

        return "mm";
    }

    internal static List<PmiDetail> ExtractPmiDetails(IReadOnlyDictionary<int, EntityInstance> data)
    {
        var result = new List<PmiDetail>();
        foreach (EntityInstance entity in data.Values.OrderBy(item => item.Id))
        {
            if (!IsNamed(entity, "DIMENSIONAL_SIZE"))
            {
                continue;
            }

            string value = FindFirstValueText(entity, data) ?? StepParameterFormatter.CompactPreview(entity, 80);
            string? tolerance = FindToleranceText(entity, data);
            result.Add(new PmiDetail(entity.Id, entity.Name ?? "DIMENSIONAL_SIZE", value, tolerance));
        }

        return result;
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

    private static bool IsNamed(EntityInstance entity, string name) =>
        string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase) ||
        (entity.Components?.Any(component => string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static IReadOnlyList<Parameter>? TryGetParameters(EntityInstance entity, string entityName)
    {
        EntityComponent? component = entity.Components?
            .FirstOrDefault(candidate => string.Equals(candidate.Name, entityName, StringComparison.OrdinalIgnoreCase));
        if (component is not null)
        {
            return component.Parameters;
        }

        return IsNamed(entity, entityName) ? entity.Parameters : null;
    }

    private static string? FindFirstValueText(EntityInstance entity, IReadOnlyDictionary<int, EntityInstance> data)
    {
        foreach (Parameter parameter in entity.Parameters)
        {
            if (parameter is Parameter.StringValue stringValue && !string.IsNullOrWhiteSpace(stringValue.Value))
            {
                return stringValue.Value;
            }

            if (TryGetNumericValue(parameter, data, new HashSet<int>(), out double value))
            {
                return value.ToString("G");
            }
        }

        return null;
    }

    private static string? FindToleranceText(EntityInstance entity, IReadOnlyDictionary<int, EntityInstance> data)
    {
        HashSet<int> neighborhood = StepParameterFormatter.EnumerateReferences(entity).ToHashSet();
        neighborhood.Add(entity.Id);

        foreach (EntityInstance candidate in data.Values.OrderBy(item => item.Id))
        {
            if (!IsNamed(candidate, "VALUE_RANGE"))
            {
                continue;
            }

            if (!StepParameterFormatter.EnumerateReferences(candidate).Any(referenceId => neighborhood.Contains(referenceId)) &&
                candidate.Id != entity.Id)
            {
                continue;
            }

            List<double> values = candidate.Parameters
                .SelectMany(FlattenParameters)
                .Select(TryConvertNumeric)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Take(2)
                .ToList();

            if (values.Count >= 2)
            {
                return $"{values[0]:G} .. {values[1]:G}";
            }
        }

        return null;
    }

    private static bool TryGetNumericValue(
        Parameter parameter,
        IReadOnlyDictionary<int, EntityInstance> data,
        HashSet<int> visited,
        out double value)
    {
        switch (parameter)
        {
            case Parameter.RealValue realValue:
                value = realValue.Value;
                return true;
            case Parameter.IntegerValue integerValue:
                value = integerValue.Value;
                return true;
            case Parameter.EntityReference entityReference when data.TryGetValue(entityReference.Id, out EntityInstance? referenced) && visited.Add(entityReference.Id):
                foreach (Parameter referencedParameter in referenced.Parameters)
                {
                    if (TryGetNumericValue(referencedParameter, data, visited, out value))
                    {
                        return true;
                    }
                }

                if (referenced.Components is not null)
                {
                    foreach (EntityComponent component in referenced.Components)
                    {
                        foreach (Parameter componentParameter in component.Parameters)
                        {
                            if (TryGetNumericValue(componentParameter, data, visited, out value))
                            {
                                return true;
                            }
                        }
                    }
                }

                break;
            case Parameter.ListValue listValue:
                foreach (Parameter item in listValue.Items)
                {
                    if (TryGetNumericValue(item, data, visited, out value))
                    {
                        return true;
                    }
                }
                break;
            case Parameter.TypedValue typedValue:
                if (TryGetNumericValue(typedValue.Inner, data, visited, out value))
                {
                    return true;
                }
                break;
        }

        value = 0;
        return false;
    }

    private static IEnumerable<Parameter> FlattenParameters(Parameter parameter)
    {
        yield return parameter;
        switch (parameter)
        {
            case Parameter.ListValue listValue:
                foreach (Parameter item in listValue.Items.SelectMany(FlattenParameters))
                {
                    yield return item;
                }
                break;
            case Parameter.TypedValue typedValue:
                foreach (Parameter item in FlattenParameters(typedValue.Inner))
                {
                    yield return item;
                }
                break;
        }
    }

    private static double? TryConvertNumeric(Parameter parameter) => parameter switch
    {
        Parameter.RealValue realValue => realValue.Value,
        Parameter.IntegerValue integerValue => integerValue.Value,
        _ => null
    };

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


