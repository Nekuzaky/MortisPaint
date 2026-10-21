using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace MortisPaint;

/// <summary>Polices embarquées du design system Mortis (licence OFL, voir Assets/Fonts).</summary>
public static class MortisFonts
{
    public const string Key = "fonts:Mortis";

    public static AppBuilder WithMortisFonts(this AppBuilder builder) => builder
        .ConfigureFonts(fm => fm.AddFontCollection(new EmbeddedFontCollection(
            new Uri(Key, UriKind.Absolute),
            new Uri("avares://MortisPaint/Assets/Fonts", UriKind.Absolute))))
        .With(new FontManagerOptions { DefaultFamilyName = Key + "#Atkinson Hyperlegible Next" });
}
