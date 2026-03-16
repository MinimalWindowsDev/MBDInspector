using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using StepParser.Parser;

namespace MBDInspector;

internal static class StepParameterFormatter
{
    public static string FormatEntity(EntityInstance entity) =>
        entity.IsComplex
            ? $"#{entity.Id} = {FormatComplexEntity(entity)};"
            : $"#{entity.Id} = {entity.Name ?? "<unknown>"}({FormatParameters(entity.Parameters)});";

    public static string CompactPreview(EntityInstance entity, int maxLength = 120)
    {
        string text = entity.IsComplex
            ? FormatComplexEntity(entity)
            : $"{entity.Name ?? "<unknown>"}({FormatParameters(entity.Parameters)})";

        text = text.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return text.Length <= maxLength ? text : $"{text[..(maxLength - 1)]}…";
    }

    public static int CountReferences(EntityInstance entity)
    {
        int total = entity.Parameters.Sum(CountReferences);
        if (entity.Components is null)
        {
            return total;
        }

        return total + entity.Components.Sum(component => component.Parameters.Sum(CountReferences));
    }

    public static IEnumerable<int> EnumerateReferences(EntityInstance entity)
    {
        foreach (Parameter parameter in entity.Parameters)
        {
            foreach (int id in EnumerateReferences(parameter))
            {
                yield return id;
            }
        }

        if (entity.Components is null)
        {
            yield break;
        }

        foreach (EntityComponent component in entity.Components)
        {
            foreach (Parameter parameter in component.Parameters)
            {
                foreach (int id in EnumerateReferences(parameter))
                {
                    yield return id;
                }
            }
        }
    }

    public static IReadOnlyList<KeyValuePair<string, string>> DescribeEntity(EntityInstance entity)
    {
        var items = new List<KeyValuePair<string, string>>();

        switch ((entity.Name ?? string.Empty).ToUpperInvariant())
        {
            case "CARTESIAN_POINT":
                if (TryGetCartesianPoint(entity, out Point3D point))
                {
                    items.Add(new("XYZ", $"{point.X:G}, {point.Y:G}, {point.Z:G}"));
                }
                break;
            case "ADVANCED_FACE":
                items.Add(new("Role", "Boundary representation face"));
                break;
            case "EDGE_CURVE":
                items.Add(new("Role", "Boundary representation edge"));
                break;
            case "PRODUCT":
                if (entity.Parameters.Count > 1)
                {
                    items.Add(new("Name", FormatParameter(entity.Parameters[0])));
                    items.Add(new("Description", FormatParameter(entity.Parameters[1])));
                }
                break;
            case "SHAPE_REPRESENTATION":
                items.Add(new("Role", "Shape representation root"));
                break;
        }

        if (entity.Name?.Contains("DATUM", StringComparison.OrdinalIgnoreCase) == true ||
            entity.Name?.Contains("TOLER", StringComparison.OrdinalIgnoreCase) == true ||
            entity.Name?.Contains("DIMENSION", StringComparison.OrdinalIgnoreCase) == true)
        {
            items.Add(new("PMI", "Likely semantic or presentation manufacturing annotation"));
        }

        return items;
    }

    private static string FormatComplexEntity(EntityInstance entity)
    {
        if (entity.Components is null || entity.Components.Count == 0)
        {
            return entity.Name ?? "<complex>";
        }

        return string.Join(" ",
            entity.Components.Select(component =>
                $"{component.Name}({FormatParameters(component.Parameters)})"));
    }

    private static string FormatParameters(IReadOnlyList<Parameter> parameters) =>
        string.Join(", ", parameters.Select(FormatParameter));

    private static string FormatParameter(Parameter parameter) => parameter switch
    {
        Parameter.EntityReference entityReference => $"#{entityReference.Id}",
        Parameter.StringValue stringValue => $"'{stringValue.Value}'",
        Parameter.IntegerValue integerValue => integerValue.Value.ToString(),
        Parameter.RealValue realValue => realValue.Value.ToString("G"),
        Parameter.BinaryValue binaryValue => $"\"{binaryValue.Value}\"",
        Parameter.EnumValue enumValue => $".{enumValue.Name}.",
        Parameter.ListValue listValue => $"({string.Join(", ", listValue.Items.Select(FormatParameter))})",
        Parameter.UnsetValue => "$",
        Parameter.InheritedValue => "*",
        Parameter.TypedValue typedValue => $"{typedValue.TypeName}({FormatParameter(typedValue.Inner)})",
        _ => "?"
    };

    private static int CountReferences(Parameter parameter) => parameter switch
    {
        Parameter.EntityReference => 1,
        Parameter.ListValue listValue => listValue.Items.Sum(CountReferences),
        Parameter.TypedValue typedValue => CountReferences(typedValue.Inner),
        _ => 0
    };

    private static IEnumerable<int> EnumerateReferences(Parameter parameter)
    {
        switch (parameter)
        {
            case Parameter.EntityReference entityReference:
                yield return entityReference.Id;
                break;
            case Parameter.ListValue listValue:
                foreach (Parameter item in listValue.Items)
                {
                    foreach (int id in EnumerateReferences(item))
                    {
                        yield return id;
                    }
                }
                break;
            case Parameter.TypedValue typedValue:
                foreach (int id in EnumerateReferences(typedValue.Inner))
                {
                    yield return id;
                }
                break;
        }
    }

    private static bool TryGetCartesianPoint(EntityInstance entity, out Point3D point)
    {
        point = default;
        if (entity.Parameters.Count < 2 || entity.Parameters[1] is not Parameter.ListValue coords || coords.Items.Count < 2)
        {
            return false;
        }

        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = coords.Items.Count >= 3 ? ToDouble(coords.Items[2]) : 0.0;
        point = new Point3D(x, y, z);
        return true;
    }

    private static double ToDouble(Parameter parameter) => parameter switch
    {
        Parameter.RealValue realValue => realValue.Value,
        Parameter.IntegerValue integerValue => integerValue.Value,
        _ => 0.0
    };
}
