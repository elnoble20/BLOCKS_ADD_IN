using Autodesk.Revit.DB;

namespace BlockToFamily;

public enum BlockMatchKind
{
    Unmatched = 0,
    MatchedByTypeName = 1,
    MatchedByFamilyName = 2,
    MatchedByFullKey = 3,
    ManualOverride = 4
}

/// <summary>
/// Represents the validation and mapping state for a unique DWG block name.
/// </summary>
public sealed class BlockValidationSummary
{
    public required string BlockName { get; init; }
    public required int InstanceCount { get; init; }
    public required string SampleLayer { get; init; }
    public BlockMatchKind MatchKind { get; set; }
    public FamilySymbol? MatchedSymbol { get; set; }
    public bool PlaceSelected { get; set; }

    public bool IsValid => MatchedSymbol is not null && IsPlaceableSymbol(MatchedSymbol);

    public string ValidationStatusText => MatchKind switch
    {
        BlockMatchKind.MatchedByTypeName => "Valid (Matched Type Name)",
        BlockMatchKind.MatchedByFamilyName => "Valid (Matched Family Name)",
        BlockMatchKind.MatchedByFullKey => "Valid (Matched Family:Type)",
        BlockMatchKind.ManualOverride => "Valid (Mapped Override)",
        _ => "Missing (No Matching Revit Family)"
    };

    public string MatchedFamilyName => MatchedSymbol?.Family?.Name ?? string.Empty;
    public string MatchedTypeName => MatchedSymbol?.Name ?? string.Empty;
    public string CategoryName => MatchedSymbol?.Category?.Name ?? string.Empty;
    public string PlacementTypeName => MatchedSymbol?.Family?.FamilyPlacementType.ToString() ?? string.Empty;

    public static bool IsPlaceableSymbol(FamilySymbol symbol)
    {
        FamilyPlacementType? pt = symbol.Family?.FamilyPlacementType;
        return pt == FamilyPlacementType.OneLevelBased
            || pt == FamilyPlacementType.WorkPlaneBased
            || pt == FamilyPlacementType.OneLevelBasedHosted
            || pt == FamilyPlacementType.TwoLevelsBased;
    }
}

/// <summary>
/// Indexes all loaded FamilySymbols in the active Revit Document and validates
/// discovered DWG block names against Type Name, Family Name, or "Family : Type".
/// </summary>
public sealed class FamilyCatalog
{
    public IReadOnlyList<FamilySymbol> PlaceableSymbols { get; }
    private readonly Dictionary<string, FamilySymbol> _byTypeName;
    private readonly Dictionary<string, FamilySymbol> _byFamilyName;
    private readonly Dictionary<string, FamilySymbol> _byFullKey;

    private FamilyCatalog(
        IReadOnlyList<FamilySymbol> placeableSymbols,
        Dictionary<string, FamilySymbol> byTypeName,
        Dictionary<string, FamilySymbol> byFamilyName,
        Dictionary<string, FamilySymbol> byFullKey)
    {
        PlaceableSymbols = placeableSymbols;
        _byTypeName = byTypeName;
        _byFamilyName = byFamilyName;
        _byFullKey = byFullKey;
    }

    public static FamilyCatalog Build(Document doc)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(BlockValidationSummary.IsPlaceableSymbol)
            .OrderBy(s => s.Family?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byTypeName = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        var byFamilyName = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        var byFullKey = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);

        foreach (FamilySymbol sym in symbols)
        {
            string familyName = sym.Family?.Name ?? string.Empty;
            string typeName = sym.Name ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(typeName))
            {
                byTypeName.TryAdd(typeName.Trim(), sym);
            }

            if (!string.IsNullOrWhiteSpace(familyName))
            {
                byFamilyName.TryAdd(familyName.Trim(), sym);
            }

            if (!string.IsNullOrWhiteSpace(familyName) && !string.IsNullOrWhiteSpace(typeName))
            {
                byFullKey.TryAdd($"{familyName.Trim()}:{typeName.Trim()}", sym);
                byFullKey.TryAdd($"{familyName.Trim()} : {typeName.Trim()}", sym);
            }
        }

        return new FamilyCatalog(symbols, byTypeName, byFamilyName, byFullKey);
    }

    /// <summary>
    /// Validates all discovered DWG blocks against the current Revit document's loaded families.
    /// </summary>
    public List<BlockValidationSummary> ValidateBlocks(IEnumerable<BlockReferenceInfo> blocks)
    {
        var summaries = new List<BlockValidationSummary>();

        var grouped = blocks
            .GroupBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, BlockReferenceInfo> group in grouped)
        {
            string blockName = group.Key;
            string sampleLayer = group.Select(x => x.LayerName).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? string.Empty;

            (FamilySymbol? symbol, BlockMatchKind kind) = ResolveSymbol(blockName);

            summaries.Add(new BlockValidationSummary
            {
                BlockName = blockName,
                InstanceCount = group.Count(),
                SampleLayer = sampleLayer,
                MatchKind = kind,
                MatchedSymbol = symbol,
                PlaceSelected = symbol is not null
            });
        }

        return summaries;
    }

    public (FamilySymbol? Symbol, BlockMatchKind Kind) ResolveSymbol(string nameOrKey)
    {
        if (string.IsNullOrWhiteSpace(nameOrKey))
        {
            return (null, BlockMatchKind.Unmatched);
        }

        string key = nameOrKey.Trim();

        // Priority 1: Exact Type Name match
        if (_byTypeName.TryGetValue(key, out FamilySymbol? byType))
        {
            return (byType, BlockMatchKind.MatchedByTypeName);
        }

        // Priority 2: Exact Family Name match (uses first type in family)
        if (_byFamilyName.TryGetValue(key, out FamilySymbol? byFam))
        {
            return (byFam, BlockMatchKind.MatchedByFamilyName);
        }

        // Priority 3: "Family : Type" composite key
        if (_byFullKey.TryGetValue(key, out FamilySymbol? byComposite))
        {
            return (byComposite, BlockMatchKind.MatchedByFullKey);
        }

        return (null, BlockMatchKind.Unmatched);
    }

    public FamilySymbol? ResolveByFamilyAndType(string? familyName, string? typeName)
    {
        string fam = familyName?.Trim() ?? string.Empty;
        string typ = typeName?.Trim() ?? string.Empty;

        if (!string.IsNullOrEmpty(fam) && !string.IsNullOrEmpty(typ) &&
            _byFullKey.TryGetValue($"{fam}:{typ}", out FamilySymbol? exact))
        {
            return exact;
        }

        if (!string.IsNullOrEmpty(typ) && _byTypeName.TryGetValue(typ, out FamilySymbol? byType))
        {
            return byType;
        }

        if (!string.IsNullOrEmpty(fam) && _byFamilyName.TryGetValue(fam, out FamilySymbol? byFam))
        {
            return byFam;
        }

        return null;
    }
}
