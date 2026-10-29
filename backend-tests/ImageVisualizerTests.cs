using Kufr.Backend.Features.Images;
using Kufr.Backend.Features.Keepsakes;

internal static class ImageVisualizerTests
{
    internal static void Run()
    {
        var brief = ImageVisualizer.Describe("Nakresli jednu fiktivní hůlku.",
            ("Rukojeť", "Vyřezávaná\n\tjasanová rukojeť"), ("Volitelné", null));
        if (!brief.Contains("Vyřezávaná jasanová rukojeť", StringComparison.Ordinal)
            || brief.Contains('{') || brief.Contains("\\u", StringComparison.Ordinal) || brief.Contains("Volitelné", StringComparison.Ordinal))
        { throw new Exception("The visualizer must produce readable visual prose and omit absent details."); }
        var legacy = OpenAiKeepsakeProvider.BuildImagePrompt(new("Štíhlá silueta", "Prohnutý úchop", "Stříbrné rytí", "Jasan", "Namodralá záře"));
        if (legacy.Contains('{') || !legacy.Contains("Namodralá záře", StringComparison.Ordinal)
            || !legacy.Contains("Stříbrné rytí", StringComparison.Ordinal))
        { throw new Exception("The alternate keepsake generator must receive prose instead of a serialized design."); }
        Console.WriteLine("Shared visualizer and alternate keepsake image prose passed.");
    }
}
