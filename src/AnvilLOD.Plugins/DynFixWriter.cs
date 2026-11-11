using System.Globalization;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AnvilLOD.Plugins;

/// <summary>
/// The write pass for an APPROVED plan (<see cref="DynRefReport.Plan"/>): writes edited COPIES of DynDOLOD.esp and of the data files that need a change into
/// a new folder. The originals in the DynDOLOD output are never touched, an existing non-empty output folder is refused, and the plan is recomputed
/// from the load order and must equal the approved list, so what is written is what was reviewed.
/// </summary>
public static class DynFixWriter
{
    public sealed record Result(string Folder, string Plugin, int Repositioned, int Sunk, IReadOnlyList<string> DataFiles, int DataRowsEdited, IReadOnlyList<string> Verification);

    private const float SunkZ = -30000f;

    public static Result Apply(DynRefReport report, string approvedPlanCsv, string dyndolodOutput, string outputFolder, IProgress<string>? progress = null)
    {
        // --- guards -------------------------------------------------------------------------------------------------------------------------
        var fullOut = Path.GetFullPath(outputFolder);
        var fullSrc = Path.GetFullPath(dyndolodOutput);
        if (string.Equals(fullOut.TrimEnd('\\', '/'), fullSrc.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || fullOut.StartsWith(fullSrc.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The output folder must not be the DynDOLOD output or inside it: the originals are never written.");
        if (Directory.Exists(fullOut) && Directory.EnumerateFileSystemEntries(fullOut).Any())
            throw new InvalidOperationException($"{fullOut} already has files. Choose a new folder: nothing is overwritten.");

        var espPath = Path.Combine(dyndolodOutput, "DynDOLOD.esp");
        if (!File.Exists(espPath)) throw new FileNotFoundException("DynDOLOD.esp not found in the DynDOLOD output.", espPath);

        // --- the approved plan must equal the plan the load order gives now --------------------------------------------------------------------
        var approved = ReadApproved(approvedPlanCsv);
        var live = report.Plan().Select(i => (i.Action.ToString(), i.DynamicRef)).ToHashSet();
        var onlyApproved = approved.Except(live).ToList();
        var onlyLive = live.Except(approved).ToList();
        if (onlyApproved.Count > 0 || onlyLive.Count > 0)
            throw new InvalidOperationException($"The approved plan no longer matches the load order ({onlyApproved.Count} approved items are gone, {onlyLive.Count} new ones appeared). "
                + "Nothing was written. Run dyncheck --plan again and review it.");
        if (report.Plan().Any(i => i.Action == DynPlanAction.Investigate))
            throw new InvalidOperationException("The plan has Investigate items: those cannot be fixed from the data files. Resolve them first.");
        if (report.Problems.Any(f => f.Row.Ng))
            throw new InvalidOperationException("The data files are DynDOLOD NG format (DynDOLOD_NG_<World>.txt): checking them works, writing fixes for them is not supported yet.");

        // --- the plugin ------------------------------------------------------------------------------------------------------------------------
        progress?.Report("Reading DynDOLOD.esp...");
        var modKey = ModKey.FromNameAndExtension("DynDOLOD.esp");
        var mod = SkyrimMod.CreateFromBinary(new ModPath(modKey, espPath), SkyrimRelease.SkyrimSE);
        var refs = new Dictionary<FormKey, IPlacedObject>();
        foreach (var r in mod.EnumerateMajorRecords<IPlacedObject>()) refs[r.FormKey] = r;

        int repositioned = 0, sunk = 0;
        foreach (var f in report.Problems)
        {
            if (!FormKey.TryFactory($"{f.Row.DynId:X6}:DynDOLOD.esp", out var dk) || !refs.TryGetValue(dk, out var rec))
                throw new InvalidOperationException($"{f.Row.DynId:X6}:DynDOLOD.esp is not in DynDOLOD.esp.");
            rec.Placement ??= new Placement();
            switch (f.Issue)
            {
                case DynRefIssue.Moved when f.Place is { } p && Math.Abs(p.SrcPosition.Z - SunkZ) >= 1f:
                    rec.Placement.Position = new P3Float(p.SrcPosition.X, p.SrcPosition.Y, p.SrcPosition.Z);
                    rec.Placement.Rotation = new P3Float(p.SrcRotation.X, p.SrcRotation.Y, p.SrcRotation.Z);
                    rec.Scale = Math.Abs(p.SrcScale - 1f) < 1e-6f ? null : p.SrcScale;
                    repositioned++;
                    break;
                case DynRefIssue.Moved:
                case DynRefIssue.SourceMissing:
                case DynRefIssue.SourceDeleted:
                    rec.Placement.Position = new P3Float(rec.Placement.Position.X, rec.Placement.Position.Y, SunkZ);
                    sunk++;
                    break;
            }
        }

        Directory.CreateDirectory(fullOut);
        var outEsp = Path.Combine(fullOut, "DynDOLOD.esp");
        progress?.Report($"Writing {outEsp}...");
        mod.WriteToBinary(outEsp, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListContent = MastersListContentOption.NoCheck,   // the master list is not changed; keep it exactly as DynDOLOD wrote it
            MastersListOrdering = MastersListOrderingOption.NoCheck,
        });

        // --- the data files -------------------------------------------------------------------------------------------------------------------
        var dataFiles = new List<string>();
        int rowsEdited = 0;
        var byFile = report.Problems.Where(f => f.Issue == DynRefIssue.EnableStateChanged).GroupBy(f => f.Row.World);
        foreach (var g in byFile)
        {
            var name = $"DynDOLOD_{g.Key}_Objects.txt";
            var src = Path.Combine(DynamicRefCheck.DataFolder(dyndolodOutput), name);
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(src));          // Latin1 keeps every byte exactly
            var lines = SplitKeepingEndings(text);
            foreach (var f in g)
            {
                int idx = f.Row.Line - 1;
                lines[idx] = EditObjectsLine(lines[idx], f.Row.DynId, f.StartsEnabledNow, f.ChainOppositeNow);
                rowsEdited++;
            }
            var dest = Path.Combine(fullOut, "skse", "plugins", "DynDOLOD_Data", name);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, Encoding.Latin1.GetBytes(string.Concat(lines)));
            dataFiles.Add(name);
        }

        var verification = Verify(espPath, outEsp, report, progress);
        return new Result(fullOut, outEsp, repositioned, sunk, dataFiles, rowsEdited, verification);
    }

