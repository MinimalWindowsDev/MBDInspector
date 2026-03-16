using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using StepParser.Parser;

namespace MBDInspector;

public static class StepTessellator
{
    private const int CircleSamples = 96;
    private const int SplineSamplesPerSpan = 12;
    private const int MaxSurfaceSubdivisionDepth = 4;
    private static readonly Color NoColor = Color.FromArgb(0, 0, 0, 0);

    private readonly record struct CylinderSurface(
        Point3D Origin,
        Vector3D Axis,
        Vector3D XDirection,
        Vector3D YDirection,
        double Radius);

    private readonly record struct ConeSurface(
        Point3D Origin,
        Vector3D Axis,
        Vector3D XDirection,
        Vector3D YDirection,
        double BaseRadius,
        double SemiAngle);

    private readonly record struct SphereSurface(
        Point3D Origin,
        Vector3D Axis,
        Vector3D XDirection,
        Vector3D YDirection,
        double Radius);

    private readonly record struct TorusSurface(
        Point3D Origin,
        Vector3D Axis,
        Vector3D XDirection,
        Vector3D YDirection,
        double MajorRadius,
        double MinorRadius);

    internal readonly record struct FaceBuildFailure(
        int FaceId,
        string SurfaceType,
        string Reason);

    public static List<(MeshGeometry3D Mesh, Color? FaceColor)> Tessellate(
        IReadOnlyDictionary<int, EntityInstance> data,
        IReadOnlyDictionary<int, Color>? colorMap = null)
    {
        List<FaceMeshItem> faceMeshes = TessellateFaces(data, colorMap);
        var buckets = new Dictionary<Color, (List<Point3D> Positions, List<int> Indices, List<Vector3D> Normals)>();

        foreach (FaceMeshItem faceMesh in faceMeshes)
        {
            Color colorKey = faceMesh.FaceColor ?? NoColor;
            if (!buckets.TryGetValue(colorKey, out var bucket))
            {
                bucket = (new List<Point3D>(), new List<int>(), new List<Vector3D>());
                buckets[colorKey] = bucket;
            }

            int baseIndex = bucket.Positions.Count;
            bucket.Positions.AddRange(faceMesh.Mesh.Positions);
            bucket.Indices.AddRange(faceMesh.Mesh.TriangleIndices.Select(index => index + baseIndex));
            if (faceMesh.Mesh.Normals.Count == faceMesh.Mesh.Positions.Count)
            {
                bucket.Normals.AddRange(faceMesh.Mesh.Normals);
            }
            else
            {
                bucket.Normals.AddRange(Enumerable.Repeat(new Vector3D(0, 0, 1), faceMesh.Mesh.Positions.Count));
            }
        }

        var result = new List<(MeshGeometry3D Mesh, Color? FaceColor)>(buckets.Count);
        foreach ((Color colorKey, (List<Point3D> positions, List<int> indices, List<Vector3D> normals)) in buckets)
        {
            if (positions.Count == 0)
            {
                continue;
            }

            result.Add((CreateFrozenMesh(positions, indices, normals), colorKey == NoColor ? null : colorKey));
        }

        return result;
    }

    internal static List<FaceMeshItem> TessellateFaces(
        IReadOnlyDictionary<int, EntityInstance> data,
        IReadOnlyDictionary<int, Color>? colorMap = null)
    {
        var result = new List<FaceMeshItem>();
        foreach ((int faceId, EntityInstance entity) in data)
        {
            if (!TryBuildFaceGeometry(entity, data, out List<Point3D>? positions, out List<int>? indices, out List<Vector3D>? normals, out _))
            {
                continue;
            }

            Color? faceColor = colorMap is not null && colorMap.TryGetValue(faceId, out Color color)
                ? color
                : null;

            result.Add(new FaceMeshItem(
                faceId,
                CreateFrozenMesh(positions!, indices!, normals!),
                faceColor));
        }

        return result;
    }

    public static bool TryTessellateFace(
        int faceId,
        IReadOnlyDictionary<int, EntityInstance> data,
        out MeshGeometry3D? mesh)
    {
        mesh = null;
        if (!data.TryGetValue(faceId, out EntityInstance? entity) ||
            !TryBuildFaceGeometry(entity, data, out List<Point3D>? positions, out List<int>? indices, out List<Vector3D>? normals, out _))
        {
            return false;
        }

        mesh = CreateFrozenMesh(positions!, indices!, normals!);
        return true;
    }

    internal static FaceBuildFailure? DescribeFaceFailure(
        int faceId,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(faceId, out EntityInstance? entity))
        {
            return new FaceBuildFailure(faceId, "<missing>", "face entity missing");
        }

        string surfaceType = TryGetSurfaceType(entity, data);
        if (!TryGetFacePolygon(entity, data, out List<Point3D>? polygon, out bool sameSense, out Parameter? surfaceParameter, out string? polygonFailure))
        {
            return new FaceBuildFailure(faceId, surfaceType, polygonFailure ?? "failed to extract face polygon");
        }

        var positions = new List<Point3D>();
        var indices = new List<int>();
        var normals = new List<Vector3D>();

        if (surfaceParameter is not null &&
            TryBuildAnalyticSurfaceFaceGeometry(surfaceParameter, polygon!, sameSense, data, positions, indices, normals))
        {
            return null;
        }

        Vector3D desiredNormal = surfaceParameter is not null
            ? ResolveSurfaceNormal(surfaceParameter, data) ?? ComputePolygonNormal(polygon!)
            : ComputePolygonNormal(polygon!);
        if (!sameSense)
        {
            desiredNormal = -desiredNormal;
        }

        if (!TryTriangulateProjectedPolygon(polygon!, desiredNormal, positions, indices, normals))
        {
            return new FaceBuildFailure(faceId, surfaceType, "triangulation failed");
        }

