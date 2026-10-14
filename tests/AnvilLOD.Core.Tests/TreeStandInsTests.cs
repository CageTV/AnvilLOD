using AnvilLOD.Core.World;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class TreeStandInsTests
{
    private static readonly string[] Trees =
    [
        @"meshes\bscyrodiil\landscape\trees\treemaple01.nif", @"meshes\bscyrodiil\landscape\trees\treemaple03.nif",
        @"meshes\bscyrodiil\landscape\trees\treeoak02.nif", @"meshes\bstamriel\landscape\trees\bsktreemapleforest01.nif",
    ];

    [Fact]
    public void SameFamilyTakesNearestNumber() =>
        Assert.Equal(@"meshes\bscyrodiil\landscape\trees\treemaple03.nif", TreeStandIns.Choose(@"meshes\bscyrodiil\landscape\trees\treemaple04.nif", Trees));

    [Fact]
    public void OtherFolderIsNeverUsed() =>
        Assert.Null(TreeStandIns.Choose(@"meshes\bstamriel\landscape\trees\bsktreeredwoodcoastalforest01.nif", Trees));

    [Fact]
    public void SharedPrefixInSameFolderIsAccepted() =>
        Assert.Equal(@"meshes\bstamriel\landscape\trees\bsktreemapleforest01.nif", TreeStandIns.Choose(@"meshes\bstamriel\landscape\trees\bsktreemapleforestyoung02.nif", Trees));

    [Fact]
    public void MappingWins()
    {
        var map = TreeStandIns.ParseIni("; comment" + Environment.NewLine + "meshes/bstamriel/landscape/trees/bsktreemangroveswamp01.nif = " + @"meshes\bscyrodiil\landscape\trees\treeoak02.nif" + Environment.NewLine);
        Assert.Equal(@"meshes\bscyrodiil\landscape\trees\treeoak02.nif", TreeStandIns.Choose(@"meshes\bstamriel\landscape\trees\bsktreemangroveswamp01.nif", Trees, map));
    }
}
