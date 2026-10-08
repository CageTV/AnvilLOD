using System.Text.Json;

namespace AnvilLOD.Plugins;

/// <summary>JSON report shared by the CLI and the app.</summary>
public static class ScanReport
{
    public static void Write(string path, ScanSummary s, TimeSpan total)
    {
        var report = new
        {
            generatedAt = DateTimeOffset.Now,
            totalSeconds = Math.Round(total.TotalSeconds, 2),
            timing = new
            {
                indexSeconds = Math.Round(s.IndexTime.TotalSeconds, 2),
                scanSeconds = Math.Round(s.Stats.Elapsed.TotalSeconds, 2),
                bucketHashSeconds = Math.Round(s.BucketAndHashTime.TotalSeconds, 3),
            },
            s.Stats.PluginsInLoadOrder,
            s.PluginsLoaded,
            s.MissingPlugins,
            s.Warnings,
            s.Stats.PlacedObjectsVisited,
            s.Stats.LodReferencesFound,
            s.Stats.SkippedDisabled,
            s.Stats.SkippedMissingMesh,
            s.SkippedOutsideGrid,
            archives = new { count = s.ArchivesIndexed, files = s.ArchivedFiles },
            grids = s.Grids.ToDictionary(kv => kv.Key, kv => kv.Value),
            plan = new { rebuild = s.Plan.Rebuild.Count, unchanged = s.Plan.Unchanged.Count, stale = s.Plan.StaleFiles },
            generation = s.Generation is null ? null : new
            {
                s.Generation.BlocksWritten,
                s.Generation.BlocksEmpty,
                s.Generation.BlocksFailed,
                s.Generation.Triangles,
                s.Generation.TrianglesCulled,
                s.Generation.MeshesLoaded,
                seconds = Math.Round(s.Generation.Elapsed.TotalSeconds, 2),
                staleDeleted = s.StaleFilesDeleted,
                meshErrors = s.Generation.MeshErrors,
                blockErrors = s.Generation.BlockErrors.ToDictionary(kv => kv.Key.FileName, kv => kv.Value),
            },
            missingMeshes = s.Stats.MissingMeshes.OrderByDescending(kv => kv.Value).Take(500)
                .Select(kv => new { path = kv.Key, refs = kv.Value }),
            quads = s.Quads
                .OrderBy(kv => kv.Key.Worldspace).ThenBy(kv => kv.Key.Level).ThenBy(kv => kv.Key.X).ThenBy(kv => kv.Key.Y)
                .Select(kv => new { file = kv.Key.RelativePath, refs = kv.Value.Count, hash = s.QuadHashes[kv.Key] }),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
