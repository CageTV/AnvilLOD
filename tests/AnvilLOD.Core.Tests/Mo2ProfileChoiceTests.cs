using AnvilLOD.Plugins.Mo2;
using Xunit;

namespace AnvilLOD.Core.Tests;

public class Mo2ProfileChoiceTests
{
    private static readonly string[] Profiles = ["tabula rasa!", "anvil-test-notexgen", "TEST"];

    [Fact]
    public void UnderMo2_TheProfileMo2HasSelectedWinsOverTheRememberedOne() =>
        Assert.Equal("anvil-test-notexgen", Mo2ProfileChoice.Pick(Profiles, "tabula rasa!", "anvil-test-notexgen", underMo2: true));

    [Fact]
    public void OutsideMo2_TheRememberedProfileWins() =>
        Assert.Equal("tabula rasa!", Mo2ProfileChoice.Pick(Profiles, "tabula rasa!", "anvil-test-notexgen", underMo2: false));

    [Fact]
    public void NamesMatchWithoutRegardToCase() =>
        Assert.Equal("TEST", Mo2ProfileChoice.Pick(Profiles, null, "test", underMo2: true));

    [Fact]
    public void UnderMo2_AMissingMo2ProfileFallsBackToTheRememberedOne() =>
        Assert.Equal("tabula rasa!", Mo2ProfileChoice.Pick(Profiles, "tabula rasa!", "gone", underMo2: true));

    [Fact]
    public void NothingKnown_TheFirstProfile() =>
        Assert.Equal("tabula rasa!", Mo2ProfileChoice.Pick(Profiles, null, null, underMo2: false));

    [Fact]
    public void NoProfiles_Null() =>
        Assert.Null(Mo2ProfileChoice.Pick([], "a", "b", underMo2: true));
}
