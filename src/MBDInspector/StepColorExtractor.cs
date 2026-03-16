using System;
using System.Collections.Generic;
using System.Windows.Media;
using StepParser.Parser;

namespace MBDInspector;

/// <summary>
/// Walks the STEP presentation colour chain and returns a map from
/// entity ID (typically ADVANCED_FACE or MANIFOLD_SOLID_BREP) → WPF Color.
///
/// Chain:
///   STYLED_ITEM('', (#psa_ids...), #styled_entity_id)
///   └─ PRESENTATION_STYLE_ASSIGNMENT((#ssu_ids...))
///      └─ SURFACE_STYLE_USAGE(.BOTH., #surface_side_style_id)
///         └─ SURFACE_SIDE_STYLE('', (#ssfa_ids...))
///            └─ SURFACE_STYLE_FILL_AREA(#fill_area_style_id)
///               └─ FILL_AREA_STYLE('', (#fasc_ids...))
///                  └─ FILL_AREA_STYLE_COLOUR('', #colour_rgb_id)
///                     └─ COLOUR_RGB('', r, g, b)   // values in [0..1]
/// </summary>
public static class StepColorExtractor
{
    public static Dictionary<int, Color> Extract(
        IReadOnlyDictionary<int, EntityInstance> data)
    {
        var map = new Dictionary<int, Color>();

        foreach (var (_, entity) in data)
        {
            if (!IsNamed(entity, "STYLED_ITEM")) continue;
            // params[1] = list of PRESENTATION_STYLE_ASSIGNMENT refs
            // params[2] = styled entity ref (ADVANCED_FACE, SOLID, etc.)
            if (entity.Parameters.Count < 3) continue;
            if (entity.Parameters[2] is not Parameter.EntityReference itemRef) continue;
            if (entity.Parameters[1] is not Parameter.ListValue psaList) continue;

            foreach (Parameter psaRef in psaList.Items)
            {
                Color? c = ResolvePsa(psaRef, data);
                if (c.HasValue) { map[itemRef.Id] = c.Value; break; }
            }
        }

        return map;
    }

    // ── Chain resolution ──────────────────────────────────────────────────

    private static Color? ResolvePsa(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // PRESENTATION_STYLE_ASSIGNMENT((#ssu...))
        // params[0] = list of SURFACE_STYLE_USAGE refs
        if (!TryGetEntity(param, data, "PRESENTATION_STYLE_ASSIGNMENT", out var psa)) return null;
        if (psa!.Parameters.Count < 1) return null;
        if (psa.Parameters[0] is not Parameter.ListValue ssuList) return null;

        foreach (Parameter ssuRef in ssuList.Items)
        {
            Color? c = ResolveSsu(ssuRef, data);
            if (c.HasValue) return c;
        }
        return null;
    }

    private static Color? ResolveSsu(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // SURFACE_STYLE_USAGE(.BOTH., #surface_side_style)
        // params[1] = SURFACE_SIDE_STYLE ref
        if (!TryGetEntity(param, data, "SURFACE_STYLE_USAGE", out var ssu)) return null;
        if (ssu!.Parameters.Count < 2) return null;
        return ResolveSss(ssu.Parameters[1], data);
    }

    private static Color? ResolveSss(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // SURFACE_SIDE_STYLE('', (#ssfa...))
        // params[1] = list of SURFACE_STYLE_FILL_AREA refs
        if (!TryGetEntity(param, data, "SURFACE_SIDE_STYLE", out var sss)) return null;
        if (sss!.Parameters.Count < 2) return null;
        if (sss.Parameters[1] is not Parameter.ListValue ssfaList) return null;

        foreach (Parameter ssfaRef in ssfaList.Items)
        {
            Color? c = ResolveSsfa(ssfaRef, data);
            if (c.HasValue) return c;
        }
        return null;
    }

    private static Color? ResolveSsfa(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // SURFACE_STYLE_FILL_AREA(#fill_area_style)
        // params[0] = FILL_AREA_STYLE ref
        if (!TryGetEntity(param, data, "SURFACE_STYLE_FILL_AREA", out var ssfa)) return null;
        if (ssfa!.Parameters.Count < 1) return null;
        return ResolveFas(ssfa.Parameters[0], data);
    }

    private static Color? ResolveFas(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // FILL_AREA_STYLE('', (#fasc...))
        // params[1] = list containing FILL_AREA_STYLE_COLOUR refs
        if (!TryGetEntity(param, data, "FILL_AREA_STYLE", out var fas)) return null;
        if (fas!.Parameters.Count < 2) return null;
        if (fas.Parameters[1] is not Parameter.ListValue fascList) return null;

        foreach (Parameter fascRef in fascList.Items)
        {
            Color? c = ResolveFasc(fascRef, data);
            if (c.HasValue) return c;
        }
        return null;
    }

    private static Color? ResolveFasc(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // FILL_AREA_STYLE_COLOUR('', #colour_rgb)
        // params[1] = COLOUR_RGB ref
        if (!TryGetEntity(param, data, "FILL_AREA_STYLE_COLOUR", out var fasc)) return null;
        if (fasc!.Parameters.Count < 2) return null;
        return ResolveRgb(fasc.Parameters[1], data);
    }

    private static Color? ResolveRgb(
        Parameter param, IReadOnlyDictionary<int, EntityInstance> data)
    {
        // COLOUR_RGB('', r, g, b)   values in [0..1]
        if (!TryGetEntity(param, data, "COLOUR_RGB", out var rgb)) return null;
        if (rgb!.Parameters.Count < 4) return null;
        byte r = ToByte(rgb.Parameters[1]);
        byte g = ToByte(rgb.Parameters[2]);
        byte b = ToByte(rgb.Parameters[3]);
        return Color.FromRgb(r, g, b);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool TryGetEntity(
        Parameter param,
        IReadOnlyDictionary<int, EntityInstance> data,
        string expectedName,
        out EntityInstance? entity)
    {
        entity = null;
        if (param is not Parameter.EntityReference eref) return false;
        if (!data.TryGetValue(eref.Id, out entity)) return false;
        return IsNamed(entity, expectedName);
    }

    private static byte ToByte(Parameter p)
    {
        double v = p switch
        {
            Parameter.RealValue    r => r.Value,
            Parameter.IntegerValue i => (double)i.Value,
            _                        => 0.0
        };
        return (byte)Math.Clamp(v * 255.0, 0, 255);
    }

    private static bool IsNamed(EntityInstance e, string name) =>
        string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase);
}
