using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StepParser.Parser;

namespace MBDInspector;

/// <summary>
/// Walks the STEP B-rep hierarchy and produces triangulated solid geometry.
///
/// Strategy per ADVANCED_FACE:
///   1. Resolve FACE_OUTER_BOUND → EDGE_LOOP → ORIENTED_EDGE → EDGE_CURVE chain.
///   2. Sample each edge curve (LINE=endpoints, CIRCLE/ELLIPSE=32pts,
///      B_SPLINE=control polygon, others=endpoints).
///   3. Fan-triangulate the resulting boundary polygon from its centroid.
///   4. Compute the face normal from the surface entity when it is a PLANE,
///      otherwise from the first non-degenerate polygon triple.
/// </summary>
public static class StepTessellator
{
    private const int CircleSamples = 32;

    // ── Public API ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns one flat-shaded MeshGeometry3D containing every tessellated face.
    /// Vertices are NOT shared between faces so per-face flat normals are preserved.
    /// </summary>
    public static MeshGeometry3D Tessellate(IReadOnlyDictionary<int, EntityInstance> data)
    {
        var positions = new Point3DCollection();
        var normals   = new Vector3DCollection();
        var indices   = new Int32Collection();

        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "ADVANCED_FACE")) continue;
            if (entity.Parameters.Count < 4) continue;

            // param[1]: list of face bounds, param[2]: surface, param[3]: same_sense
            if (entity.Parameters[1] is not Parameter.ListValue boundsParam) continue;

            bool sameSense = entity.Parameters[3] is not Parameter.EnumValue ev
                             || !string.Equals(ev.Name, "F", StringComparison.OrdinalIgnoreCase);

            // Compute surface normal from the surface entity if possible
            Vector3D? surfaceNormal = ResolveSurfaceNormal(entity.Parameters[2], data);

            // Walk each face bound; collect the outer boundary polygon
            foreach (Parameter boundRef in boundsParam.Items)
            {
                if (boundRef is not Parameter.EntityReference bref) continue;
                if (!data.TryGetValue(bref.Id, out EntityInstance? bound)) continue;
                if (bound.Parameters.Count < 2) continue;
                if (bound.Parameters[1] is not Parameter.EntityReference loopRef) continue;

                List<Point3D> polygon = SampleEdgeLoop(loopRef.Id, data);
                if (polygon.Count < 3) continue;

                Vector3D normal = surfaceNormal
                    ?? ComputePolygonNormal(polygon);

                if (!sameSense) normal = -normal;

                // Fan-triangulate from centroid
                Point3D centroid = Centroid(polygon);
                int base0 = positions.Count;

                for (int i = 0; i < polygon.Count; i++)
                {
                    int next = (i + 1) % polygon.Count;

                    int i0 = positions.Count;
                    positions.Add(centroid);
                    normals.Add(normal);

                    positions.Add(polygon[i]);
                    normals.Add(normal);

                    positions.Add(polygon[next]);
                    normals.Add(normal);

                    indices.Add(i0);
                    indices.Add(i0 + 1);
                    indices.Add(i0 + 2);
                }

                break; // one outer boundary per face
            }
        }

        return new MeshGeometry3D
        {
            Positions      = positions,
            Normals        = normals,
            TriangleIndices = indices
        };
    }

    // ── Surface normal ────────────────────────────────────────────────────

    private static Vector3D? ResolveSurfaceNormal(
        Parameter surfaceParam, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (surfaceParam is not Parameter.EntityReference sref) return null;
        if (!data.TryGetValue(sref.Id, out EntityInstance? surface)) return null;

        if (IsNamed(surface, "PLANE"))
        {
            if (surface.Parameters.Count < 2) return null;
            var (_, z, _) = ResolveAxis2(surface.Parameters[1], data);
            return z == default ? null : z;
        }

        return null; // other surface types: derive from polygon winding
    }

    // ── Edge loop / curve sampling ────────────────────────────────────────

    private static List<Point3D> SampleEdgeLoop(
        int loopId, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(loopId, out EntityInstance? loop)) return [];
        if (!IsNamed(loop, "EDGE_LOOP")) return [];
        if (loop.Parameters.Count < 2) return [];
        if (loop.Parameters[1] is not Parameter.ListValue edgeList) return [];

        var result = new List<Point3D>();
        foreach (Parameter edgeRef in edgeList.Items)
        {
            if (edgeRef is not Parameter.EntityReference eref) continue;
            if (!data.TryGetValue(eref.Id, out EntityInstance? oe)) continue;
            if (!IsNamed(oe, "ORIENTED_EDGE")) continue;
            if (oe.Parameters.Count < 5) continue;
            if (oe.Parameters[3] is not Parameter.EntityReference curveRef) continue;

            bool forward = oe.Parameters[4] is not Parameter.EnumValue ev
                           || !string.Equals(ev.Name, "F", StringComparison.OrdinalIgnoreCase);

            List<Point3D> pts = SampleEdgeCurve(curveRef.Id, data);
            if (!forward) pts.Reverse();

            // Skip first point if it duplicates the last already added
            int skip = result.Count > 0 && pts.Count > 0
                       && (pts[0] - result[^1]).Length < 1e-6 ? 1 : 0;

            result.AddRange(pts.Skip(skip));
        }

        // Remove closing duplicate
        if (result.Count > 1 && (result[0] - result[^1]).Length < 1e-6)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static List<Point3D> SampleEdgeCurve(
        int id, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(id, out EntityInstance? ec)) return [];
        if (!IsNamed(ec, "EDGE_CURVE")) return [];
        if (ec.Parameters.Count < 4) return [];

        Point3D? start = ResolveVertex(ec.Parameters[1], data);
        Point3D? end   = ResolveVertex(ec.Parameters[2], data);

        if (ec.Parameters[3] is not Parameter.EntityReference geomRef)
            return Compact([start, end]);

        if (!data.TryGetValue(geomRef.Id, out EntityInstance? geom))
            return Compact([start, end]);

        return SampleCurve(geom, start, end, data);
    }

    private static List<Point3D> SampleCurve(
        EntityInstance geom, Point3D? start, Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (IsNamed(geom, "LINE"))
            return Compact([start, end]);

        if (IsNamed(geom, "CIRCLE"))
            return SampleCircle(geom, start, end, data);

        if (IsNamed(geom, "ELLIPSE"))
            return SampleEllipse(geom, start, end, data);

        if (IsNamed(geom, "B_SPLINE_CURVE_WITH_KNOTS") ||
            IsNamed(geom, "B_SPLINE_CURVE") ||
            IsNamed(geom, "RATIONAL_B_SPLINE_CURVE"))
            return SampleBSpline(geom, start, end, data);

        return Compact([start, end]);
    }

    // ── Curve samplers ────────────────────────────────────────────────────

    private static List<Point3D> SampleCircle(
        EntityInstance circle, Point3D? start, Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (circle.Parameters.Count < 3) return Compact([start, end]);

        double radius = ToDouble(circle.Parameters[2]);
        if (radius <= 0) return Compact([start, end]);

        var (center, zDir, xDir) = ResolveAxis2(circle.Parameters[1], data);
        if (xDir == default) return Compact([start, end]);

        Vector3D yDir = Vector3D.CrossProduct(zDir, xDir);
        yDir.Normalize();

        bool fullCircle = start == null || end == null ||
                          (start.Value - end.Value).Length < 1e-6;

        double t0 = 0, t1 = 2 * Math.PI;
        if (!fullCircle)
        {
            t0 = AngleOnCircle(start!.Value - center, xDir, yDir);
            t1 = AngleOnCircle(end!.Value   - center, xDir, yDir);
            if (t1 <= t0) t1 += 2 * Math.PI;
        }

        int n = fullCircle ? CircleSamples
              : Math.Max(4, (int)(Math.Abs(t1 - t0) / (2 * Math.PI) * CircleSamples));

        var pts = new List<Point3D>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + (t1 - t0) * i / n;
            pts.Add(center + xDir * (radius * Math.Cos(t)) + yDir * (radius * Math.Sin(t)));
        }
        return pts;
    }

    private static List<Point3D> SampleEllipse(
        EntityInstance ellipse, Point3D? start, Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        // ELLIPSE('', #axis2, semi_axis1, semi_axis2)
        if (ellipse.Parameters.Count < 4) return Compact([start, end]);

        double a = ToDouble(ellipse.Parameters[2]);
        double b = ToDouble(ellipse.Parameters[3]);
        if (a <= 0 || b <= 0) return Compact([start, end]);

        var (center, zDir, xDir) = ResolveAxis2(ellipse.Parameters[1], data);
        if (xDir == default) return Compact([start, end]);

        Vector3D yDir = Vector3D.CrossProduct(zDir, xDir);
        yDir.Normalize();

        bool fullEllipse = start == null || end == null ||
                           (start.Value - end.Value).Length < 1e-6;

        double t0 = 0, t1 = 2 * Math.PI;
        if (!fullEllipse)
        {
            t0 = AngleOnCircle(start!.Value - center, xDir, yDir);
            t1 = AngleOnCircle(end!.Value   - center, xDir, yDir);
            if (t1 <= t0) t1 += 2 * Math.PI;
        }

        int n = CircleSamples;
        var pts = new List<Point3D>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + (t1 - t0) * i / n;
            pts.Add(center + xDir * (a * Math.Cos(t)) + yDir * (b * Math.Sin(t)));
        }
        return pts;
    }

    private static List<Point3D> SampleBSpline(
        EntityInstance bspline, Point3D? start, Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        // B_SPLINE_CURVE_WITH_KNOTS('', degree, (#cp ...), form, closed, self_int, mults, knots, spec)
        // Index 2 = control points list
        if (bspline.Parameters.Count < 3) return Compact([start, end]);
        if (bspline.Parameters[2] is not Parameter.ListValue cpList)
            return Compact([start, end]);

        var pts = new List<Point3D>();
        foreach (Parameter cpRef in cpList.Items)
        {
            Point3D? pt = ResolveCartesianPointParam(cpRef, data);
            if (pt.HasValue) pts.Add(pt.Value);
        }

        return pts.Count >= 2 ? pts : Compact([start, end]);
    }

    // ── Entity resolution helpers ─────────────────────────────────────────

    private static (Point3D center, Vector3D z, Vector3D x) ResolveAxis2(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference eref) return default;
        if (!data.TryGetValue(eref.Id, out EntityInstance? axis)) return default;

        // AXIS2_PLACEMENT_3D('', #location, #axis_dir, #ref_dir)
        if (axis.Parameters.Count < 4) return default;

        Point3D? center = ResolveCartesianPointParam(axis.Parameters[1], data);
        Vector3D z = ResolveDirectionParam(axis.Parameters[2], data);
        Vector3D x = ResolveDirectionParam(axis.Parameters[3], data);

        return (center ?? default, z, x);
    }

    private static Point3D? ResolveVertex(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference eref) return null;
        if (!data.TryGetValue(eref.Id, out EntityInstance? vp)) return null;
        if (!IsNamed(vp, "VERTEX_POINT")) return null;
        if (vp.Parameters.Count < 2) return null;
        return ResolveCartesianPointParam(vp.Parameters[1], data);
    }

    private static Point3D? ResolveCartesianPointParam(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference eref) return null;
        if (!data.TryGetValue(eref.Id, out EntityInstance? cp)) return null;
        if (!IsNamed(cp, "CARTESIAN_POINT")) return null;
        if (cp.Parameters.Count < 2) return null;
        if (cp.Parameters[1] is not Parameter.ListValue coords) return null;
        if (coords.Items.Count < 2) return null;
        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = coords.Items.Count >= 3 ? ToDouble(coords.Items[2]) : 0.0;
        return new Point3D(x, y, z);
    }

    private static Vector3D ResolveDirectionParam(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference eref) return default;
        if (!data.TryGetValue(eref.Id, out EntityInstance? dir)) return default;
        if (!IsNamed(dir, "DIRECTION")) return default;
        if (dir.Parameters.Count < 2) return default;
        if (dir.Parameters[1] is not Parameter.ListValue coords) return default;
        if (coords.Items.Count < 3) return default;
        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = ToDouble(coords.Items[2]);
        var v = new Vector3D(x, y, z);
        v.Normalize();
        return v;
    }

    // ── Math utilities ────────────────────────────────────────────────────

    private static Vector3D ComputePolygonNormal(List<Point3D> polygon)
    {
        // Newell's method: robust for arbitrary planar polygons
        var n = new Vector3D();
        int count = polygon.Count;
        for (int i = 0; i < count; i++)
        {
            Point3D cur  = polygon[i];
            Point3D next = polygon[(i + 1) % count];
            n.X += (cur.Y - next.Y) * (cur.Z + next.Z);
            n.Y += (cur.Z - next.Z) * (cur.X + next.X);
            n.Z += (cur.X - next.X) * (cur.Y + next.Y);
        }
        if (n.Length < 1e-10) return new Vector3D(0, 0, 1);
        n.Normalize();
        return n;
    }

    private static Point3D Centroid(List<Point3D> pts)
    {
        double x = 0, y = 0, z = 0;
        foreach (Point3D p in pts) { x += p.X; y += p.Y; z += p.Z; }
        int n = pts.Count;
        return new Point3D(x / n, y / n, z / n);
    }

    private static double AngleOnCircle(Vector3D v, Vector3D xDir, Vector3D yDir) =>
        Math.Atan2(Vector3D.DotProduct(v, yDir), Vector3D.DotProduct(v, xDir));

    private static double ToDouble(Parameter p) => p switch
    {
        Parameter.RealValue    r => r.Value,
        Parameter.IntegerValue i => (double)i.Value,
        _                        => 0.0
    };

    private static List<Point3D> Compact(Point3D?[] pts) =>
        pts.Where(p => p.HasValue).Select(p => p!.Value).ToList();

    private static bool IsNamed(EntityInstance e, string name) =>
        string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase);
}