        return null;
    }

    public static bool TryExtractFaceOutline(
        int faceId,
        IReadOnlyDictionary<int, EntityInstance> data,
        out IReadOnlyList<Point3D> outline)
    {
        outline = Array.Empty<Point3D>();
        if (!data.TryGetValue(faceId, out EntityInstance? entity) ||
            !TryGetFacePolygon(entity, data, out List<Point3D>? polygon, out _, out _, out _))
        {
            return false;
        }

        outline = polygon!;
        return true;
    }

    private static bool TryBuildFaceGeometry(
        EntityInstance entity,
        IReadOnlyDictionary<int, EntityInstance> data,
        out List<Point3D>? positions,
        out List<int>? indices,
        out List<Vector3D>? normals,
        out List<Point3D>? polygon)
    {
        positions = null;
        indices = null;
        normals = null;
        polygon = null;

        if (!TryGetFacePolygon(entity, data, out polygon, out bool sameSense, out Parameter? surfaceParameter, out _))
        {
            return false;
        }

        positions = [];
        indices = [];
        normals = [];

        if (surfaceParameter is not null &&
            TryBuildAnalyticSurfaceFaceGeometry(surfaceParameter, polygon!, sameSense, data, positions, indices, normals))
        {
            return positions.Count > 0;
        }

        Vector3D desiredNormal = surfaceParameter is not null
            ? ResolveSurfaceNormal(surfaceParameter, data) ?? ComputePolygonNormal(polygon!)
            : ComputePolygonNormal(polygon!);
        if (!sameSense)
        {
            desiredNormal = -desiredNormal;
        }

        if (!TryTriangulateProjectedPolygon(polygon!, desiredNormal, positions, indices, normals) &&
            !TryBuildFanTriangulation(polygon!, desiredNormal, positions, indices, normals))
        {
            return false;
        }

        return positions.Count > 0;
    }

    private static bool TryGetFacePolygon(
        EntityInstance entity,
        IReadOnlyDictionary<int, EntityInstance> data,
        out List<Point3D>? polygon,
        out bool sameSense,
        out Parameter? surfaceParameter,
        out string? failureReason)
    {
        polygon = null;
        sameSense = true;
        surfaceParameter = null;
        failureReason = null;

        if (!IsNamed(entity, "ADVANCED_FACE") ||
            entity.Parameters.Count < 4 ||
            entity.Parameters[1] is not Parameter.ListValue boundsParam)
        {
            failureReason = "advanced face bounds missing";
            return false;
        }

        surfaceParameter = entity.Parameters[2];
        sameSense = entity.Parameters[3] is not Parameter.EnumValue enumValue
                     || !string.Equals(enumValue.Name, "F", StringComparison.OrdinalIgnoreCase);

        List<Point3D>? bestPolygon = null;
        bool bestIsOuter = false;
        int bestPointCount = -1;

        foreach (Parameter boundRef in boundsParam.Items)
        {
            if (boundRef is not Parameter.EntityReference boundaryReference ||
                !data.TryGetValue(boundaryReference.Id, out EntityInstance? bound) ||
                bound.Parameters.Count < 2 ||
                bound.Parameters[1] is not Parameter.EntityReference loopRef)
            {
                continue;
            }

            polygon = SampleEdgeLoop(loopRef.Id, data);
            if (polygon.Count >= 3)
            {
                bool isOuterBound = IsNamed(bound, "FACE_OUTER_BOUND");
                if (bestPolygon is null ||
                    (isOuterBound && !bestIsOuter) ||
                    (isOuterBound == bestIsOuter && polygon.Count > bestPointCount))
                {
                    bestPolygon = polygon;
                    bestIsOuter = isOuterBound;
                    bestPointCount = polygon.Count;
                }

                continue;
            }

            if (!data.TryGetValue(loopRef.Id, out EntityInstance? loop))
            {
                failureReason = $"bound #{boundaryReference.Id} references missing loop #{loopRef.Id}";
                continue;
            }

            failureReason = loop.Name is null
                ? $"loop #{loopRef.Id} could not be sampled"
                : $"loop #{loopRef.Id} ({loop.Name}) sampled only {polygon.Count} point(s)";
        }

        if (bestPolygon is not null)
        {
            polygon = bestPolygon;
            return true;
        }

        failureReason ??= "no valid face loop produced three or more points";
        return false;
    }

    private static bool TryBuildAnalyticSurfaceFaceGeometry(
        Parameter surfaceParameter,
        IReadOnlyList<Point3D> polygon,
        bool sameSense,
        IReadOnlyDictionary<int, EntityInstance> data,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (TryResolveCylindricalSurface(surfaceParameter, data, out CylinderSurface cylinder))
        {
            return TryBuildCylindricalFaceGeometry(polygon, cylinder, sameSense, positions, indices, normals);
        }

        if (TryResolveConicalSurface(surfaceParameter, data, out ConeSurface cone))
        {
            return TryBuildConicalFaceGeometry(polygon, cone, sameSense, positions, indices, normals);
        }

        if (TryResolveSphericalSurface(surfaceParameter, data, out SphereSurface sphere))
        {
            return TryBuildSphericalFaceGeometry(polygon, sphere, sameSense, positions, indices, normals);
        }

        if (TryResolveToroidalSurface(surfaceParameter, data, out TorusSurface torus))
        {
            return TryBuildToroidalFaceGeometry(polygon, torus, sameSense, positions, indices, normals);
        }

        return false;
    }

    private static bool TryBuildConicalFaceGeometry(
        IReadOnlyList<Point3D> polygon,
        ConeSurface cone,
        bool sameSense,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (!TryProjectPolygonToCone(polygon, cone, out List<Point> uvPolygon) ||
            !TryTriangulatePolygon(uvPolygon, out List<int> triangleIndices))
        {
            return false;
        }

        double uRange = uvPolygon.Max(point => point.X) - uvPolygon.Min(point => point.X);
        double vRange = uvPolygon.Max(point => point.Y) - uvPolygon.Min(point => point.Y);
        double targetEdgeLength = Math.Max(0.5, Math.Max(uRange, vRange) / 10.0);

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            Point a = uvPolygon[triangleIndices[i]];
            Point b = uvPolygon[triangleIndices[i + 1]];
            Point c = uvPolygon[triangleIndices[i + 2]];
            AddConicalTriangle(a, b, c, cone, sameSense, targetEdgeLength, 0, positions, indices, normals);
        }

        return positions.Count > 0;
    }

    private static bool TryBuildSphericalFaceGeometry(
        IReadOnlyList<Point3D> polygon,
        SphereSurface sphere,
        bool sameSense,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (!TryProjectPolygonToSphere(polygon, sphere, out List<Point> uvPolygon) ||
            !TryTriangulatePolygon(uvPolygon, out List<int> triangleIndices))
        {
            return false;
        }

        double uRange = uvPolygon.Max(point => point.X) - uvPolygon.Min(point => point.X);
        double vRange = uvPolygon.Max(point => point.Y) - uvPolygon.Min(point => point.Y);
        double targetEdgeLength = Math.Max(0.5, Math.Max(uRange, vRange) / 12.0);

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            Point a = uvPolygon[triangleIndices[i]];
            Point b = uvPolygon[triangleIndices[i + 1]];
            Point c = uvPolygon[triangleIndices[i + 2]];
            AddSphericalTriangle(a, b, c, sphere, sameSense, targetEdgeLength, 0, positions, indices, normals);
        }

        return positions.Count > 0;
    }

    private static bool TryBuildToroidalFaceGeometry(
        IReadOnlyList<Point3D> polygon,
        TorusSurface torus,
        bool sameSense,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (!TryProjectPolygonToTorus(polygon, torus, out List<Point> uvPolygon) ||
            !TryTriangulatePolygon(uvPolygon, out List<int> triangleIndices))
        {
            return false;
        }

        double uRange = uvPolygon.Max(point => point.X) - uvPolygon.Min(point => point.X);
        double vRange = uvPolygon.Max(point => point.Y) - uvPolygon.Min(point => point.Y);
        double targetEdgeLength = Math.Max(0.5, Math.Max(uRange, vRange) / 12.0);

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            Point a = uvPolygon[triangleIndices[i]];
            Point b = uvPolygon[triangleIndices[i + 1]];
            Point c = uvPolygon[triangleIndices[i + 2]];
            AddToroidalTriangle(a, b, c, torus, sameSense, targetEdgeLength, 0, positions, indices, normals);
        }

        return positions.Count > 0;
    }
    private static bool TryBuildCylindricalFaceGeometry(
        IReadOnlyList<Point3D> polygon,
        CylinderSurface cylinder,
        bool sameSense,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (!TryProjectPolygonToCylinder(polygon, cylinder, out List<Point> uvPolygon) ||
            !TryTriangulatePolygon(uvPolygon, out List<int> triangleIndices))
        {
            return false;
        }

        double uRange = uvPolygon.Max(point => point.X) - uvPolygon.Min(point => point.X);
        double vRange = uvPolygon.Max(point => point.Y) - uvPolygon.Min(point => point.Y);
        double targetEdgeLength = Math.Max(0.5, Math.Max(uRange, vRange) / 10.0);

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            Point a = uvPolygon[triangleIndices[i]];
            Point b = uvPolygon[triangleIndices[i + 1]];
            Point c = uvPolygon[triangleIndices[i + 2]];
            AddCylindricalTriangle(a, b, c, cylinder, sameSense, targetEdgeLength, 0, positions, indices, normals);
        }

        return positions.Count > 0;
    }

    private static void AddCylindricalTriangle(
        Point a,
        Point b,
        Point c,
        CylinderSurface cylinder,
        bool sameSense,
        double targetEdgeLength,
        int depth,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        double maxEdge = Math.Max(
            (a - b).Length,
            Math.Max((b - c).Length, (c - a).Length));

        if (depth < MaxSurfaceSubdivisionDepth && maxEdge > targetEdgeLength)
        {
            Point ab = MidPoint(a, b);
            Point bc = MidPoint(b, c);
            Point ca = MidPoint(c, a);

            AddCylindricalTriangle(a, ab, ca, cylinder, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddCylindricalTriangle(ab, b, bc, cylinder, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddCylindricalTriangle(ca, bc, c, cylinder, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddCylindricalTriangle(ab, bc, ca, cylinder, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            return;
        }

        Point3D p0 = PointOnCylinder(a, cylinder);
        Point3D p1 = PointOnCylinder(b, cylinder);
        Point3D p2 = PointOnCylinder(c, cylinder);

        Vector3D n0 = NormalOnCylinder(a, cylinder);
        Vector3D n1 = NormalOnCylinder(b, cylinder);
        Vector3D n2 = NormalOnCylinder(c, cylinder);
        if (!sameSense)
        {
            n0 = -n0;
            n1 = -n1;
            n2 = -n2;
        }

        Vector3D desiredNormal = n0 + n1 + n2;
        if (desiredNormal.Length < 1e-10)
        {
            desiredNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
        }

        AddTriangle(p0, p1, p2, n0, n1, n2, desiredNormal, positions, indices, normals);
    }

    private static void AddConicalTriangle(
        Point a,
        Point b,
        Point c,
        ConeSurface cone,
        bool sameSense,
        double targetEdgeLength,
        int depth,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        double maxEdge = Math.Max((a - b).Length, Math.Max((b - c).Length, (c - a).Length));
        if (depth < MaxSurfaceSubdivisionDepth && maxEdge > targetEdgeLength)
        {
            Point ab = MidPoint(a, b);
            Point bc = MidPoint(b, c);
            Point ca = MidPoint(c, a);
            AddConicalTriangle(a, ab, ca, cone, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddConicalTriangle(ab, b, bc, cone, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddConicalTriangle(ca, bc, c, cone, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddConicalTriangle(ab, bc, ca, cone, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            return;
        }

        Point3D p0 = PointOnCone(a, cone);
        Point3D p1 = PointOnCone(b, cone);
        Point3D p2 = PointOnCone(c, cone);
        Vector3D n0 = NormalOnCone(a, cone);
        Vector3D n1 = NormalOnCone(b, cone);
        Vector3D n2 = NormalOnCone(c, cone);
        if (!sameSense)
        {
            n0 = -n0; n1 = -n1; n2 = -n2;
        }

        AddTriangle(p0, p1, p2, n0, n1, n2, n0 + n1 + n2, positions, indices, normals);
    }

    private static void AddSphericalTriangle(
        Point a,
        Point b,
        Point c,
        SphereSurface sphere,
        bool sameSense,
        double targetEdgeLength,
        int depth,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        double maxEdge = Math.Max((a - b).Length, Math.Max((b - c).Length, (c - a).Length));
        if (depth < MaxSurfaceSubdivisionDepth && maxEdge > targetEdgeLength)
        {
            Point ab = MidPoint(a, b);
            Point bc = MidPoint(b, c);
            Point ca = MidPoint(c, a);
            AddSphericalTriangle(a, ab, ca, sphere, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddSphericalTriangle(ab, b, bc, sphere, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddSphericalTriangle(ca, bc, c, sphere, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddSphericalTriangle(ab, bc, ca, sphere, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            return;
        }

        Point3D p0 = PointOnSphere(a, sphere);
        Point3D p1 = PointOnSphere(b, sphere);
        Point3D p2 = PointOnSphere(c, sphere);
        Vector3D n0 = NormalOnSphere(a, sphere);
        Vector3D n1 = NormalOnSphere(b, sphere);
        Vector3D n2 = NormalOnSphere(c, sphere);
        if (!sameSense)
        {
            n0 = -n0; n1 = -n1; n2 = -n2;
        }

        AddTriangle(p0, p1, p2, n0, n1, n2, n0 + n1 + n2, positions, indices, normals);
    }

    private static void AddToroidalTriangle(
        Point a,
        Point b,
        Point c,
        TorusSurface torus,
        bool sameSense,
        double targetEdgeLength,
        int depth,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        double maxEdge = Math.Max((a - b).Length, Math.Max((b - c).Length, (c - a).Length));
        if (depth < MaxSurfaceSubdivisionDepth && maxEdge > targetEdgeLength)
        {
            Point ab = MidPoint(a, b);
            Point bc = MidPoint(b, c);
            Point ca = MidPoint(c, a);
            AddToroidalTriangle(a, ab, ca, torus, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddToroidalTriangle(ab, b, bc, torus, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddToroidalTriangle(ca, bc, c, torus, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            AddToroidalTriangle(ab, bc, ca, torus, sameSense, targetEdgeLength, depth + 1, positions, indices, normals);
            return;
        }

        Point3D p0 = PointOnTorus(a, torus);
        Point3D p1 = PointOnTorus(b, torus);
        Point3D p2 = PointOnTorus(c, torus);
        Vector3D n0 = NormalOnTorus(a, torus);
        Vector3D n1 = NormalOnTorus(b, torus);
        Vector3D n2 = NormalOnTorus(c, torus);
        if (!sameSense)
        {
            n0 = -n0; n1 = -n1; n2 = -n2;
        }

        AddTriangle(p0, p1, p2, n0, n1, n2, n0 + n1 + n2, positions, indices, normals);
    }

    private static bool TryTriangulateProjectedPolygon(
        IReadOnlyList<Point3D> polygon,
        Vector3D desiredNormal,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (!TryProjectPolygonToPlane(polygon, desiredNormal, out List<Point> projected) ||
            !TryTriangulatePolygon(projected, out List<int> triangleIndices))
        {
            return false;
        }

        Vector3D normal = desiredNormal;
        if (normal.Length < 1e-10)
        {
            normal = ComputePolygonNormal(polygon.ToList());
        }

        for (int i = 0; i < triangleIndices.Count; i += 3)
        {
            Point3D p0 = polygon[triangleIndices[i]];
            Point3D p1 = polygon[triangleIndices[i + 1]];
            Point3D p2 = polygon[triangleIndices[i + 2]];
            AddTriangle(p0, p1, p2, normal, normal, normal, normal, positions, indices, normals);
        }

        return positions.Count > 0;
    }

    private static void AddTriangle(
        Point3D p0,
        Point3D p1,
        Point3D p2,
        Vector3D n0,
        Vector3D n1,
        Vector3D n2,
        Vector3D desiredNormal,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        Vector3D autoNormal = Vector3D.CrossProduct(p1 - p0, p2 - p0);
        if (autoNormal.Length < 1e-10)
        {
            return;
        }

        if (desiredNormal.Length > 1e-10 && Vector3D.DotProduct(autoNormal, desiredNormal) < 0)
        {
            (p1, p2) = (p2, p1);
            (n1, n2) = (n2, n1);
        }

        Normalize(ref n0);
        Normalize(ref n1);
        Normalize(ref n2);

        int baseIndex = positions.Count;
        positions.Add(p0);
        positions.Add(p1);
        positions.Add(p2);
        normals.Add(n0);
        normals.Add(n1);
        normals.Add(n2);
        indices.Add(baseIndex);
        indices.Add(baseIndex + 1);
        indices.Add(baseIndex + 2);
    }

    private static bool TryResolveCylindricalSurface(
        Parameter surfaceParam,
        IReadOnlyDictionary<int, EntityInstance> data,
        out CylinderSurface cylinder)
    {
        cylinder = default;
        if (surfaceParam is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? surface) ||
            !IsNamed(surface, "CYLINDRICAL_SURFACE") ||
            surface.Parameters.Count < 3)
        {
            return false;
        }

        double radius = ToDouble(surface.Parameters[2]);
        if (radius <= 0)
        {
            return false;
        }

        (Point3D origin, Vector3D axis, Vector3D xDirection) = ResolveAxis2(surface.Parameters[1], data);
        if (axis.Length < 1e-10)
        {
            axis = new Vector3D(0, 0, 1);
        }

        if (xDirection.Length < 1e-10)
        {
            xDirection = CreatePerpendicular(axis);
        }

        Vector3D yDirection = Vector3D.CrossProduct(axis, xDirection);
        Normalize(ref axis);
        Normalize(ref xDirection);
        Normalize(ref yDirection);

        cylinder = new CylinderSurface(origin, axis, xDirection, yDirection, radius);
        return true;
    }

    private static bool TryResolveConicalSurface(
        Parameter surfaceParam,
        IReadOnlyDictionary<int, EntityInstance> data,
        out ConeSurface cone)
    {
        cone = default;
        if (surfaceParam is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? surface) ||
            !IsNamed(surface, "CONICAL_SURFACE") ||
            surface.Parameters.Count < 4)
        {
            return false;
        }

        double radius = ToDouble(surface.Parameters[2]);
        double semiAngle = ToDouble(surface.Parameters[3]);
        (Point3D origin, Vector3D axis, Vector3D xDirection) = ResolveAxis2(surface.Parameters[1], data);
        Vector3D yDirection = Vector3D.CrossProduct(axis, xDirection);
        Normalize(ref axis);
        Normalize(ref xDirection);
        Normalize(ref yDirection);
        cone = new ConeSurface(origin, axis, xDirection, yDirection, radius, semiAngle);
        return true;
    }

    private static bool TryResolveSphericalSurface(
        Parameter surfaceParam,
        IReadOnlyDictionary<int, EntityInstance> data,
        out SphereSurface sphere)
    {
        sphere = default;
        if (surfaceParam is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? surface) ||
            !IsNamed(surface, "SPHERICAL_SURFACE") ||
            surface.Parameters.Count < 3)
        {
            return false;
        }

        double radius = ToDouble(surface.Parameters[2]);
        (Point3D origin, Vector3D axis, Vector3D xDirection) = ResolveAxis2(surface.Parameters[1], data);
        Vector3D yDirection = Vector3D.CrossProduct(axis, xDirection);
        Normalize(ref axis);
        Normalize(ref xDirection);
        Normalize(ref yDirection);
        sphere = new SphereSurface(origin, axis, xDirection, yDirection, radius);
        return radius > 0;
    }

    private static bool TryResolveToroidalSurface(
        Parameter surfaceParam,
        IReadOnlyDictionary<int, EntityInstance> data,
        out TorusSurface torus)
    {
        torus = default;
        if (surfaceParam is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? surface) ||
            !IsNamed(surface, "TOROIDAL_SURFACE") ||
            surface.Parameters.Count < 4)
        {
            return false;
        }

        double majorRadius = ToDouble(surface.Parameters[2]);
        double minorRadius = ToDouble(surface.Parameters[3]);
        (Point3D origin, Vector3D axis, Vector3D xDirection) = ResolveAxis2(surface.Parameters[1], data);
        Vector3D yDirection = Vector3D.CrossProduct(axis, xDirection);
        Normalize(ref axis);
        Normalize(ref xDirection);
        Normalize(ref yDirection);
        torus = new TorusSurface(origin, axis, xDirection, yDirection, majorRadius, minorRadius);
        return majorRadius > 0 && minorRadius > 0;
    }

    private static bool TryProjectPolygonToPlane(
        IReadOnlyList<Point3D> polygon,
        Vector3D normal,
        out List<Point> projected)
    {
        projected = [];
        if (polygon.Count < 3)
        {
            return false;
        }

        if (normal.Length < 1e-10)
        {
            normal = ComputePolygonNormal(polygon.ToList());
        }

        Normalize(ref normal);
        Vector3D axisU = CreatePerpendicular(normal);
        Vector3D axisV = Vector3D.CrossProduct(normal, axisU);
        Normalize(ref axisU);
        Normalize(ref axisV);

        Point3D origin = polygon[0];
        foreach (Point3D point in polygon)
        {
            Vector3D offset = point - origin;
            projected.Add(new Point(
                Vector3D.DotProduct(offset, axisU),
                Vector3D.DotProduct(offset, axisV)));
        }

        return true;
    }

    private static bool TryProjectPolygonToCylinder(
        IReadOnlyList<Point3D> polygon,
        CylinderSurface cylinder,
        out List<Point> projected)
    {
        projected = [];
        if (polygon.Count < 3)
        {
            return false;
        }

        double? previousAngle = null;
        foreach (Point3D point in polygon)
        {
            Vector3D offset = point - cylinder.Origin;
            double axial = Vector3D.DotProduct(offset, cylinder.Axis);
            Vector3D radial = offset - (cylinder.Axis * axial);
            if (radial.Length < 1e-10)
            {
                return false;
            }

            double angle = Math.Atan2(
                Vector3D.DotProduct(radial, cylinder.YDirection),
                Vector3D.DotProduct(radial, cylinder.XDirection));

            if (previousAngle.HasValue)
            {
                while (angle - previousAngle.Value > Math.PI)
                {
                    angle -= Math.PI * 2.0;
                }

                while (angle - previousAngle.Value < -Math.PI)
                {
                    angle += Math.PI * 2.0;
                }
            }

            previousAngle = angle;
            projected.Add(new Point(angle * cylinder.Radius, axial));
        }

        return true;
    }

    private static bool TryProjectPolygonToCone(
        IReadOnlyList<Point3D> polygon,
        ConeSurface cone,
        out List<Point> projected)
    {
        projected = [];
        double? previousAngle = null;
        foreach (Point3D point in polygon)
        {
            Vector3D offset = point - cone.Origin;
            double axial = Vector3D.DotProduct(offset, cone.Axis);
            Vector3D radial = offset - (cone.Axis * axial);
            if (radial.Length < 1e-10)
            {
                return false;
            }

            double angle = Math.Atan2(
                Vector3D.DotProduct(radial, cone.YDirection),
                Vector3D.DotProduct(radial, cone.XDirection));
            angle = UnwrapAngle(angle, previousAngle);
            previousAngle = angle;
            projected.Add(new Point(angle * Math.Max(0.001, radial.Length), axial));
        }

        return projected.Count >= 3;
    }

    private static bool TryProjectPolygonToSphere(
        IReadOnlyList<Point3D> polygon,
        SphereSurface sphere,
        out List<Point> projected)
    {
        projected = [];
        double? previousLongitude = null;
        foreach (Point3D point in polygon)
        {
            Vector3D offset = point - sphere.Origin;
            if (offset.Length < 1e-10)
            {
                return false;
            }

            Normalize(ref offset);
            double longitude = Math.Atan2(
                Vector3D.DotProduct(offset, sphere.YDirection),
                Vector3D.DotProduct(offset, sphere.XDirection));
            longitude = UnwrapAngle(longitude, previousLongitude);
            previousLongitude = longitude;

            double latitude = Math.Asin(Math.Clamp(Vector3D.DotProduct(offset, sphere.Axis), -1.0, 1.0));
            projected.Add(new Point(longitude * sphere.Radius, latitude * sphere.Radius));
        }

        return projected.Count >= 3;
    }

    private static bool TryProjectPolygonToTorus(
        IReadOnlyList<Point3D> polygon,
        TorusSurface torus,
        out List<Point> projected)
    {
        projected = [];
        double? previousU = null;
        double? previousV = null;
        foreach (Point3D point in polygon)
        {
            Vector3D offset = point - torus.Origin;
            double axial = Vector3D.DotProduct(offset, torus.Axis);
            Vector3D planar = offset - (torus.Axis * axial);
            if (planar.Length < 1e-10)
            {
                return false;
            }

            double u = Math.Atan2(
                Vector3D.DotProduct(planar, torus.YDirection),
                Vector3D.DotProduct(planar, torus.XDirection));
            u = UnwrapAngle(u, previousU);
            previousU = u;

            double radial = planar.Length;
            double v = Math.Atan2(axial, radial - torus.MajorRadius);
            v = UnwrapAngle(v, previousV);
            previousV = v;

            projected.Add(new Point(u * torus.MajorRadius, v * torus.MinorRadius));
        }

        return projected.Count >= 3;
    }

    private static Point3D PointOnCylinder(Point uv, CylinderSurface cylinder)
    {
        double angle = uv.X / cylinder.Radius;
        return cylinder.Origin
             + (cylinder.Axis * uv.Y)
             + (cylinder.XDirection * (cylinder.Radius * Math.Cos(angle)))
             + (cylinder.YDirection * (cylinder.Radius * Math.Sin(angle)));
    }

    private static Vector3D NormalOnCylinder(Point uv, CylinderSurface cylinder)
    {
        double angle = uv.X / cylinder.Radius;
        Vector3D normal =
            (cylinder.XDirection * Math.Cos(angle)) +
            (cylinder.YDirection * Math.Sin(angle));
        Normalize(ref normal);
        return normal;
    }

    private static Point3D PointOnCone(Point uv, ConeSurface cone)
    {
        double axial = uv.Y;
        double radius = cone.BaseRadius + (axial * Math.Tan(cone.SemiAngle));
        double angle = uv.X / Math.Max(0.001, Math.Abs(radius));
        return cone.Origin
             + (cone.Axis * axial)
             + (cone.XDirection * (radius * Math.Cos(angle)))
             + (cone.YDirection * (radius * Math.Sin(angle)));
    }

    private static Vector3D NormalOnCone(Point uv, ConeSurface cone)
    {
        double axial = uv.Y;
        double radius = cone.BaseRadius + (axial * Math.Tan(cone.SemiAngle));
        double angle = uv.X / Math.Max(0.001, Math.Abs(radius));
        Vector3D radial =
            (cone.XDirection * Math.Cos(angle)) +
            (cone.YDirection * Math.Sin(angle));
        Vector3D tangentV = cone.Axis + (radial * Math.Tan(cone.SemiAngle));
        Vector3D tangentU =
            (-cone.XDirection * (radius * Math.Sin(angle))) +
            (cone.YDirection * (radius * Math.Cos(angle)));
        Vector3D normal = Vector3D.CrossProduct(tangentU, tangentV);
        Normalize(ref normal);
        return normal;
    }

    private static Point3D PointOnSphere(Point uv, SphereSurface sphere)
    {
        double longitude = uv.X / sphere.Radius;
        double latitude = uv.Y / sphere.Radius;
        double cosLat = Math.Cos(latitude);
        return sphere.Origin
             + (sphere.XDirection * (sphere.Radius * cosLat * Math.Cos(longitude)))
             + (sphere.YDirection * (sphere.Radius * cosLat * Math.Sin(longitude)))
             + (sphere.Axis * (sphere.Radius * Math.Sin(latitude)));
    }

    private static Vector3D NormalOnSphere(Point uv, SphereSurface sphere)
    {
        Point3D point = PointOnSphere(uv, sphere);
        Vector3D normal = point - sphere.Origin;
        Normalize(ref normal);
        return normal;
    }

    private static Point3D PointOnTorus(Point uv, TorusSurface torus)
    {
        double u = uv.X / torus.MajorRadius;
        double v = uv.Y / torus.MinorRadius;
        Vector3D majorDir =
            (torus.XDirection * Math.Cos(u)) +
            (torus.YDirection * Math.Sin(u));
        return torus.Origin
             + (majorDir * (torus.MajorRadius + (torus.MinorRadius * Math.Cos(v))))
             + (torus.Axis * (torus.MinorRadius * Math.Sin(v)));
    }

    private static Vector3D NormalOnTorus(Point uv, TorusSurface torus)
    {
        double u = uv.X / torus.MajorRadius;
        double v = uv.Y / torus.MinorRadius;
        Vector3D majorDir =
            (torus.XDirection * Math.Cos(u)) +
            (torus.YDirection * Math.Sin(u));
        Vector3D normal = (majorDir * Math.Cos(v)) + (torus.Axis * Math.Sin(v));
        Normalize(ref normal);
        return normal;
    }

    private static bool TryTriangulatePolygon(
        IReadOnlyList<Point> polygon,
        out List<int> indices)
    {
        indices = [];
        if (polygon.Count < 3)
        {
            return false;
        }

        double signedArea = SignedArea(polygon);
        if (Math.Abs(signedArea) < 1e-8)
        {
            return false;
        }

        bool isCounterClockwise = signedArea > 0;
        List<int> remaining = Enumerable.Range(0, polygon.Count).ToList();
        int guard = polygon.Count * polygon.Count;

        while (remaining.Count > 3 && guard-- > 0)
        {
            bool earFound = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int prev = remaining[(i - 1 + remaining.Count) % remaining.Count];
                int current = remaining[i];
                int next = remaining[(i + 1) % remaining.Count];

                Point a = polygon[prev];
                Point b = polygon[current];
                Point c = polygon[next];
                if (!IsConvex(a, b, c, isCounterClockwise))
                {
                    continue;
                }

                bool containsPoint = false;
                foreach (int index in remaining)
                {
                    if (index == prev || index == current || index == next)
                    {
                        continue;
                    }

                    if (IsPointInTriangle(polygon[index], a, b, c))
                    {
                        containsPoint = true;
                        break;
                    }
                }

                if (containsPoint)
                {
                    continue;
                }

                indices.Add(prev);
                indices.Add(current);
                indices.Add(next);
                remaining.RemoveAt(i);
                earFound = true;
                break;
            }

            if (!earFound)
            {
                break;
            }
        }

        if (remaining.Count == 3)
        {
            indices.Add(remaining[0]);
            indices.Add(remaining[1]);
            indices.Add(remaining[2]);
        }

        return indices.Count >= 3;
    }

    private static double SignedArea(IReadOnlyList<Point> polygon)
    {
        double area = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            Point current = polygon[i];
            Point next = polygon[(i + 1) % polygon.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area / 2.0;
    }

    private static bool IsConvex(Point a, Point b, Point c, bool isCounterClockwise)
    {
        double cross = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        return isCounterClockwise ? cross > 1e-8 : cross < -1e-8;
    }

    private static bool IsPointInTriangle(Point point, Point a, Point b, Point c)
    {
        double denominator = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
        if (Math.Abs(denominator) < 1e-10)
        {
            return false;
        }

        double alpha = (((b.Y - c.Y) * (point.X - c.X)) + ((c.X - b.X) * (point.Y - c.Y))) / denominator;
        double beta = (((c.Y - a.Y) * (point.X - c.X)) + ((a.X - c.X) * (point.Y - c.Y))) / denominator;
        double gamma = 1.0 - alpha - beta;

        return alpha > 1e-8 && beta > 1e-8 && gamma > 1e-8;
    }
    private static List<Point3D> SampleEdgeLoop(
        int loopId,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(loopId, out EntityInstance? loop) ||
            !IsNamed(loop, "EDGE_LOOP") ||
            loop.Parameters.Count < 2 ||
            loop.Parameters[1] is not Parameter.ListValue edgeList)
        {
            return [];
        }

        var result = new List<Point3D>();
        foreach (Parameter edgeRef in edgeList.Items)
        {
            if (edgeRef is not Parameter.EntityReference entityReference ||
                !data.TryGetValue(entityReference.Id, out EntityInstance? orientedEdge) ||
                !IsNamed(orientedEdge, "ORIENTED_EDGE") ||
                orientedEdge.Parameters.Count < 5 ||
                orientedEdge.Parameters[3] is not Parameter.EntityReference curveRef)
            {
                continue;
            }

            bool forward = orientedEdge.Parameters[4] is not Parameter.EnumValue enumValue
                           || !string.Equals(enumValue.Name, "F", StringComparison.OrdinalIgnoreCase);

            List<Point3D> points = SampleEdgeCurve(curveRef.Id, data);
            if (!forward)
            {
                points.Reverse();
            }

            int skip = result.Count > 0 && points.Count > 0 && (points[0] - result[^1]).Length < 1e-6 ? 1 : 0;
            result.AddRange(points.Skip(skip));
        }

        if (result.Count > 1 && (result[0] - result[^1]).Length < 1e-6)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    private static List<Point3D> SampleEdgeCurve(
        int id,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (!data.TryGetValue(id, out EntityInstance? edgeCurve) ||
            !IsNamed(edgeCurve, "EDGE_CURVE") ||
            edgeCurve.Parameters.Count < 4)
        {
            return [];
        }

        Point3D? start = ResolveVertex(edgeCurve.Parameters[1], data);
        Point3D? end = ResolveVertex(edgeCurve.Parameters[2], data);

        if (edgeCurve.Parameters[3] is not Parameter.EntityReference geometryReference ||
            !data.TryGetValue(geometryReference.Id, out EntityInstance? geometry))
        {
            return Compact([start, end]);
        }

        return SampleCurve(geometry, start, end, data);
    }

    private static List<Point3D> SampleCurve(
        EntityInstance geometry,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (IsNamed(geometry, "TRIMMED_CURVE") && geometry.Parameters.Count >= 2)
        {
            if (geometry.Parameters[1] is Parameter.EntityReference basisCurveRef &&
                data.TryGetValue(basisCurveRef.Id, out EntityInstance? basisCurve))
            {
                return SampleCurve(basisCurve, start, end, data);
            }
        }

        if ((IsNamed(geometry, "SURFACE_CURVE") || IsNamed(geometry, "SEAM_CURVE")) &&
            geometry.Parameters.Count >= 2)
        {
            if (geometry.Parameters[1] is Parameter.EntityReference curve3dRef &&
                data.TryGetValue(curve3dRef.Id, out EntityInstance? curve3d))
            {
                return SampleCurve(curve3d, start, end, data);
            }
        }

        if (IsNamed(geometry, "LINE"))
        {
            return Compact([start, end]);
        }

        if (IsNamed(geometry, "CIRCLE"))
        {
            return SampleCircle(geometry, start, end, data);
        }

        if (IsNamed(geometry, "ELLIPSE"))
        {
            return SampleEllipse(geometry, start, end, data);
        }

        if (IsNamed(geometry, "B_SPLINE_CURVE_WITH_KNOTS") ||
            IsNamed(geometry, "B_SPLINE_CURVE") ||
            IsNamed(geometry, "RATIONAL_B_SPLINE_CURVE"))
        {
            return SampleBSpline(geometry, start, end, data);
        }

        return Compact([start, end]);
    }

    private static List<Point3D> SampleCircle(
        EntityInstance circle,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (circle.Parameters.Count < 3)
        {
            return Compact([start, end]);
        }

        double radius = ToDouble(circle.Parameters[2]);
        if (radius <= 0)
        {
            return Compact([start, end]);
        }

        (Point3D center, Vector3D zDirection, Vector3D xDirection) = ResolveAxis2(circle.Parameters[1], data);
        if (xDirection.Length < 1e-10)
        {
            return Compact([start, end]);
        }

        Vector3D yDirection = Vector3D.CrossProduct(zDirection, xDirection);
        Normalize(ref yDirection);

        bool fullCircle = start is null || end is null || (start.Value - end.Value).Length < 1e-6;
        double t0 = 0;
        double t1 = 2 * Math.PI;
        if (!fullCircle)
        {
            t0 = AngleOnCircle(start!.Value - center, xDirection, yDirection);
            t1 = AngleOnCircle(end!.Value - center, xDirection, yDirection);
            if (t1 <= t0)
            {
                t1 += 2 * Math.PI;
            }
        }

        int sampleCount = fullCircle
            ? CircleSamples
            : Math.Max(8, (int)Math.Ceiling((Math.Abs(t1 - t0) / (2 * Math.PI)) * CircleSamples));

        var points = new List<Point3D>(sampleCount + 1);
        for (int i = 0; i <= sampleCount; i++)
        {
            double t = t0 + ((t1 - t0) * i / sampleCount);
            points.Add(center + (xDirection * (radius * Math.Cos(t))) + (yDirection * (radius * Math.Sin(t))));
        }

        return points;
    }

    private static List<Point3D> SampleEllipse(
        EntityInstance ellipse,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (ellipse.Parameters.Count < 4)
        {
            return Compact([start, end]);
        }

        double semiAxis1 = ToDouble(ellipse.Parameters[2]);
        double semiAxis2 = ToDouble(ellipse.Parameters[3]);
        if (semiAxis1 <= 0 || semiAxis2 <= 0)
        {
            return Compact([start, end]);
        }

        (Point3D center, Vector3D zDirection, Vector3D xDirection) = ResolveAxis2(ellipse.Parameters[1], data);
        if (xDirection.Length < 1e-10)
        {
            return Compact([start, end]);
        }

        Vector3D yDirection = Vector3D.CrossProduct(zDirection, xDirection);
        Normalize(ref yDirection);

        bool fullEllipse = start is null || end is null || (start.Value - end.Value).Length < 1e-6;
        double t0 = 0;
        double t1 = 2 * Math.PI;
        if (!fullEllipse)
        {
            t0 = AngleOnCircle(start!.Value - center, xDirection, yDirection);
            t1 = AngleOnCircle(end!.Value - center, xDirection, yDirection);
            if (t1 <= t0)
            {
                t1 += 2 * Math.PI;
            }
        }

        int sampleCount = fullEllipse
            ? CircleSamples
            : Math.Max(8, (int)Math.Ceiling((Math.Abs(t1 - t0) / (2 * Math.PI)) * CircleSamples));

        var points = new List<Point3D>(sampleCount + 1);
        for (int i = 0; i <= sampleCount; i++)
        {
            double t = t0 + ((t1 - t0) * i / sampleCount);
            points.Add(center + (xDirection * (semiAxis1 * Math.Cos(t))) + (yDirection * (semiAxis2 * Math.Sin(t))));
        }

        return points;
    }

    private static List<Point3D> SampleBSpline(
        EntityInstance bspline,
        Point3D? start,
        Point3D? end,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        IReadOnlyList<Parameter>? parameters =
            TryGetParameters(bspline, "B_SPLINE_CURVE") ??
            TryGetParameters(bspline, "B_SPLINE_CURVE_WITH_KNOTS") ??
            TryGetParameters(bspline, "RATIONAL_B_SPLINE_CURVE");
        if (!TryGetBSplineControlPoints(parameters, out Parameter.ListValue? controlPoints))
        {
            return Compact([start, end]);
        }

        var points = new List<Point3D>();
        foreach (Parameter controlPointReference in controlPoints!.Items)
        {
            Point3D? point = ResolveCartesianPointParam(controlPointReference, data);
            if (point.HasValue)
            {
                points.Add(point.Value);
            }
        }

        if (points.Count < 2)
        {
            return Compact([start, end]);
        }

        if (points.Count == 2)
        {
            return points;
        }

        var samples = new List<Point3D> { points[0] };
        for (int i = 0; i < points.Count - 1; i++)
        {
            Point3D p0 = i == 0 ? points[0] : points[i - 1];
            Point3D p1 = points[i];
            Point3D p2 = points[i + 1];
            Point3D p3 = i + 2 < points.Count ? points[i + 2] : points[^1];

            for (int step = 1; step <= SplineSamplesPerSpan; step++)
            {
                double t = step / (double)SplineSamplesPerSpan;
                samples.Add(CatmullRom(p0, p1, p2, p3, t));
            }
        }

        return samples;
    }

    private static bool TryGetBSplineControlPoints(
        IReadOnlyList<Parameter>? parameters,
        out Parameter.ListValue? controlPoints)
    {
        controlPoints = null;
        if (parameters is null)
        {
            return false;
        }

        if (parameters.Count >= 2 && parameters[1] is Parameter.ListValue directList)
        {
            controlPoints = directList;
            return true;
        }

        if (parameters.Count >= 3 && parameters[2] is Parameter.ListValue simpleEntityList)
        {
            controlPoints = simpleEntityList;
            return true;
        }

        return false;
    }

    private static bool TryBuildFanTriangulation(
        IReadOnlyList<Point3D> polygon,
        Vector3D desiredNormal,
        List<Point3D> positions,
        List<int> indices,
        List<Vector3D> normals)
    {
        if (polygon.Count < 3)
        {
            return false;
        }

        Point3D center = new(
            polygon.Average(point => point.X),
            polygon.Average(point => point.Y),
            polygon.Average(point => point.Z));

        Vector3D normal = desiredNormal.Length < 1e-10 ? ComputePolygonNormal(polygon.ToList()) : desiredNormal;
        if (normal.Length < 1e-10)
        {
            normal = new Vector3D(0, 0, 1);
        }

        Normalize(ref normal);

        int centerIndex = positions.Count;
        positions.Add(center);
        normals.Add(normal);

        for (int i = 0; i < polygon.Count; i++)
        {
            positions.Add(polygon[i]);
            normals.Add(normal);
        }

        for (int i = 0; i < polygon.Count; i++)
        {
            int current = centerIndex + 1 + i;
            int next = centerIndex + 1 + ((i + 1) % polygon.Count);
            indices.Add(centerIndex);
            indices.Add(current);
            indices.Add(next);
        }

        return true;
    }

    private static Point3D CatmullRom(Point3D p0, Point3D p1, Point3D p2, Point3D p3, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;

        return new Point3D(
            0.5 * ((2 * p1.X) + ((-p0.X + p2.X) * t) + ((2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2) + ((-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3)),
            0.5 * ((2 * p1.Y) + ((-p0.Y + p2.Y) * t) + ((2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2) + ((-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)),
            0.5 * ((2 * p1.Z) + ((-p0.Z + p2.Z) * t) + ((2 * p0.Z - 5 * p1.Z + 4 * p2.Z - p3.Z) * t2) + ((-p0.Z + 3 * p1.Z - 3 * p2.Z + p3.Z) * t3)));
    }
    private static Vector3D? ResolveSurfaceNormal(
        Parameter surfaceParam,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (surfaceParam is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? surface))
        {
            return null;
        }

        if (IsNamed(surface, "PLANE"))
        {
            if (surface.Parameters.Count < 2)
            {
                return null;
            }

            (_, Vector3D z, _) = ResolveAxis2(surface.Parameters[1], data);
            return z.Length < 1e-10 ? null : z;
        }

        return null;
    }

    private static string TryGetSurfaceType(
        EntityInstance face,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (face.Parameters.Count < 3 ||
            face.Parameters[2] is not Parameter.EntityReference surfaceRef ||
            !data.TryGetValue(surfaceRef.Id, out EntityInstance? surface))
        {
            return "<unknown>";
        }

        if (!string.IsNullOrWhiteSpace(surface.Name))
        {
            return surface.Name;
        }

        return surface.Components?
            .Select(component => component.Name)
            .FirstOrDefault(name => name.Contains("SURFACE", StringComparison.OrdinalIgnoreCase))
            ?? "<unknown>";
    }

    private static IReadOnlyList<Parameter>? TryGetParameters(EntityInstance entity, string componentName)
    {
        EntityComponent? component = entity.Components?
            .FirstOrDefault(candidate => string.Equals(candidate.Name, componentName, StringComparison.OrdinalIgnoreCase));
        if (component is not null)
        {
            return component.Parameters;
        }

        return IsNamed(entity, componentName) ? entity.Parameters : null;
    }

    private static (Point3D Center, Vector3D Z, Vector3D X) ResolveAxis2(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? axis) ||
            axis.Parameters.Count < 2)
        {
            return default;
        }

        Point3D? center = ResolveCartesianPointParam(axis.Parameters[1], data);
        Vector3D z = axis.Parameters.Count >= 3 ? ResolveDirectionParam(axis.Parameters[2], data) : new Vector3D(0, 0, 1);
        if (z.Length < 1e-10)
        {
            z = new Vector3D(0, 0, 1);
        }

        Vector3D x = axis.Parameters.Count >= 4 ? ResolveDirectionParam(axis.Parameters[3], data) : CreatePerpendicular(z);
        if (x.Length < 1e-10)
        {
            x = CreatePerpendicular(z);
        }

        x -= z * Vector3D.DotProduct(x, z);
        Normalize(ref z);
        Normalize(ref x);

        return (center ?? default, z, x);
    }

    private static Point3D? ResolveVertex(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? vertexPoint) ||
            !IsNamed(vertexPoint, "VERTEX_POINT") ||
            vertexPoint.Parameters.Count < 2)
        {
            return null;
        }

        return ResolveCartesianPointParam(vertexPoint.Parameters[1], data);
    }

    private static Point3D? ResolveCartesianPointParam(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? point) ||
            !IsNamed(point, "CARTESIAN_POINT") ||
            point.Parameters.Count < 2 ||
            point.Parameters[1] is not Parameter.ListValue coords ||
            coords.Items.Count < 2)
        {
            return null;
        }

        double x = ToDouble(coords.Items[0]);
        double y = ToDouble(coords.Items[1]);
        double z = coords.Items.Count >= 3 ? ToDouble(coords.Items[2]) : 0.0;
        return new Point3D(x, y, z);
    }

    private static Vector3D ResolveDirectionParam(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        if (param is not Parameter.EntityReference entityReference ||
            !data.TryGetValue(entityReference.Id, out EntityInstance? direction) ||
            !IsNamed(direction, "DIRECTION") ||
            direction.Parameters.Count < 2 ||
            direction.Parameters[1] is not Parameter.ListValue coords ||
            coords.Items.Count < 3)
        {
            return default;
        }

        var vector = new Vector3D(
            ToDouble(coords.Items[0]),
            ToDouble(coords.Items[1]),
            ToDouble(coords.Items[2]));
        Normalize(ref vector);
        return vector;
    }

    private static Vector3D ComputePolygonNormal(List<Point3D> polygon)
    {
        var normal = new Vector3D();
        for (int i = 0; i < polygon.Count; i++)
        {
            Point3D current = polygon[i];
            Point3D next = polygon[(i + 1) % polygon.Count];
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }

        if (normal.Length < 1e-10)
        {
            return new Vector3D(0, 0, 1);
        }

        Normalize(ref normal);
        return normal;
    }

    private static double AngleOnCircle(Vector3D vector, Vector3D xDirection, Vector3D yDirection) =>
        Math.Atan2(Vector3D.DotProduct(vector, yDirection), Vector3D.DotProduct(vector, xDirection));

    private static double UnwrapAngle(double angle, double? previousAngle)
    {
        if (!previousAngle.HasValue)
        {
            return angle;
        }

        while (angle - previousAngle.Value > Math.PI)
        {
            angle -= Math.PI * 2.0;
        }

        while (angle - previousAngle.Value < -Math.PI)
        {
            angle += Math.PI * 2.0;
        }

        return angle;
    }

    private static double ToDouble(Parameter parameter) => parameter switch
    {
        Parameter.RealValue realValue => realValue.Value,
        Parameter.IntegerValue integerValue => integerValue.Value,
        _ => 0.0
    };

    private static List<Point3D> Compact(Point3D?[] points) =>
        points.Where(point => point.HasValue).Select(point => point!.Value).ToList();

    private static Vector3D CreatePerpendicular(Vector3D vector)
    {
        Vector3D axis = Math.Abs(vector.Z) < 0.9 ? new Vector3D(0, 0, 1) : new Vector3D(1, 0, 0);
        Vector3D perpendicular = Vector3D.CrossProduct(vector, axis);
        if (perpendicular.Length < 1e-10)
        {
            perpendicular = Vector3D.CrossProduct(vector, new Vector3D(0, 1, 0));
        }

        Normalize(ref perpendicular);
        return perpendicular;
    }

    private static Point MidPoint(Point a, Point b) =>
        new((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    private static MeshGeometry3D CreateFrozenMesh(
        IReadOnlyList<Point3D> positions,
        IReadOnlyList<int> indices,
        IReadOnlyList<Vector3D> normals)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positions),
            TriangleIndices = new Int32Collection(indices),
            Normals = new Vector3DCollection(normals)
        };

        mesh.Freeze();
        return mesh;
    }

    private static void Normalize(ref Vector3D vector)
    {
        if (vector.Length >= 1e-10)
        {
            vector.Normalize();
        }
    }

    private static bool IsNamed(EntityInstance entity, string name) =>
        string.Equals(entity.Name, name, StringComparison.OrdinalIgnoreCase) ||
        (entity.Components?.Any(component => string.Equals(component.Name, name, StringComparison.OrdinalIgnoreCase)) ?? false);
}
