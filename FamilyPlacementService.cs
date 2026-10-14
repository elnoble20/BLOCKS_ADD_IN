using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BlockToFamily;

public sealed record PlacementReport(
    int TotalBlocksConsidered,
    int InstancesPlaced,
    int SkippedUnmatched,
    int SkippedDuplicate,
    int ErrorsCount,
    IReadOnlyList<string> UnmatchedBlockNames,
    IReadOnlyList<string> ErrorMessages);

/// <summary>
/// Places validated FamilySymbols on top of each DWG block's insertion point,
/// rotating each placed instance to match the DWG block's rotation angle.
/// </summary>
public static class FamilyPlacementService
{
    private const string TrackingCommentPrefix = "DWG_BLOCK:";

    public static PlacementReport PlaceValidatedFamilies(
        Document doc,
        Level level,
        IReadOnlyList<BlockReferenceInfo> blocks,
        IReadOnlyDictionary<string, BlockValidationSummary> validationByBlockName,
        bool useBlockZOffset = false,
        bool skipExistingDuplicates = true)
    {
        int considered = 0;
        int placed = 0;
        int skippedUnmatched = 0;
        int skippedDuplicate = 0;
        int errorsCount = 0;

        var unmatchedNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var errorMessages = new List<string>();

        // Index existing placed instances with DWG_BLOCK tracking comments to prevent duplicates on re-runs
        HashSet<string> existingSignatures = skipExistingDuplicates
            ? CollectExistingPlacedSignatures(doc)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var tg = new TransactionGroup(doc, "Place Revit Families from DWG Blocks");
        tg.Start();

        // Step 1: Activate all required FamilySymbols in their own Transaction
        using (var tActivate = new Transaction(doc, "Activate Matched Family Symbols"))
        {
            tActivate.Start();
            foreach (BlockValidationSummary summary in validationByBlockName.Values)
            {
                if (summary.PlaceSelected && summary.MatchedSymbol is { IsActive: false } symbol)
                {
                    symbol.Activate();
                }
            }
            tActivate.Commit();
        }

        // Step 2: Place FamilyInstances at each DWG block coordinate and rotate
        using (var tPlace = new Transaction(doc, "Place Family Instances on DWG Blocks"))
        {
            tPlace.Start();

            foreach (BlockReferenceInfo block in blocks)
            {
                if (!validationByBlockName.TryGetValue(block.BlockName, out BlockValidationSummary? summary) ||
                    !summary.PlaceSelected)
                {
                    continue;
                }

                considered++;

                if (summary.MatchedSymbol is null || !summary.IsValid)
                {
                    skippedUnmatched++;
                    unmatchedNames.Add(block.BlockName);
                    continue;
                }

                double targetZ = useBlockZOffset ? block.Origin.Z : level.Elevation;
                var placementPoint = new XYZ(block.Origin.X, block.Origin.Y, targetZ);

                string signature = BuildSignature(block.BlockName, placementPoint);
                if (skipExistingDuplicates && existingSignatures.Contains(signature))
                {
                    skippedDuplicate++;
                    continue;
                }

                try
                {
                    FamilyInstance instance = doc.Create.NewFamilyInstance(
                        placementPoint,
                        summary.MatchedSymbol,
                        level,
                        StructuralType.NonStructural);

                    // If placing at DWG Z elevation relative to level, set offset from host level
                    if (useBlockZOffset)
                    {
                        Parameter? offsetParam = instance.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM)
                            ?? instance.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
                        if (offsetParam is { IsReadOnly: false })
                        {
                            offsetParam.Set(block.Origin.Z - level.Elevation);
                        }
                    }

                    // Apply block Z rotation
                    if (Math.Abs(block.RotationRadians) > 1e-6)
                    {
                        Line zAxis = Line.CreateBound(placementPoint, placementPoint + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, instance.Id, zAxis, block.RotationRadians);
                    }

                    // Stamp Comments parameter for traceability and duplicate prevention
                    Parameter? commentsParam = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (commentsParam is { IsReadOnly: false })
                    {
                        commentsParam.Set($"{TrackingCommentPrefix}{block.BlockName}");
                    }

                    existingSignatures.Add(signature);
                    placed++;
                }
                catch (Exception ex)
                {
                    errorsCount++;
                    if (errorMessages.Count < 15)
                    {
                        errorMessages.Add($"{block.BlockName} at ({block.Origin.X:F2}, {block.Origin.Y:F2}): {ex.Message}");
                    }
                }
            }

            tPlace.Commit();
        }

        tg.Assimilate();

        return new PlacementReport(
            TotalBlocksConsidered: considered,
            InstancesPlaced: placed,
            SkippedUnmatched: skippedUnmatched,
            SkippedDuplicate: skippedDuplicate,
            ErrorsCount: errorsCount,
            UnmatchedBlockNames: unmatchedNames.ToList(),
            ErrorMessages: errorMessages);
    }

    public static Level? ResolveHostLevel(Document doc, View view, ImportInstance import)
    {
        if (view.GenLevel is { } viewLevel)
        {
            return viewLevel;
        }

        if (import.LevelId != ElementId.InvalidElementId &&
            doc.GetElement(import.LevelId) is Level importLevel)
        {
            return importLevel;
        }

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .FirstOrDefault();
    }

    private static HashSet<string> CollectExistingPlacedSignatures(Document doc)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var instances = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilyInstance))
            .Cast<FamilyInstance>();

        foreach (FamilyInstance fi in instances)
        {
            string? comment = fi.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (string.IsNullOrWhiteSpace(comment) ||
                !comment.StartsWith(TrackingCommentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fi.Location is LocationPoint lp)
            {
                string blockName = comment[TrackingCommentPrefix.Length..].Trim();
                set.Add(BuildSignature(blockName, lp.Point));
            }
        }

        return set;
    }

    private static string BuildSignature(string blockName, XYZ pt) =>
        $"{blockName.ToUpperInvariant()}|{pt.X:F2}|{pt.Y:F2}";
}
