using WordFlow.App.Bootstrap;

namespace WordFlow.App.Styling;

public sealed record ThemeVisualState(double ImageOpacity, double VeilOpacity);

public static class ThemeVisualMapper
{
    public static ThemeVisualState Map(double opacity)
    {
        double value = ImageThemeService.ClampOpacity(opacity);
        return new ThemeVisualState(value, 0.88d - (0.76d * value));
    }
}
