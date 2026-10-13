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
        Assert.Equal(@"textures\anvillod\water\watertile_d.dds", m.Textures[0]);
        Assert.Equal(@"textures\anvillod\water\watertile_n.dds", m.Textures[1]);
        // 0x9E2E2F26 little-endian = R 26, G 2F, B 2E, A 9E: the stream twins' dark teal (2A3433, 69% opaque) made 10% darker and 10% more transparent
        Assert.Equal(0x9E, (int)(NifGeometryReader.FakeWaterColor >> 24));
    }

    [Fact]
    public void A_rivers_flow_direction_follows_the_channel_toward_the_lower_neighbouring_water()
    {
        var strip = new List<(float X0, float X1, float Y0, float Y1)>();
        for (int j = 0; j < 32; j++) strip.Add((1920f, 2176f, j * 128f, (j + 1) * 128f));   // a channel running north-south
        var cells = new Dictionary<(string Ws, int X, int Y), float> { [("Tamriel", 5, 5)] = -4000f, [("Tamriel", 5, 6)] = -4100f };
        var north = AnvilLOD.Plugins.WaterCopies.FlowDirection(null!, cells, "Tamriel", 5, 5, -4000f, strip);
        Assert.NotNull(north);
        Assert.True(north!.Value.Y > 0.95f && Math.Abs(north.Value.X) < 0.1f);          // the lower neighbour is to the north (+Y)
        var cells2 = new Dictionary<(string Ws, int X, int Y), float> { [("Tamriel", 5, 5)] = -4000f, [("Tamriel", 5, 4)] = -4100f };
        var south = AnvilLOD.Plugins.WaterCopies.FlowDirection(null!, cells2, "Tamriel", 5, 5, -4000f, strip);
        Assert.True(south!.Value.Y < -0.95f);                                           // ... or to the south
        var pond = new List<(float X0, float X1, float Y0, float Y1)> { (1000f, 3000f, 1000f, 1128f), (1000f, 3000f, 1128f, 1256f), (1000f, 3000f, 1256f, 1384f), (1000f, 3000f, 1384f, 3000f) };
        Assert.Null(AnvilLOD.Plugins.WaterCopies.FlowDirection(null!, cells, "Tamriel", 5, 5, -4000f, pond));   // roundish: no direction
    }

    [Fact]
    public void The_water_edge_follows_the_terrain_contour_instead_of_whole_squares()
    {
        // Terrain rising to the north-east: height (x + y - 20) * 8 at grid point (x, y); the water (height 0) fills x + y < 20.
        var terrain = new AnvilLOD.Core.World.TerrainHeights();
        var heights = new float[AnvilLOD.Core.World.TerrainHeights.Grid * AnvilLOD.Core.World.TerrainHeights.Grid];
        for (int y = 0; y < AnvilLOD.Core.World.TerrainHeights.Grid; y++)
            for (int x = 0; x < AnvilLOD.Core.World.TerrainHeights.Grid; x++)
                heights[y * AnvilLOD.Core.World.TerrainHeights.Grid + x] = (x + y - 20) * 8f;
        terrain.Set("Tamriel", 0, 0, heights);
        var template = new AnvilLOD.Meshes.LodMeshPart
        {
            Material = NifGeometryReader.FakeWaterMaterial, Positions = [], UVs = [], Normals = [], Tangents = [], Bitangents = [], Triangles = [],
        };
        var part = AnvilLOD.Plugins.WaterCopies.BuildCell(terrain, "Tamriel", 0, 0, 0f, template, 1f / 2048f, true, 0x80473A22u, 3f,
            new Dictionary<(string Ws, int X, int Y), float>(), out _, out _, out _, out _, out _);
        Assert.NotNull(part);
        float s = AnvilLOD.Core.World.TerrainHeights.Spacing;
        // no vertex sticks out past the contour x + y = 20 grid steps (whole squares would reach x + y = 21), and the contour is reached
        Assert.All(part!.Positions, p => Assert.True((p.X + p.Y) / s <= 20.001f));
        Assert.Contains(part.Positions, p => (p.X + p.Y) / s > 19.99f);
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
