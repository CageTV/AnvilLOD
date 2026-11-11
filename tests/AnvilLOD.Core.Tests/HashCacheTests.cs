using AnvilLOD.Plugins;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class HashCacheTests
{
    [Fact]
    public void Same_content_with_a_new_time_gives_the_same_hash_and_different_content_does_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anvil-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var f = Path.Combine(dir, "a.nif");
            File.WriteAllText(f, "model one");
            var cache = HashCache.Load(Path.Combine(dir, "cache.json"));
            var h1 = cache.HashOf(new FileInfo(f));

            // a support app rewrites the same bytes: new time, same content
            File.WriteAllText(f, "model one");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(5));
            Assert.Equal(h1, cache.HashOf(new FileInfo(f)));

            // really different content, same length
            File.WriteAllText(f, "model two");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(6));
            Assert.NotEqual(h1, cache.HashOf(new FileInfo(f)));

            // the cache is remembered between runs, and an unchanged file is not read again
            cache.Save();
            var again = HashCache.Load(Path.Combine(dir, "cache.json"));
            var before = again.Computed;
            again.HashOf(new FileInfo(f));
            Assert.Equal(before, again.Computed);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
