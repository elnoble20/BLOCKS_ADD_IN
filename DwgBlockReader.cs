using Autodesk.Revit.DB;

namespace BlockToFamily;

/// <summary>
/// Represents a single DWG block reference discovered inside an ImportInstance.
/// </summary>
public sealed record BlockReferenceInfo(
    string BlockName,
    string RawSymbolName,
    string LayerName,
    XYZ Origin,
    double RotationRadians,
    double ScaleX,
    double ScaleY,
    bool IsMirrored,
    Transform Transform)
{
    public double RotationDegrees => RotationRadians * (180.0 / Math.PI);
}

/// <summary>
/// Traverses the geometry of a DWG ImportInstance to extract nested block references,
/// insertion points in Revit internal coordinates (feet), rotation angles, and layer names.
/// Uses GeometryInstance.GetSymbolGeometryId().SymbolId for Revit 2024–2027 compatibility.
/// </summary>
public static class DwgBlockReader
{
    /// <summary>
    /// Walks the geometry of an ImportInstance and returns every nested block reference.
    /// Supports multi-level nested blocks while ignoring anonymous AutoCAD blocks (*U, *D, etc.).
    /// </summary>
    public static IList<BlockReferenceInfo> ReadBlocks(
        ImportInstance import,
        View? view = null,
        bool includeAnonymousBlocks = false)
    {
        var results = new List<BlockReferenceInfo>();

        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = false
        };

        if (view is not null)
        {
            options.View = view;
        }

