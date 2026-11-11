namespace AlienTextFlow;

public sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public int MinWidthPercent { get; set; } = 75;
    public float FontSizeMultiplier { get; set; } = 1f;
    public float MinFontSizePixels { get; set; } = 1f;
    public bool AllowTwoLines { get; set; } = true;
    public float TwoLineMinimumGain { get; set; } = 1.12f;

    public void Validate()
    {
        if (MinWidthPercent < 75 || MinWidthPercent > 100 || MinWidthPercent % 5 != 0)
            throw new System.ArgumentOutOfRangeException(nameof(MinWidthPercent), "Use 75, 80, 85, 90, 95 or 100.");
        if (float.IsNaN(FontSizeMultiplier) || FontSizeMultiplier < 0.5f || FontSizeMultiplier > 2)
            throw new System.ArgumentOutOfRangeException(nameof(FontSizeMultiplier), "Use a value from 0.5 to 2.");
        if (float.IsNaN(MinFontSizePixels) || MinFontSizePixels < 0.1f || MinFontSizePixels > 12)
            throw new System.ArgumentOutOfRangeException(nameof(MinFontSizePixels), "Use a value from 0.1 to 12.");
        if (float.IsNaN(TwoLineMinimumGain) || TwoLineMinimumGain < 1 || TwoLineMinimumGain > 2)
            throw new System.ArgumentOutOfRangeException(nameof(TwoLineMinimumGain), "Use a value from 1 to 2.");
    }
}
