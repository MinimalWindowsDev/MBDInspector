using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using StepParser.Parser;

namespace MBDInspector;

/// <summary>
/// Resolves STEP presentation colours and propagates inherited colours down to faces.
/// </summary>
public static class StepColorExtractor
{
    private const int MaxColorInheritanceDepth = 8;

    public static Dictionary<int, Color> Extract(
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        Dictionary<int, Color> directStyles = ExtractDirectStyles(data);
        Dictionary<int, IReadOnlyList<int>> inboundReferences = BuildInboundReferences(data);
        var resolved = new Dictionary<int, Color>(directStyles);

        foreach ((int entityId, EntityInstance entity) in data)
        {
            if (!IsNamed(entity, "ADVANCED_FACE") || resolved.ContainsKey(entityId))
            {
                continue;
            }

            if (TryResolveInheritedColor(entityId, directStyles, inboundReferences, out Color color))
            {
                resolved[entityId] = color;
            }
        }

        return resolved;
    }

    private static Dictionary<int, Color> ExtractDirectStyles(
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        var map = new Dictionary<int, Color>();

        foreach ((_, EntityInstance entity) in data)
        {
            if (!IsNamed(entity, "STYLED_ITEM") || entity.Parameters.Count < 3)
            {
                continue;
            }

            if (entity.Parameters[2] is not Parameter.EntityReference itemRef ||
                entity.Parameters[1] is not Parameter.ListValue psaList)
            {
                continue;
            }

            foreach (Parameter psaRef in psaList.Items)
            {
                Color? color = ResolvePsa(psaRef, data);
                if (color.HasValue)
                {
                    map[itemRef.Id] = color.Value;
                    break;
                }
            }
        }

        return map;
    }

    private static bool TryResolveInheritedColor(
        int entityId,
        IReadOnlyDictionary<int, Color> directStyles,
        IReadOnlyDictionary<int, IReadOnlyList<int>> inboundReferences,
        out Color color)
    {
        color = default;
        var visited = new HashSet<int> { entityId };
        var queue = new Queue<(int Id, int Depth)>();
        queue.Enqueue((entityId, 0));

        while (queue.Count > 0)
        {
            (int currentId, int depth) = queue.Dequeue();
            if (depth >= MaxColorInheritanceDepth ||
                !inboundReferences.TryGetValue(currentId, out IReadOnlyList<int>? referrers))
            {
                continue;
            }

            foreach (int referrerId in referrers)
            {
                if (!visited.Add(referrerId))
                {
                    continue;
                }

                if (directStyles.TryGetValue(referrerId, out color))
                {
                    return true;
                }

                queue.Enqueue((referrerId, depth + 1));
            }
        }

        return false;
    }

