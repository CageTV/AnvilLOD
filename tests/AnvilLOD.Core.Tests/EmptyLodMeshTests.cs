using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Core.Tests;

public class EmptyLodMeshTests
{
    // meshes\lod\imperial\impextwindow01_lod_0.nif from DynDOLOD Resources: a bare BSFadeNode (202 bytes).
    // DynDOLOD ships these on purpose to say "this object gets no LOD".
    private const string BareFadeNode = "R2FtZWJyeW8gRmlsZSBGb3JtYXQsIFZlcnNpb24gMjAuMi4wLjcKBwACFAEMAAAAAQAAAGQAAAABAAEAAQABAAoAAABCU0ZhZGVOb2RlAABQAAAAAQAAAA4AAAAOAAAASW1wRXh0V2luZG93MDEAAAAAAAAAAAAAAAD/////DgAIAAAAAAAAAAAAAAAAAAAAgD8AAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAACAPwAAgD//////AAAAAAAAAAABAAAAAAAAAA==";

    [Fact]
    public void A_file_that_is_only_an_empty_node_is_a_deliberate_no_LOD_mesh_not_an_error()
    {
        var mesh = NifGeometryReader.Read("meshes/lod/imperial/impextwindow01_lod_0.nif", Convert.FromBase64String(BareFadeNode));
        Assert.Empty(mesh.Parts);
        Assert.Empty(mesh.Warnings);
        Assert.True(mesh.NoShapesInFile);
    }
}
