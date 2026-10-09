using System.Numerics;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using AnvilLOD.Plugins.Authoring;

namespace AnvilLOD.Core.Tests;

public class LodMeshMakerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "anvillod-maker-" + Guid.NewGuid().ToString("N"));

    public LodMeshMakerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    /// <summary>A bumpy 60x60 sheet, 7,200 triangles, big enough to survive the part-size cut.</summary>
    private static byte[] DenseModel(string name)
    {
        const int n = 60;
        var pos = new Vector3[(n + 1) * (n + 1)];
        for (int y = 0; y <= n; y++)
            for (int x = 0; x <= n; x++)
                pos[y * (n + 1) + x] = new Vector3(x * 20f, y * 20f, MathF.Sin(x * 0.4f) * MathF.Cos(y * 0.4f) * 12f);
        var tris = new List<ushort>();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                ushort a = (ushort)(y * (n + 1) + x), b = (ushort)(a + 1), c = (ushort)(a + n + 1), d = (ushort)(c + 1);
                tris.AddRange([a, b, d, a, d, c]);
            }
        var material = new LodMaterial(["textures\\test\\wall.dds", "textures\\test\\wall_n.dds", "", "", "", "", "", "", ""],
            0x80000300, 5, 3, false, 0, 0, Vector3.Zero, 1f);
        var part = new LodMeshPart
        {
            Name = name,
            Material = material,
            Positions = pos,
            UVs = pos.Select(p => new Vector2(p.X / (n * 20f), p.Y / (n * 20f))).ToArray(),
            Normals = pos.Select(_ => Vector3.UnitZ).ToArray(),
            Tangents = pos.Select(_ => Vector3.UnitX).ToArray(),
            Bitangents = pos.Select(_ => Vector3.UnitY).ToArray(),
            Triangles = [.. tris],
        };
        return LodNifWriter.Write(name, [part]);
    }

    private string Model(string folder, string file)
    {
        var path = Path.Combine(_root, folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, DenseModel(Path.GetFileNameWithoutExtension(file)));
        return path;
    }

    private static int Triangles(string nif) => NifGeometryReader.Read(nif, File.ReadAllBytes(nif)).Parts.Sum(p => p.TriangleCount);

    [Fact]
    public void Makes_three_smaller_levels_keeps_the_textures_and_writes_a_rule_line()
    {
        Model("modA\\meshes\\arch", "wall01.nif");
        var output = Path.Combine(_root, "out");
        var r = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "modA")], output, "Test"));

        Assert.Equal(1, r.ModelsDone);
        Assert.Equal(3, r.MeshesWritten);
        var l0 = Path.Combine(output, "meshes", "lod", "Test", "wall01_lod_0.nif");
        var l2 = Path.Combine(output, "meshes", "lod", "Test", "wall01_lod_2.nif");
        Assert.True(File.Exists(l0));
        Assert.InRange(Triangles(l0), 1, 7200 - 1);
        Assert.True(Triangles(l2) < Triangles(l0));

        var mesh = NifGeometryReader.Read(l2, File.ReadAllBytes(l2));
        Assert.Equal("textures\\test\\wall.dds", mesh.Parts[0].Material.Textures[0]);

        var rule = File.ReadAllText(r.RuleFile!);
        Assert.Contains("LODGen1=arch\\wall01.nif,Level0,Level1,Level2,Level2,FarLOD,Unchanged,0,AnvilLOD LOD Maker", rule);
        Assert.True(File.Exists(r.ReportFile));
    }

    [Fact]
    public void A_second_run_keeps_files_and_the_rule_file_only_gains_new_models()
    {
        Model("modA\\meshes\\arch", "wall01.nif");
        var output = Path.Combine(_root, "out");
        LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "modA")], output, "Test"));
        var l0 = Path.Combine(output, "meshes", "lod", "Test", "wall01_lod_0.nif");
        File.WriteAllText(l0, "hand edited");

        Model("modB\\meshes\\arch", "wall02.nif");
        var r = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "modA"), Path.Combine(_root, "modB")], output, "Test"));

        Assert.Equal("hand edited", File.ReadAllText(l0));                 // kept
        Assert.Equal(3, r.MeshesWritten);                                  // only wall02's three
        var rule = File.ReadAllLines(r.RuleFile!).Where(l => l.StartsWith("LODGen")).ToList();
        Assert.Equal(2, rule.Count);
        Assert.Contains(rule, l => l.Contains("arch\\wall02.nif"));

        var again = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "modA")], output, "Test", Overwrite: true));
        Assert.NotEqual("hand edited", File.ReadAllText(l0));              // replaced on request
    }

    [Fact]
    public void Of_two_models_with_the_same_file_name_only_the_first_is_made_and_LOD_files_are_skipped()
    {
        Model("mod\\meshes\\a", "same.nif");
        Model("mod\\meshes\\b", "same.nif");
        Model("mod\\meshes\\a", "thing_lod_0.nif");
        var r = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "out"), "G"));

        Assert.Equal(1, r.ModelsDone);
        Assert.Contains(r.Items, i => i.Model == "meshes\\b\\same.nif" && i.Status.StartsWith("Skipped: same file name", StringComparison.Ordinal));
        Assert.Contains(r.Items, i => i.Model.EndsWith("thing_lod_0.nif", StringComparison.Ordinal) && i.Status.StartsWith("Skipped: already a LOD mesh", StringComparison.Ordinal));
    }

    [Fact]
    public void Levels_can_be_chosen_and_the_rule_line_uses_the_nearest_level_made()
    {
        Assert.Equal("a\\b.nif,Level0,Level0,Level2,Level2,FarLOD,Unchanged,0,AnvilLOD LOD Maker", LodMeshMaker.RuleLine("meshes\\a\\b.nif", [0, 2]));
        Assert.Equal("a\\b.nif,Level1,Level1,Level1,Level1,FarLOD,Unchanged,0,AnvilLOD LOD Maker", LodMeshMaker.RuleLine("meshes\\a\\b.nif", [1]));
        Assert.Equal("x.nif,Level0,Level1,Level2,Level2,FarLOD,Unchanged,0,AnvilLOD LOD Maker", LodMeshMaker.RuleLine("meshes\\x.nif", [0, 1, 2]));

        Model("mod\\meshes\\arch", "wall01.nif");
        var output = Path.Combine(_root, "out");
        var r = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], output, "G", Levels: [1]));
        Assert.Equal(1, r.MeshesWritten);
        Assert.True(File.Exists(Path.Combine(output, "meshes", "lod", "G", "wall01_lod_1.nif")));
        Assert.False(File.Exists(Path.Combine(output, "meshes", "lod", "G", "wall01_lod_0.nif")));
    }

    [Fact]
    public void A_finer_detail_setting_keeps_more_triangles_and_small_models_can_be_skipped()
    {
        Model("mod\\meshes\\arch", "wall01.nif");
        var normal = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "n"), "G", Levels: [2], Budget: BudgetMode.Off));
        var fine = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "f"), "G", Levels: [2], Detail: 0.25f, Budget: BudgetMode.Off));
        Assert.True(Triangles(Path.Combine(_root, "f", "meshes", "lod", "G", "wall01_lod_2.nif")) >= Triangles(Path.Combine(_root, "n", "meshes", "lod", "G", "wall01_lod_2.nif")));
        Assert.Equal(1, normal.ModelsDone + fine.ModelsDone - 1);

        var skipped = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "s"), "G", MinModelSize: 100000f));
        Assert.Equal(0, skipped.ModelsDone);
        Assert.StartsWith("Skipped: smaller", skipped.Items[0].Status, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recommended_budget_grows_with_model_size_and_never_rises_with_distance()
    {
        foreach (var size in new[] { 200f, 999f, 1000f, 2999f, 5000f, 7999f, 8000f, 50000f })
        {
            int b0 = LodBudgets.Recommended(0, size), b1 = LodBudgets.Recommended(1, size), b2 = LodBudgets.Recommended(2, size);
            Assert.True(b0 >= b1 && b1 >= b2, $"size {size}: {b0}/{b1}/{b2}");
        }
        Assert.True(LodBudgets.Recommended(0, 500f) < LodBudgets.Recommended(0, 2000f));
        Assert.True(LodBudgets.Recommended(0, 2000f) < LodBudgets.Recommended(0, 5000f));
        Assert.Equal(250, LodBudgets.Recommended(0, 999f));
        Assert.Equal(600, LodBudgets.Recommended(0, 1000f));
        Assert.Contains("units", LodBudgets.Describe());
    }

    [Fact]
    public void The_default_budget_keeps_every_level_within_the_recommended_numbers_and_stepping_down()
    {
        Model("mod\\meshes\\arch", "wall01.nif");   // about 1,200 units wide: the 1,000-3,000 class (600 / 450 / 300)
        var output = Path.Combine(_root, "out");
        var r = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], output, "G"));
        int T(int level) => Triangles(Path.Combine(output, "meshes", "lod", "G", $"wall01_lod_{level}.nif"));
        Assert.InRange(T(0), 1, 600);
        Assert.InRange(T(1), 1, 450);
        Assert.InRange(T(2), 1, 300);
        Assert.True(T(0) >= T(1) && T(1) >= T(2));
        Assert.True(T(0) > 300, "the budget should be used, not undershot to nothing");
        Assert.Contains("Triangle budget: recommended", File.ReadAllText(r.ReportFile));
    }

    [Fact]
    public void A_custom_budget_is_met_zero_means_no_limit_and_off_leaves_the_detail_setting_alone()
    {
        Model("mod\\meshes\\arch", "wall01.nif");
        int T(string folder, int level) => Triangles(Path.Combine(_root, folder, "meshes", "lod", "G", $"wall01_lod_{level}.nif"));

        LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "c"), "G", Budget: BudgetMode.Custom, CustomBudget: [150, 0, 60]));
        Assert.InRange(T("c", 0), 1, 150);
        Assert.InRange(T("c", 2), 1, 60);
        Assert.True(T("c", 1) > 150, "LOD 1 had no limit");   // only the detail setting applies

        LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "o"), "G", Budget: BudgetMode.Off));
        Assert.True(T("o", 0) > 600, "without a budget the dense sheet stays bigger than the recommended 600");

        Assert.Equal(150, LodMeshMaker.BudgetFor(new LodMakerRequest([], "x", "G", Budget: BudgetMode.Custom, CustomBudget: [150, 0, 60]), 0, 5000f));
        Assert.Null(LodMeshMaker.BudgetFor(new LodMakerRequest([], "x", "G", Budget: BudgetMode.Custom, CustomBudget: [150, 0, 60]), 1, 5000f));
        Assert.Null(LodMeshMaker.BudgetFor(new LodMakerRequest([], "x", "G", Budget: BudgetMode.Off), 0, 5000f));
        Assert.Equal(2500, LodMeshMaker.BudgetFor(new LodMakerRequest([], "x", "G"), 0, 5000f));
    }

    [Fact]
    public void The_shared_budget_lookup_is_what_the_maker_and_the_author_tool_use()
    {
        Assert.Equal(600, LodBudgets.For(BudgetMode.Recommended, null, 0, 2000f));
        Assert.Equal(300, LodBudgets.For(BudgetMode.Custom, [300, 0, 0], 0, 2000f));
        Assert.Null(LodBudgets.For(BudgetMode.Custom, [300, 0, 0], 1, 2000f));
        Assert.Null(LodBudgets.For(BudgetMode.Custom, null, 0, 2000f));
        Assert.Null(LodBudgets.For(BudgetMode.Off, [300, 300, 300], 0, 2000f));
    }

    [Fact]
    public void A_budget_that_cannot_be_met_is_reported_instead_of_failing()
    {
        Model("mod\\meshes\\arch", "wall01.nif");
        var r = LodMeshMaker.Run(new LodMakerRequest([Path.Combine(_root, "mod")], Path.Combine(_root, "out"), "G", Levels: [0], Budget: BudgetMode.Custom, CustomBudget: [1, 0, 0]));
        Assert.Equal(1, r.ModelsDone);
        Assert.Contains("over the budget of 1", r.Items[0].Action);
    }

    [Fact]
    public void Single_files_get_their_meshes_relative_path_and_the_output_folder_is_never_read_back()
    {
        var file = Model("mod\\meshes\\arch\\deep", "wall01.nif");
        Assert.Equal("meshes\\arch\\deep\\wall01.nif", LodMeshMaker.RelativeOf(file));
        Assert.Equal("meshes\\loose.nif", LodMeshMaker.RelativeOf("C:\\Somewhere\\loose.nif"));

        // The output sits inside the folder being read: its own files must not be picked up as inputs.
        var mod = Path.Combine(_root, "mod");
        var output = Path.Combine(mod, "meshes", "made");
        var r = LodMeshMaker.Run(new LodMakerRequest([mod], output, "G"));
        var again = LodMeshMaker.Run(new LodMakerRequest([mod], output, "G"));
        Assert.Equal(1, r.Items.Count);
        Assert.Equal(1, again.Items.Count);
    }

    [Fact]
    public void Missing_inputs_and_empty_level_lists_are_reported()
    {
        var notes = new List<string>();
        Assert.Empty(LodMeshMaker.Collect([Path.Combine(_root, "nope")], null, notes));
        Assert.Single(notes);
        Assert.Throws<ArgumentException>(() => LodMeshMaker.Run(new LodMakerRequest([_root], Path.Combine(_root, "o"), "G", Levels: [])));
    }
}
