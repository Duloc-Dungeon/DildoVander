namespace Kufr.Backend.Features.Game;

internal enum GameBossMood { Arrogant, Angry, Desperate, Sarcastic }

// Direction follows disclosed combat state, never a hidden next move or an
// invented outcome. Only these authored voice tags can reach ElevenLabs.
internal sealed record GameBossDelivery(GameBossMood Mood, string Direction, string Tags)
{
    internal static GameBossDelivery For(GameBossAttemptView match)
    {
        if (match.Status == "complete")
        {
            return match.Result == "win"
                ? new(GameBossMood.Desperate, "Baron je poražený: zoufalý, bez předstírání vítězství.", "[desperate] [sighs]")
                : new(GameBossMood.Arrogant, "Baron vyhrál: povýšený, samolibý smích.", "[haughty] [laughing]");
        }
        if (match.OpponentEnergy <= 1)
        {
            return new(GameBossMood.Desperate, "Baronovi zbývá poslední energie: zoufalství a praskající sebejistota.", "[desperate]");
        }
        return match.Rounds.LastOrDefault()?.Winner switch
        {
            "player" => new(GameBossMood.Angry, "Baron právě utržil zásah: naštvaný, uražená ješitnost.", "[angry]"),
            "opponent" => new(GameBossMood.Arrogant, "Baron právě zasáhl soupeře: povýšený, krátký posměšný smích.", "[haughty] [laughing]"),
            "draw" => new(GameBossMood.Sarcastic, "Výměna byla nerozhodná: sarkastický, zakrývá rozpaky.", "[sarcastic]"),
            _ => new(GameBossMood.Arrogant, "Začátek souboje: povýšený, klidná přehnaná sebejistota.", "[haughty]")
        };
    }

    internal string Speak(string text)
    {
        // GPT supplies words, the game owns delivery. Reject model-supplied tags
        // rather than interpreting arbitrary bracket instructions from a profile.
        if (string.IsNullOrWhiteSpace(text) || text.Length > 320 || text.Contains('[') || text.Contains(']'))
        { throw new InvalidDataException("Invalid untagged Baron reply."); }
        return $"{Tags} {text}";
    }
}
