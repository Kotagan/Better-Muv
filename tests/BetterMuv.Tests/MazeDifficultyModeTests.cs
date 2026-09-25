using BetterMuv.Core;

namespace BetterMuv.Tests;

public class MazeDifficultyModeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("keep", false)]
    [InlineData("custom", true)]
    [InlineData("自选", true)]
    [InlineData("tower", true)]
    [InlineData("爬塔", true)]
    public void OnlyCustomOrTowerModeRequiresRecognizedFloor(string? mode, bool expected)
    {
        Assert.Equal(expected, MazeDifficultyRunner.RequiresRecognizedFloor(mode));
    }

    [Theory]
    [InlineData("tower", "tower")]
    [InlineData("爬塔", "tower")]
    [InlineData("climb", "tower")]
    [InlineData("keep", "keep")]
    [InlineData("custom", "custom")]
    public void NormalizeModeRecognizesTower(string input, string expected)
    {
        Assert.Equal(expected, MazeDifficultyRunner.NormalizeMode(input));
    }
}
