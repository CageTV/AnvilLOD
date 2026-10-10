using System.Numerics;
using System.Text;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.World;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using AnvilLOD.Plugins;

namespace AnvilLOD.Core.Tests;

public class WaterStandInTests
{
    [Theory]
    [InlineData(@"dyndolod\lod\water\tundrastreamstraight01watera_dyndolod_lod.nif", true)]
    [InlineData(@"water\tundrapond01.nif", true)]
    [InlineData(@"effects\fxcreekflatlarge.nif", false)]                        // an effect mesh with no "water" in its name
    [InlineData(@"dyndolod\lod\water\mineralpoolbigwater01_dyndolod_lod.nif", true)]
    [InlineData(@"dyndolod\lod\water\water1024_dyndolod_lod.nif", true)]      // the Water1024Creek / River plane family
    [InlineData(@"dyndolod\lod\water\water2048x1024_dyndolod_lod.nif", true)]
    [InlineData(@"effects\fxrapids.nif", false)]                                // not a water-named mesh
    [InlineData(@"dyndolod\lod\effects\fxwaterfallbodytall_dyndolod_lod.nif", false)]
    [InlineData(@"clutter\waterwheel01.nif", false)]
    public void Filter_picks_the_water_planes(string mesh, bool expected)
        => Assert.Equal(expected, WaterStandIns.DefaultFilter(mesh));

    [Fact]
    public void Fake_water_matches_the_CS_Water_Mod_twin()
    {
        var m = NifGeometryReader.FakeWaterMaterial;
        Assert.Equal(0x8E400309u, m.ShaderFlags1);
        Assert.Equal(0x8020u, m.ShaderFlags2);
        Assert.True(m.HasAlpha);
        Assert.Equal((ushort)0x10ED, m.AlphaFlags);
        Assert.Equal(@"textures\effects\FXwaterTile01.dds", m.Textures[0]);
        Assert.Equal(@"textures\effects\FXwaterTile01_n.dds", m.Textures[1]);
        // 0xAF33342A little-endian = R 2A, G 34, B 33, A AF (69% opaque dark teal)
        Assert.Equal(0xAF, (int)(NifGeometryReader.FakeWaterColor >> 24));
    }

    private sealed class MemoryAssets(Dictionary<string, byte[]> files) : IAssetSource
    {
        public bool Exists(string p) => files.ContainsKey(p);
        public bool TryOpen(string p, out Stream s)
        {
            if (files.TryGetValue(p, out var b)) { s = new MemoryStream(b); return true; }
            s = Stream.Null; return false;
        }
        public string? Fingerprint(string p) => files.TryGetValue(p, out var b) ? b.Length.ToString() : null;
    }

    private static LodMeshPart LitPart() => new()
    {
        Material = NifGeometryReader.FakeWaterMaterial,
        Positions = [new(0, 0, 0), new(100, 0, 0), new(0, 100, 0)],
        UVs = [new(0, 0), new(1, 0), new(0, 1)],
        Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        Tangents = [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX],
        Bitangents = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
        Colors = [0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu],
        Triangles = [0, 1, 2],
    };

    [Fact]
    public void A_mesh_without_a_water_shader_gets_no_stand_in()
    {
        var nif = LodNifWriter.Write("streamlit", [LitPart()]);
        var assets = new MemoryAssets(new(StringComparer.OrdinalIgnoreCase) { [@"meshes\water\streamlit.nif"] = nif });
        var dir = Directory.CreateTempSubdirectory("anvil-water-").FullName;
        try
        {
            var r = WaterStandIns.Build([@"water\streamlit.nif", @"water\missing_stream.nif", @"effects\fxwaterfalltall.nif"], assets, dir);
            Assert.Equal(0, r.Converted);
            Assert.Equal(1, r.NoWaterShader);      // the lit one is read fine and left alone
            Assert.Equal(1, r.Unreadable);         // the missing one; the waterfall is filtered out before it is looked at
            Assert.Empty(r.Map);
            Assert.False(Directory.Exists(Path.Combine(dir, "meshes", "anvillod")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mesh_map_replaces_paths_in_the_dyn_file()
    {
        LodReference Ref(string id, string mesh) => new($"0:{id}", id, "", "Tamriel", "x.esp", Vector3.Zero, Vector3.Zero, 1f,
            new LodMeshSet(mesh, null, null, null), LodReferenceFlags.None);
        var refs = new[]
        {
            new DynamicLodReference(Ref("a", @"meshes\dyndolod\lod\water\tundrastreamstraight01watera_dyndolod_lod.nif"), "Skyrim.esm", 1, "Skyrim.esm", 0x3C, Grid: DynamicGrid.Near),
            new DynamicLodReference(Ref("b", @"meshes\effects\fxrapids.nif"), "Skyrim.esm", 2, "Skyrim.esm", 0x3C, Grid: DynamicGrid.Near),
        };
        var dir = Directory.CreateTempSubdirectory("anvil-dyn-").FullName;
        try
        {
            var map = new Dictionary<string, string> { [@"dyndolod\lod\water\tundrastreamstraight01watera_dyndolod_lod.nif"] = @"anvillod\water\x_123456.nif" };
            Assert.Equal(2, DynamicLodWriter.Write(dir, refs, map));
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, "SKSE", "Plugins", "AnvilLOD", "AnvilLOD.dyn")));
            Assert.Contains(@"anvillod\water\x_123456.nif", text);
            Assert.DoesNotContain("tundrastreamstraight01watera", text);
            Assert.Contains(@"effects\fxrapids.nif", text); // unmapped meshes are untouched
        }
        finally { Directory.Delete(dir, true); }
    }
}
