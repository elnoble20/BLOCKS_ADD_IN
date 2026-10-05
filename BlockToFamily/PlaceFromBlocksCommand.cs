using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace BlockToFamily;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class PlaceFromBlocksCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        // 1. Pick the DWG import/link
        ImportInstance import;
        try
        {
            Reference r = uidoc.Selection.PickObject(ObjectType.Element, new ImportFilter(), "Select a DWG import or link");
            import = (ImportInstance)doc.GetElement(r);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }

        // 2. Read block references
        IList<BlockReferenceInfo> blocks = DwgBlockReader.ReadBlocks(import, view);
        if (blocks.Count == 0)
        {
            TaskDialog.Show("Blocks → Families", "No block references were found in the selected import.");
            return Result.Succeeded;
        }

        // 3. Index loaded family types by name (type name first, then family name as fallback)
        Dictionary<string, FamilySymbol> symbolsByName = BuildSymbolIndex(doc);

        // 4. Determine a host level
        Level? level = ResolveLevel(doc, view, import);
        if (level is null)
        {
            message = "Could not determine a level for placement. Run from a plan view or set the import's level.";
            return Result.Failed;
        }

        // 5. Place
        int placed = 0;
        var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var tg = new TransactionGroup(doc, "Place families from DWG blocks"))
        {
            tg.Start();

            // Activate any needed symbols in their own transaction (required before placement).
            using (var tAct = new Transaction(doc, "Activate family types"))
            {
                tAct.Start();
                foreach (var b in blocks)
                {
                    if (symbolsByName.TryGetValue(b.BlockName, out var sym) && !sym.IsActive)
                        sym.Activate();
                }
                tAct.Commit();
            }

            using (var t = new Transaction(doc, "Place family instances"))
            {
                t.Start();
                foreach (var b in blocks)
                {
                    if (!symbolsByName.TryGetValue(b.BlockName, out FamilySymbol? symbol))
                    {
                        unmatched.Add(b.BlockName);
                        continue;
                    }

                    // Flatten Z to the level so instances sit on the level plane.
                    XYZ point = new(b.Origin.X, b.Origin.Y, level.Elevation);

                    FamilyInstance fi = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);

                    if (Math.Abs(b.RotationRadians) > 1e-6)
                    {
                        Line axis = Line.CreateBound(point, point + XYZ.BasisZ);
                        ElementTransformUtils.RotateElement(doc, fi.Id, axis, b.RotationRadians);
                    }
                    placed++;
                }
                t.Commit();
            }

            tg.Assimilate();
        }

        // 6. Report
        var sb = new StringBuilder();
        sb.AppendLine($"Blocks found: {blocks.Count}");
        sb.AppendLine($"Instances placed: {placed}");
        if (unmatched.Count > 0)
        {
            sb.AppendLine().AppendLine("No family type with these names was found:");
            foreach (var n in unmatched) sb.AppendLine($"  • {n}");
        }
        TaskDialog.Show("Blocks → Families", sb.ToString());

        return Result.Succeeded;
    }

    private static Dictionary<string, FamilySymbol> BuildSymbolIndex(Document doc)
    {
        var dict = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);

        var symbols = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .Where(s => s.Family?.FamilyPlacementType == FamilyPlacementType.OneLevelBased
                     || s.Family?.FamilyPlacementType == FamilyPlacementType.WorkPlaneBased
                     || s.Family?.FamilyPlacementType == FamilyPlacementType.OneLevelBasedHosted);

        // Pass 1: type name wins
        foreach (var s in symbols)
            dict.TryAdd(s.Name, s);

        // Pass 2: family name as fallback (does not overwrite type-name matches)
        foreach (var s in symbols)
            if (s.Family is { } f) dict.TryAdd(f.Name, s);

        return dict;
    }

    private static Level? ResolveLevel(Document doc, View view, ImportInstance import)
    {
        if (view.GenLevel is { } viewLevel) return viewLevel;

        if (import.LevelId != ElementId.InvalidElementId && doc.GetElement(import.LevelId) is Level importLevel)
            return importLevel;

        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .FirstOrDefault();
    }

    private sealed class ImportFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is ImportInstance;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}