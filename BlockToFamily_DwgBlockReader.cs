using Autodesk.Revit.DB;

namespace BlockToFamily;

/// <summary>A single block reference found inside a DWG ImportInstance.</summary>
public sealed record BlockReferenceInfo(string BlockName, XYZ Origin, double RotationRadians, Transform Transform);

public static class DwgBlockReader
{
    /// <summary>
    /// Walks the geometry of an ImportInstance and returns every nested block
    /// reference (name + world-space insertion point + rotation about Z).
    /// </summary>
    public static IList<BlockReferenceInfo> ReadBlocks(ImportInstance import, View view)
    {
        var results = new List<BlockReferenceInfo>();

        var options = new Options
        {
            View = view,                 // use the view so layer/visibility settings apply
            ComputeReferences = false,
            IncludeNonVisibleObjects = false
        };

        GeometryElement? geomElem = import.get_Geometry(options);
        if (geomElem is null) return results;

        // Top-level: the import itself is a GeometryInstance carrying the DWG's base transform.
        foreach (GeometryObject obj in geomElem)
        {
            if (obj is GeometryInstance topInstance)
            {
                Transform baseTransform = topInstance.Transform;
                foreach (GeometryObject nested in topInstance.GetSymbolGeometry())
                {
                    // Each nested GeometryInstance corresponds to a block reference.
                    if (nested is GeometryInstance block)
                        results.Add(ToInfo(block, baseTransform));
                }
            }
        }

        return results;
    }

    private static BlockReferenceInfo ToInfo(GeometryInstance block, Transform baseTransform)
    {
        // Symbol.Name is the DWG block name (e.g. "CHAIR-01").
        string name = block.Symbol?.Name ?? string.Empty;

        Transform world = baseTransform.Multiply(block.Transform);
        XYZ origin = world.Origin;

        // Rotation about Z derived from the transformed X basis.
        double rotation = Math.Atan2(world.BasisX.Y, world.BasisX.X);

        return new BlockReferenceInfo(NormalizeName(name), origin, rotation, world);
    }

    /// <summary>
    /// Revit sometimes prefixes nested block names with the file name, e.g. "site.dwg.CHAIR-01".
    /// Keep only the last segment so it matches a plain family/type name.
    /// </summary>
    public static string NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        int idx = raw.LastIndexOf(".dwg.", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? raw[(idx + 5)..].Trim() : raw.Trim();
    }
}