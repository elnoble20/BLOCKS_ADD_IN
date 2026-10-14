using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;

namespace BlockToFamily;

/// <summary>
/// Handles exporting DWG block validation summaries and per-instance block lists to CSV,
/// as well as importing user-edited CSV validation mappings back into Revit.
/// </summary>
public static class CsvBlockService
{
    /// <summary>
    /// Writes the unique DWG block summary &amp; Revit family validation table to a CSV file.
    /// Columns: BlockName,InstanceCount,Layer,ValidationStatus,IsValid,MatchedFamilyName,MatchedTypeName,Category,PlacementType
    /// </summary>
    public static void ExportValidationSummaryCsv(
        string filePath,
        IEnumerable<BlockValidationSummary> summaries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BlockName,InstanceCount,Layer,ValidationStatus,IsValid,MatchedFamilyName,MatchedTypeName,Category,PlacementType");

        foreach (BlockValidationSummary row in summaries)
        {
            sb.Append(EscapeCsv(row.BlockName)).Append(',')
              .Append(row.InstanceCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(EscapeCsv(row.SampleLayer)).Append(',')
              .Append(EscapeCsv(row.ValidationStatusText)).Append(',')
              .Append(row.IsValid ? "TRUE" : "FALSE").Append(',')
              .Append(EscapeCsv(row.MatchedFamilyName)).Append(',')
              .Append(EscapeCsv(row.MatchedTypeName)).Append(',')
              .Append(EscapeCsv(row.CategoryName)).Append(',')
              .Append(EscapeCsv(row.PlacementTypeName))
              .AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// Writes every individual DWG block reference (with coordinates, rotation, and validation status) to a CSV file.
    /// Columns: BlockName,RawSymbolName,Layer,OriginX_Ft,OriginY_Ft,OriginZ_Ft,RotationDegrees,ScaleX,ScaleY,IsMirrored,IsValid,MatchedFamilyName,MatchedTypeName
    /// </summary>
    public static void ExportBlockInstancesCsv(
        string filePath,
        IEnumerable<BlockReferenceInfo> blocks,
        IReadOnlyDictionary<string, BlockValidationSummary> validationByBlockName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BlockName,RawSymbolName,Layer,OriginX_Ft,OriginY_Ft,OriginZ_Ft,RotationDegrees,ScaleX,ScaleY,IsMirrored,IsValid,MatchedFamilyName,MatchedTypeName");

        foreach (BlockReferenceInfo b in blocks)
        {
            validationByBlockName.TryGetValue(b.BlockName, out BlockValidationSummary? summary);
            bool isValid = summary?.IsValid ?? false;
            string famName = summary?.MatchedFamilyName ?? string.Empty;
            string typeName = summary?.MatchedTypeName ?? string.Empty;

            sb.Append(EscapeCsv(b.BlockName)).Append(',')
              .Append(EscapeCsv(b.RawSymbolName)).Append(',')
              .Append(EscapeCsv(b.LayerName)).Append(',')
              .Append(b.Origin.X.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.Origin.Y.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.Origin.Z.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.RotationDegrees.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.ScaleX.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.ScaleY.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(b.IsMirrored ? "TRUE" : "FALSE").Append(',')
              .Append(isValid ? "TRUE" : "FALSE").Append(',')
              .Append(EscapeCsv(famName)).Append(',')
              .Append(EscapeCsv(typeName))
              .AppendLine();
        }

        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// Reads an edited validation CSV and updates the MatchedSymbol on any row where
    /// the user specified a MatchedFamilyName and/or MatchedTypeName.
    /// Returns the number of rows updated.
    /// </summary>
    public static int ImportValidationOverridesFromCsv(
        string filePath,
        IList<BlockValidationSummary> summaries,
        FamilyCatalog catalog)
    {
        if (!File.Exists(filePath))
        {
            return 0;
        }

        string[] lines = File.ReadAllLines(filePath);
        if (lines.Length <= 1)
        {
            return 0;
        }

        List<string> headers = ParseCsvLine(lines[0]);
        int idxBlock = FindColumnIndex(headers, "BlockName", "Block Name", "Block");
        int idxFamily = FindColumnIndex(headers, "MatchedFamilyName", "FamilyName", "Family Name", "Family");
        int idxType = FindColumnIndex(headers, "MatchedTypeName", "TypeName", "Type Name", "Type");

        if (idxBlock < 0 || (idxFamily < 0 && idxType < 0))
        {
            return 0;
        }

        var summaryMap = summaries.ToDictionary(s => s.BlockName, StringComparer.OrdinalIgnoreCase);
        int updated = 0;

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            List<string> cols = ParseCsvLine(lines[i]);
            if (idxBlock >= cols.Count)
            {
                continue;
            }

            string blockName = cols[idxBlock].Trim();
            if (!summaryMap.TryGetValue(blockName, out BlockValidationSummary? target))
            {
                continue;
            }

            string? fam = idxFamily >= 0 && idxFamily < cols.Count ? cols[idxFamily].Trim() : null;
            string? typ = idxType >= 0 && idxType < cols.Count ? cols[idxType].Trim() : null;

            FamilySymbol? resolved = catalog.ResolveByFamilyAndType(fam, typ);
            if (resolved is not null)
            {
                target.MatchedSymbol = resolved;
                target.MatchKind = BlockMatchKind.ManualOverride;
                target.PlaceSelected = true;
                updated++;
            }
        }

        return updated;
    }

    private static int FindColumnIndex(IReadOnlyList<string> headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string h = headers[i].Trim().Replace("_", "").Replace(" ", "");
            foreach (string candidate in candidates)
            {
                string c = candidate.Replace("_", "").Replace(" ", "");
                if (string.Equals(h, c, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }
        return -1;
    }

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('
') || value.Contains(''))
        {
            return $""{value.Replace(""", """")}"";
        }

        return value;
    }

    public static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        result.Add(current.ToString());
        return result;
    }
}
