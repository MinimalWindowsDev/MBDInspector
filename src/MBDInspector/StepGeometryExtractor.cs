using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using StepParser.Parser;

namespace MBDInspector;

public static class StepGeometryExtractor
{
    public readonly record struct Edge(Point3D Start, Point3D End);

    public static IReadOnlyList<Edge> Extract(IReadOnlyDictionary<int, EntityInstance> data)
    {
        var edges = new List<Edge>();

        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "EDGE_CURVE") || entity.Parameters.Count < 3)
            {
                continue;
            }

            Point3D? start = ResolveVertexPoint(entity.Parameters[1], data);
            Point3D? end = ResolveVertexPoint(entity.Parameters[2], data);
            if (start.HasValue && end.HasValue)
            {
                edges.Add(new Edge(start.Value, end.Value));
            }
        }

        if (edges.Count > 0)
        {
            return edges;
        }

        foreach (var (_, entity) in data)
        {
            if (IsNamed(entity, "POLY_LINE"))
            {
                AppendPolyLineEdges(entity, data, edges);
            }
        }

        if (edges.Count > 0)
        {
            return edges;
        }

        var points = new List<Point3D>();
        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "CARTESIAN_POINT"))
            {
                continue;
            }

            Point3D? point = ExtractCoords(entity);
            if (point.HasValue)
            {
                points.Add(point.Value);
            }
        }

        for (int i = 0; i + 1 < points.Count; i += 2)
        {
            edges.Add(new Edge(points[i], points[i + 1]));
        }

        return edges;
    }

    public static IReadOnlyList<Edge> ExtractEntityEdges(
        int entityId,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(entityId, out EntityInstance? entity))
        {
            return Array.Empty<Edge>();
        }

        var edges = new List<Edge>();
        if (IsNamed(entity, "EDGE_CURVE"))
        {
            Point3D? start = entity.Parameters.Count > 1 ? ResolveVertexPoint(entity.Parameters[1], data) : null;
            Point3D? end = entity.Parameters.Count > 2 ? ResolveVertexPoint(entity.Parameters[2], data) : null;
            if (start.HasValue && end.HasValue)
            {
                edges.Add(new Edge(start.Value, end.Value));
            }
            return edges;
        }

        if (IsNamed(entity, "POLY_LINE"))
        {
            AppendPolyLineEdges(entity, data, edges);
            return edges;
        }

        if (IsNamed(entity, "ADVANCED_FACE") &&
            StepTessellator.TryExtractFaceOutline(entityId, data, out IReadOnlyList<Point3D> outline))
        {
            for (int i = 0; i < outline.Count; i++)
            {
                Point3D start = outline[i];
                Point3D end = outline[(i + 1) % outline.Count];
                edges.Add(new Edge(start, end));
            }
        }

        return edges;
    }

    public static bool TryGetEntityCenter(
        int entityId,
        IReadOnlyDictionary<int, EntityInstance> data,
        out Point3D center)
    {
        center = default;
        IReadOnlyList<Edge> edges = ExtractEntityEdges(entityId, data);
        if (edges.Count > 0)
        {
            double x = 0;
            double y = 0;
            double z = 0;
            int count = 0;
            foreach (Edge edge in edges)
            {
                x += edge.Start.X + edge.End.X;
                y += edge.Start.Y + edge.End.Y;
                z += edge.Start.Z + edge.End.Z;
                count += 2;
            }

            center = new Point3D(x / count, y / count, z / count);
            return true;
        }

        if (data.TryGetValue(entityId, out EntityInstance? entity) && IsNamed(entity, "CARTESIAN_POINT"))
        {
            Point3D? point = ExtractCoords(entity);
            if (point.HasValue)
            {
                center = point.Value;
                return true;
            }
        }

        return false;
    }

    private static void AppendPolyLineEdges(
        EntityInstance entity,
        IReadOnlyDictionary<int, EntityInstance> data,
        List<Edge> edges)
    {
        if (entity.Parameters.Count < 2 || entity.Parameters[1] is not Parameter.ListValue list)
        {
            return;
        }

        Point3D? previous = null;
        foreach (Parameter item in list.Items)
        {
            Point3D? point = ResolveCartesianPoint(item, data);
            if (point.HasValue && previous.HasValue)
            {
                edges.Add(new Edge(previous.Value, point.Value));
            }

            if (point.HasValue)
            {
                previous = point;
            }
        }
    }

    private static Point3D? ResolveVertexPoint(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? vertex) ||
            !IsNamed(vertex, "VERTEX_POINT") ||
            vertex.Parameters.Count < 2)
        {
            return null;
        }

        return ResolveCartesianPoint(vertex.Parameters[1], data);
    }

    private static Point3D? ResolveCartesianPoint(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? entity) ||
            !IsNamed(entity, "CARTESIAN_POINT"))
        {
            return null;
        }

        return ExtractCoords(entity);
    }

    private static Point3D? ExtractCoords(EntityInstance entity)
    {
        if (entity.Parameters.Count < 2 || entity.Parameters[1] is not Parameter.ListValue coords || coords.Items.Count < 2)
        {
            return null;
        }

        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = coords.Items.Count >= 3 ? ToDouble(coords.Items[2]) : 0.0;
        return new Point3D(x, y, z);
    }

    private static double ToDouble(Parameter parameter) => parameter switch
    {
        Parameter.RealValue realValue => realValue.Value,
        Parameter.IntegerValue integerValue => integerValue.Value,
        _ => 0.0
    };

    private static bool IsNamed(EntityInstance entity, string name) =>
        string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase);
}
