using BetterMuv.Core;

namespace BetterMuv.Tests;

public class MazeDifficultyModeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("keep", false)]
    [InlineData("custom", true)]
    [InlineData("自选", true)]
    public void OnlyCustomModeRequiresRecognizedFloor(string? mode, bool expected)
    {
        Assert.Equal(expected, MazeDifficultyRunner.RequiresRecognizedFloor(mode));
    }
}
