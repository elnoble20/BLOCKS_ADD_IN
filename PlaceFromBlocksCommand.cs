using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BlockToFamily;

/// <summary>
/// Full 3-stage workflow command:
/// 1. Reads block names, coordinates, and rotations from a selected DWG ImportInstance.
/// 2. Validates the block list against loaded Revit FamilySymbols and opens the interactive
///    WPF validation/CSV export dialog (allowing CSV extraction and manual family remapping).
/// 3. Places the validated Revit FamilyInstances directly on top of each DWG block inside a Transaction.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class PlaceFromBlocksCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;
        View view = doc.ActiveView;

        // 1. Pick the DWG ImportInstance (or use current preselection)
        ImportInstance? import = ExportBlocksCsvCommand.SelectImportInstance(uidoc, doc);
        if (import is null)
        {
            return Result.Cancelled;
        }

        // 2. Read all nested block references from the DWG
        IList<BlockReferenceInfo> blocks = DwgBlockReader.ReadBlocks(import, view);
        if (blocks.Count == 0)
        {
            TaskDialog.Show("Blocks → Families", "No block references were found in the selected DWG import/link.");
            return Result.Succeeded;
        }

        // 3. Validate discovered DWG block names against loaded Revit families
        FamilyCatalog catalog = FamilyCatalog.Build(doc);
        List<BlockValidationSummary> summaries = catalog.ValidateBlocks(blocks);

        // 4. Resolve host level for placement
        Level? level = FamilyPlacementService.ResolveHostLevel(doc, view, import);
        if (level is null)
        {
            message = "Could not determine a host Level for placement. Open a plan view or assign a Level to the DWG import.";
            return Result.Failed;
        }

        // 5. Show interactive Validation, CSV Export & Family Mapping dialog
        string dwgName = ExportBlocksCsvCommand.ResolveDwgName(doc, import);
        var window = new BlockValidationWindow(dwgName, blocks, summaries, catalog);
        bool? confirmed = window.ShowDialog();
        if (confirmed != true)
        {
            return Result.Cancelled;
        }

        // 6. Place validated & selected FamilyInstances on top of each DWG block
        var validationByBlockName = summaries.ToDictionary(s => s.BlockName, StringComparer.OrdinalIgnoreCase);

        PlacementReport report = FamilyPlacementService.PlaceValidatedFamilies(
            doc: doc,
            level: level,
            blocks: blocks,
            validationByBlockName: validationByBlockName,
            useBlockZOffset: window.UseBlockZOffset,
            skipExistingDuplicates: window.SkipExistingDuplicates);

        // 7. Present final execution summary
        var sb = new StringBuilder();
        sb.AppendLine($"DWG Source: {dwgName}");
        sb.AppendLine($"Host Level: {level.Name}");
        sb.AppendLine($"Total DWG Blocks Read: {blocks.Count}");
        sb.AppendLine($"Queued for Placement: {report.TotalBlocksConsidered}");
        sb.AppendLine($"Family Instances Placed: {report.InstancesPlaced}");

        if (report.SkippedDuplicate > 0)
        {
            sb.AppendLine($"Skipped (Already Placed at XY): {report.SkippedDuplicate}");
        }

        if (report.SkippedUnmatched > 0)
        {
            sb.AppendLine($"Skipped (Unmatched Block Name): {report.SkippedUnmatched}");
        }

        if (report.UnmatchedBlockNames.Count > 0)
        {
            sb.AppendLine().AppendLine("Unmatched block names:");
            foreach (string name in report.UnmatchedBlockNames.Take(20))
            {
                sb.AppendLine($" • {name}");
            }
        }

        if (report.ErrorMessages.Count > 0)
        {
            sb.AppendLine().AppendLine($"Placement warnings ({report.ErrorsCount} total):");
            foreach (string err in report.ErrorMessages)
            {
                sb.AppendLine($" • {err}");
            }
        }

        TaskDialog.Show("Blocks → Families Placement Summary", sb.ToString());
        return Result.Succeeded;
    }
}
