namespace Kufr.Backend.Features.Wands;

internal static class WorkshopPrompts
{
    internal static string Dialogue { get; } = Read("Kufr.Backend.WorkshopDialogue");
    // TTS has no separate instruction field. Keep the approved delivery guide
    // alongside its concrete voice settings; never send it as spoken text.
    internal static string SpeechDirections { get; } = Read("Kufr.Backend.WorkshopSpeechDirections");
    internal const string VoiceId = "U48DQ1c9SVmD2BVCSiHL";
    internal const string SpeechModel = "eleven_v4";
    internal const string SpeechLanguage = "cs";
    internal const double SpeechSpeed = 1.0;

    private static string Read(string name)
    {
        using var source = typeof(WorkshopPrompts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Workshop prompt resource is missing.");
        using var reader = new StreamReader(source);
        return reader.ReadToEnd().Trim();
    }
}