    /// <summary>Sets column 2 ("source starts enabled") and column 4 ("opposite of parent in the chain") of one Objects.txt row; "1" is true, empty is false.</summary>
    public static string EditObjectsLine(string line, uint expectedDynId, bool startsEnabled, bool chainOpposite)
    {
        int eq = line.IndexOf('=');
        if (eq <= 0 || !uint.TryParse(line.AsSpan(0, eq), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != expectedDynId)
            throw new InvalidOperationException($"Data row does not start with {expectedDynId}: the data file changed since the plan was made.");
        int end = line.Length;
        while (end > eq && (line[end - 1] == '\r' || line[end - 1] == '\n')) end--;
        var ending = line[end..];
        var cols = line[(eq + 1)..end].Split(',');
        if (cols.Length != 19) throw new InvalidOperationException($"Data row {expectedDynId} has {cols.Length} columns, expected 19.");
        cols[2] = startsEnabled ? "1" : "";
        cols[4] = chainOpposite ? "1" : "";
        return line[..(eq + 1)] + string.Join(",", cols) + ending;
    }

    private static List<string> SplitKeepingEndings(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n') { lines.Add(text[start..(i + 1)]); start = i + 1; }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static HashSet<(string, string)> ReadApproved(string csv)
    {
        var set = new HashSet<(string, string)>();
        foreach (var line in File.ReadLines(csv).Skip(1))
        {
            var c = line.Split(',');
            if (c.Length > 4) set.Add((c[0], c[4]));          // action, dynamic_ref (e.g. 05A1F4:DynDOLOD.esp)
        }
        return set;
    }

    /// <summary>Compares the written plugin with the original: same records, and every placed object identical except the ones the plan edited.</summary>
    private static List<string> Verify(string originalEsp, string writtenEsp, DynRefReport report, IProgress<string>? progress)
    {
        progress?.Report("Verifying the written plugin against the original...");
        var msgs = new List<string>();
        using var a = SkyrimMod.CreateFromBinaryOverlay(originalEsp, SkyrimRelease.SkyrimSE);
        using var b = SkyrimMod.CreateFromBinaryOverlay(writtenEsp, SkyrimRelease.SkyrimSE);

        var masterA = a.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        var masterB = b.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        msgs.Add(masterA.SequenceEqual(masterB, StringComparer.OrdinalIgnoreCase) ? $"masters identical ({masterA.Count})" : "MASTERS DIFFER");
        msgs.Add(a.ModHeader.Flags == b.ModHeader.Flags ? "header flags identical" : "HEADER FLAGS DIFFER");

        var countA = a.EnumerateMajorRecords().GroupBy(r => r.Registration.ClassType.Name).ToDictionary(g => g.Key, g => g.Count());
        var countB = b.EnumerateMajorRecords().GroupBy(r => r.Registration.ClassType.Name).ToDictionary(g => g.Key, g => g.Count());
        bool sameCounts = countA.Count == countB.Count && countA.All(kv => countB.TryGetValue(kv.Key, out var n) && n == kv.Value);
        msgs.Add(sameCounts ? $"record counts identical ({countA.Values.Sum():N0} records, {countA.Count} types)" : "RECORD COUNTS DIFFER");

        var edited = report.Problems.Select(f => FormKey.Factory($"{f.Row.DynId:X6}:DynDOLOD.esp")).ToHashSet();
        var refsB = b.EnumerateMajorRecords<IPlacedObjectGetter>().ToDictionary(r => r.FormKey);
        int checkedRefs = 0, differ = 0, editedFound = 0;
        foreach (var ra in a.EnumerateMajorRecords<IPlacedObjectGetter>())
        {
            if (!refsB.TryGetValue(ra.FormKey, out var rb)) { differ++; continue; }
            checkedRefs++;
            bool same = ra.Base.FormKey == rb.Base.FormKey && ra.MajorRecordFlagsRaw == rb.MajorRecordFlagsRaw && ra.EditorID == rb.EditorID
                        && ra.Placement?.Position == rb.Placement?.Position && ra.Placement?.Rotation == rb.Placement?.Rotation && ra.Scale == rb.Scale
                        && (ra.VirtualMachineAdapter?.Scripts.Count ?? 0) == (rb.VirtualMachineAdapter?.Scripts.Count ?? 0)
                        && ra.EnableParent?.Reference.FormKey == rb.EnableParent?.Reference.FormKey;
            if (edited.Contains(ra.FormKey)) { if (!same) editedFound++; }
            else if (!same) differ++;
        }
        msgs.Add(differ == 0 ? $"{checkedRefs:N0} placed objects compared: only the {editedFound} planned ones differ" : $"{differ} UNPLANNED placed-object differences");
        return msgs;
    }
}