        GeometryElement? geomElem = import.get_Geometry(options);
        if (geomElem is null && view is not null)
        {
            // Fallback to model geometry if view-specific geometry returns empty
            geomElem = import.get_Geometry(new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false
            });
        }

        if (geomElem is null)
        {
            return results;
        }

        string importFilePrefix = GetImportFileNamePrefix(import);

        foreach (GeometryObject obj in geomElem)
        {
            if (obj is GeometryInstance topInstance)
            {
                Transform baseTransform = topInstance.Transform;
                GeometryElement? topSymbolGeometry = topInstance.GetSymbolGeometry();
                if (topSymbolGeometry is null)
                {
                    continue;
                }

                foreach (GeometryObject nested in topSymbolGeometry)
                {
                    if (nested is GeometryInstance blockInstance)
                    {
                        CollectBlockInstancesRecursive(
                            blockInstance,
                            baseTransform,
                            import.Document,
                            importFilePrefix,
                            includeAnonymousBlocks,
                            depth: 0,
                            results);
                    }
                }
            }
        }

        return results;
    }

    private static void CollectBlockInstancesRecursive(
        GeometryInstance block,
        Transform parentTransform,
        Document doc,
        string importFilePrefix,
        bool includeAnonymousBlocks,
        int depth,
        IList<BlockReferenceInfo> results)
    {
        if (depth > 8)
        {
            return;
        }

        string rawName = GetGeometrySymbolName(block, doc);
        string normalizedName = NormalizeName(rawName, importFilePrefix);

        Transform worldTransform = parentTransform.Multiply(block.Transform);

        if (!string.IsNullOrWhiteSpace(normalizedName) &&
            (includeAnonymousBlocks || !IsAnonymousOrSystemBlock(normalizedName)))
        {
            results.Add(ToInfo(block, rawName, normalizedName, worldTransform, doc));
            return;
        }

        // If this container is an anonymous block or wrapper, inspect one level deeper
        GeometryElement? nestedGeom = block.GetSymbolGeometry();
        if (nestedGeom is null)
        {
            return;
        }

        foreach (GeometryObject child in nestedGeom)
        {
            if (child is GeometryInstance childInstance)
            {
                CollectBlockInstancesRecursive(
                    childInstance,
                    worldTransform,
                    doc,
                    importFilePrefix,
                    includeAnonymousBlocks,
                    depth + 1,
                    results);
            }
        }
    }

    private static BlockReferenceInfo ToInfo(
        GeometryInstance block,
        string rawName,
        string normalizedName,
        Transform worldTransform,
        Document doc)
    {
        XYZ origin = worldTransform.Origin;

        double scaleX = worldTransform.BasisX.GetLength();
        double scaleY = worldTransform.BasisY.GetLength();

        XYZ unitX = scaleX > 1e-9 ? worldTransform.BasisX / scaleX : XYZ.BasisX;
        double rotation = Math.Atan2(unitX.Y, unitX.X);

        // Determinant in XY plane indicates whether the block reference is mirrored
        XYZ crossXY = worldTransform.BasisX.CrossProduct(worldTransform.BasisY);
        bool isMirrored = crossXY.DotProduct(XYZ.BasisZ) < 0;

        string layerName = ResolveLayerName(block, doc);

        return new BlockReferenceInfo(
            BlockName: normalizedName,
            RawSymbolName: rawName,
            LayerName: layerName,
            Origin: origin,
            RotationRadians: rotation,
            ScaleX: scaleX,
            ScaleY: scaleY,
            IsMirrored: isMirrored,
            Transform: worldTransform);
    }

    /// <summary>
    /// Resolves the CAD symbol name using GeometryInstance.GetSymbolGeometryId().SymbolId
    /// (required in Revit 2024–2027 after GeometryInstance.Symbol was removed).
    /// </summary>
    private static string GetGeometrySymbolName(GeometryInstance instance, Document doc)
    {
        try
        {
            SymbolGeometryId symGeomId = instance.GetSymbolGeometryId();
            if (symGeomId is not null && symGeomId.SymbolId != ElementId.InvalidElementId)
            {
                Element? symElem = doc.GetElement(symGeomId.SymbolId);
                if (symElem is not null && !string.IsNullOrWhiteSpace(symElem.Name))
                {
                    return symElem.Name;
                }
            }
        }
        catch
        {
            // Ignore and try reflection fallback below
        }

        if (instance.GetType().GetProperty("Symbol")?.GetValue(instance) is Element legacySym &&
            !string.IsNullOrWhiteSpace(legacySym.Name))
        {
            return legacySym.Name;
        }

        return string.Empty;
    }

    private static string ResolveLayerName(GeometryObject geomObj, Document doc)
    {
        if (geomObj.GraphicsStyleId != ElementId.InvalidElementId &&
            doc.GetElement(geomObj.GraphicsStyleId) is GraphicsStyle gs &&
            gs.GraphicsStyleCategory is Category cat &&
            !string.IsNullOrWhiteSpace(cat.Name))
        {
            return cat.Name;
        }

        return string.Empty;
    }

    private static string GetImportFileNamePrefix(ImportInstance import)
    {
        try
        {
            if (import.Document.GetElement(import.GetTypeId()) is ElementType type &&
                !string.IsNullOrWhiteSpace(type.Name))
            {
                return type.Name.Trim();
            }
        }
        catch
        {
            // Ignore
        }

        return string.Empty;
    }

    /// <summary>
    /// Revit frequently prefixes DWG block names with the CAD filename (e.g. "FactoryLayout.dwg.BENCH_A").
    /// Strips any "<filename>.dwg." prefix and trims whitespace so the clean block name can match Revit families.
    /// </summary>
    public static string NormalizeName(string raw, string importFilePrefix = "")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        string cleaned = raw.Trim();

        int dwgIdx = cleaned.LastIndexOf(".dwg.", StringComparison.OrdinalIgnoreCase);
        if (dwgIdx >= 0)
        {
            cleaned = cleaned[(dwgIdx + 5)..].Trim();
        }
        else if (!string.IsNullOrWhiteSpace(importFilePrefix) &&
                 cleaned.StartsWith(importFilePrefix + ".", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[(importFilePrefix.Length + 1)..].Trim();
        }

        return cleaned;
    }

    /// <summary>
    /// Filters out internal AutoCAD anonymous blocks (*U12, *D5, *X, A$C...) and paper-space definitions.
    /// </summary>
    public static bool IsAnonymousOrSystemBlock(string blockName)
    {
        if (string.IsNullOrWhiteSpace(blockName))
        {
            return true;
        }

        if (blockName.StartsWith('*') ||
            blockName.StartsWith("A$C", StringComparison.OrdinalIgnoreCase) ||
            blockName.StartsWith("_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
