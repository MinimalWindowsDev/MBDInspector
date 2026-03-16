using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using StepParser.Parser;

namespace MBDInspector;

/// <summary>
/// Walks a parsed STEP entity graph and extracts line segments for wireframe display.
///
/// Strategy (in order of precedence):
///   1. EDGE_CURVE  → VERTEX_POINT × 2 → CARTESIAN_POINT → XYZ  (full B-rep edge list)
///   2. POLY_LINE   → sequence of CARTESIAN_POINT refs            (polyline fallback)
///   3. CARTESIAN_POINT cloud — all points, no connectivity       (last resort)
/// </summary>
public static class StepGeometryExtractor
{
    public readonly record struct Edge(Point3D Start, Point3D End);

    public static IReadOnlyList<Edge> Extract(IReadOnlyDictionary<int, EntityInstance> data)
    {
        var edges = new List<Edge>();

        // ── Strategy 1: EDGE_CURVE ────────────────────────────────────────
        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "EDGE_CURVE")) continue;
            if (entity.Parameters.Count < 3) continue;

            Point3D? start = ResolveVertexPoint(entity.Parameters[1], data);
            Point3D? end   = ResolveVertexPoint(entity.Parameters[2], data);
            if (start.HasValue && end.HasValue)
                edges.Add(new Edge(start.Value, end.Value));
        }

        if (edges.Count > 0) return edges;

        // ── Strategy 2: POLY_LINE ─────────────────────────────────────────
        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "POLY_LINE")) continue;
            if (entity.Parameters.Count < 2) continue;
            if (entity.Parameters[1] is not Parameter.ListValue list) continue;

            Point3D? prev = null;
            foreach (Parameter item in list.Items)
            {
                Point3D? pt = ResolveCartesianPoint(item, data);
                if (pt.HasValue && prev.HasValue)
                    edges.Add(new Edge(prev.Value, pt.Value));
                if (pt.HasValue)
                    prev = pt;
            }
        }

        if (edges.Count > 0) return edges;

        // ── Strategy 3: CARTESIAN_POINT cloud (no connectivity) ───────────
        var points = new List<Point3D>();
        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "CARTESIAN_POINT")) continue;
            Point3D? pt = ExtractCoords(entity);
            if (pt.HasValue) points.Add(pt.Value);
        }

        // Connect consecutive pairs so LinesVisual3D can display them as crosses
        for (int i = 0; i + 1 < points.Count; i += 2)
            edges.Add(new Edge(points[i], points[i + 1]));

        return edges;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Point3D? ResolveVertexPoint(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference eref) return null;
        if (!data.TryGetValue(eref.Id, out EntityInstance? vertex)) return null;
        if (!IsNamed(vertex, "VERTEX_POINT")) return null;
        if (vertex.Parameters.Count < 2) return null;
        return ResolveCartesianPoint(vertex.Parameters[1], data);
    }

    private static Point3D? ResolveCartesianPoint(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference eref) return null;
        if (!data.TryGetValue(eref.Id, out EntityInstance? entity)) return null;
        if (!IsNamed(entity, "CARTESIAN_POINT")) return null;
        return ExtractCoords(entity);
    }

    private static Point3D? ExtractCoords(EntityInstance entity)
    {
        // CARTESIAN_POINT('name', (x, y, z))
        if (entity.Parameters.Count < 2) return null;
        if (entity.Parameters[1] is not Parameter.ListValue coords) return null;
        if (coords.Items.Count < 2) return null;     // 2-D points are valid in STEP

        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = coords.Items.Count >= 3 ? ToDouble(coords.Items[2]) : 0.0;
        return new Point3D(x, y, z);
    }

    private static double ToDouble(Parameter p) => p switch
    {
        Parameter.RealValue    r => r.Value,
        Parameter.IntegerValue i => (double)i.Value,
        _                        => 0.0
    };

    private static bool IsNamed(EntityInstance entity, string name) =>
        string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase);
}
