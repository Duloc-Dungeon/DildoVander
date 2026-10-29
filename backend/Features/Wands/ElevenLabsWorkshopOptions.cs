namespace Kufr.Backend.Features.Wands;

internal sealed class ElevenLabsWorkshopOptions
{
    internal string? ApiKey { get; init; }
    internal string VoiceId { get; init; } = WorkshopPrompts.VoiceId;
    internal string Model { get; init; } = WorkshopPrompts.SpeechModel;
    internal double Speed { get; init; } = WorkshopPrompts.SpeechSpeed;
    internal bool UseLegacyGptLive { get; init; }

    internal static ElevenLabsWorkshopOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Wands:Voice");
        var provider = section["Provider"] ?? "elevenlabs";
        var options = new ElevenLabsWorkshopOptions
        {
            ApiKey = configuration["ELEVENLABS_API_KEY"],
            VoiceId = section["VoiceId"] ?? WorkshopPrompts.VoiceId,
            Model = section["Model"] ?? WorkshopPrompts.SpeechModel,
            Speed = section.GetValue("Speed", WorkshopPrompts.SpeechSpeed),
            UseLegacyGptLive = provider == "gpt-live"
        };
        if (provider is not ("elevenlabs" or "gpt-live")
            || options.VoiceId.Length is < 1 or > 100
            || options.VoiceId.Any(character => !char.IsAsciiLetterOrDigit(character))
            || !options.Model.StartsWith("eleven_", StringComparison.Ordinal) || options.Model.Length > 80
            || options.Model.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
            || !double.IsFinite(options.Speed) || options.Speed is < 0.7 or > 1.2)
        {
            throw new InvalidOperationException("Invalid Wands:Voice configuration.");
        }
        return options;
    }
}
