using WordFlow.App.Styling;

namespace WordFlow.App.Tests.Styling;

public sealed class ThemeVisualMapperTests
{
    [Theory]
    [InlineData(0.0, 0.0, 0.88)]
    [InlineData(1.0, 1.0, 0.12)]
    [InlineData(0.5, 0.5, 0.50)]
    public void Map_uses_full_image_range_and_inverse_readability_veil(
        double slider, double expectedImage, double expectedVeil)
    {
        var state = ThemeVisualMapper.Map(slider);

        Assert.Equal(expectedImage, state.ImageOpacity, 3);
        Assert.Equal(expectedVeil, state.VeilOpacity, 3);
    }
}