    private static Dictionary<int, IReadOnlyList<int>> BuildInboundReferences(
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        var inbound = new Dictionary<int, HashSet<int>>();

        foreach ((int sourceId, EntityInstance entity) in data)
        {
            foreach (int targetId in EnumerateReferences(entity.Parameters))
            {
                if (!inbound.TryGetValue(targetId, out HashSet<int>? referrers))
                {
                    referrers = [];
                    inbound[targetId] = referrers;
                }

                referrers.Add(sourceId);
            }
        }

        return inbound.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<int>)pair.Value.OrderBy(id => id).ToList());
    }

    private static IEnumerable<int> EnumerateReferences(IEnumerable<Parameter> parameters)
    {
        foreach (Parameter parameter in parameters)
        {
            switch (parameter)
            {
                case Parameter.EntityReference entityReference:
                    yield return entityReference.Id;
                    break;

                case Parameter.ListValue listValue:
                    foreach (int item in EnumerateReferences(listValue.Items))
                    {
                        yield return item;
                    }
                    break;

                case Parameter.TypedValue typedValue:
                    foreach (int item in EnumerateReferences([typedValue.Inner]))
                    {
                        yield return item;
                    }
                    break;
            }
        }
    }

    private static Color? ResolvePsa(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!TryGetEntity(param, data, "PRESENTATION_STYLE_ASSIGNMENT", out EntityInstance? psa) ||
            psa!.Parameters.Count < 1 ||
            psa.Parameters[0] is not Parameter.ListValue styleList)
        {
            return null;
        }

        foreach (Parameter styleRef in styleList.Items)
        {
            Color? color = ResolveSurfaceStyleUsage(styleRef, data);
            if (color.HasValue)
            {
                return color;
            }
        }

        return null;
    }

    private static Color? ResolveSurfaceStyleUsage(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!TryGetEntity(param, data, "SURFACE_STYLE_USAGE", out EntityInstance? styleUsage) ||
            styleUsage!.Parameters.Count < 2)
        {
            return null;
        }

        return ResolveSurfaceSideStyle(styleUsage.Parameters[1], data);
    }

    private static Color? ResolveSurfaceSideStyle(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!TryGetEntity(param, data, "SURFACE_SIDE_STYLE", out EntityInstance? sideStyle) ||
            sideStyle!.Parameters.Count < 2 ||
            sideStyle.Parameters[1] is not Parameter.ListValue fillAreaList)
        {
            return null;
        }

        foreach (Parameter fillAreaRef in fillAreaList.Items)
        {
            Color? color = ResolveSurfaceStyleFillArea(fillAreaRef, data);
            if (color.HasValue)
            {
                return color;
            }
        }

        return null;
    }

    private static Color? ResolveSurfaceStyleFillArea(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!TryGetEntity(param, data, "SURFACE_STYLE_FILL_AREA", out EntityInstance? fillArea) ||
            fillArea!.Parameters.Count < 1)
        {
            return null;
        }

        return ResolveFillAreaStyle(fillArea.Parameters[0], data);
    }

    private static Color? ResolveFillAreaStyle(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!TryGetEntity(param, data, "FILL_AREA_STYLE", out EntityInstance? style) ||
            style!.Parameters.Count < 2 ||
            style.Parameters[1] is not Parameter.ListValue styleItems)
        {
            return null;
        }

        foreach (Parameter styleItem in styleItems.Items)
        {
            Color? color = ResolveFillAreaStyleColour(styleItem, data);
            if (color.HasValue)
            {
                return color;
            }
        }

        return null;
    }

    private static Color? ResolveFillAreaStyleColour(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!TryGetEntity(param, data, "FILL_AREA_STYLE_COLOUR", out EntityInstance? colourStyle) ||
            colourStyle!.Parameters.Count < 2)
        {
            return null;
        }

        return ResolveColourDefinition(colourStyle.Parameters[1], data);
    }

    private static Color? ResolveColourDefinition(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? colorEntity))
        {
            return null;
        }

        if (IsNamed(colorEntity, "COLOUR_RGB"))
        {
            if (colorEntity.Parameters.Count < 4)
            {
                return null;
            }

            return Color.FromRgb(
                ToByte(colorEntity.Parameters[1]),
                ToByte(colorEntity.Parameters[2]),
                ToByte(colorEntity.Parameters[3]));
        }

        if ((IsNamed(colorEntity, "DRAUGHTING_PRE_DEFINED_COLOUR") ||
             IsNamed(colorEntity, "PRE_DEFINED_COLOUR")) &&
            colorEntity.Parameters.Count >= 1 &&
            colorEntity.Parameters[0] is Parameter.StringValue namedColor)
        {
            return ResolveNamedColor(namedColor.Value);
        }

        return null;
    }

    private static Color? ResolveNamedColor(string value) => value.Trim().ToLowerInvariant() switch
    {
        "black" => Colors.Black,
        "white" => Colors.White,
        "red" => Colors.Red,
        "green" => Colors.Green,
        "blue" => Colors.Blue,
        "yellow" => Colors.Yellow,
        "cyan" => Colors.Cyan,
        "magenta" => Colors.Magenta,
        "grey" or "gray" => Colors.Gray,
        _ => null
    };

    private static bool TryGetEntity(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data,
        string expectedName,
        out EntityInstance? entity)
    {
        entity = null;
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out entity))
        {
            return false;
        }

        return IsNamed(entity, expectedName);
    }

    private static byte ToByte(Parameter parameter)
    {
        double value = parameter switch
        {
            Parameter.RealValue realValue => realValue.Value,
            Parameter.IntegerValue integerValue => integerValue.Value,
            _ => 0.0
        };

        return (byte)Math.Clamp(Math.Round(value * 255.0), 0, 255);
    }

    private static bool IsNamed(EntityInstance entity, string name) =>
        string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase) ||
        (entity.Components?.Any(component => string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false);
}
