using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.GptLive;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed partial class PersonaLabRuntime
{
    // Rebuild from this session's bounded actual turns, never another guest's
    // fixtures or inferred memory. The same pure DV01 director owns tone, consent,
    // anchors and pacing; Lab tools remain synthetic and cannot unlock hardware.
    internal static string CharacterInstructions(LabSession session, GptLiveOptions options, string currentInput = "")
    {
        var character = new CharacterSession(session.Id, options.EffectiveCharacter);
        // The session store caps total turns at 200. Replay all controls so an
        // old withdrawal survives even after the director expires its anchors.
        var turns = session.Turns.Where(turn => turn.Role == "user").ToArray();
        foreach (var turn in turns)
        {
            if (turn.Text.Length is > 0 and <= CharacterSession.MaximumSourceCharacters)
            {
                character.AcceptInput(turn.Id, 1, turn.Text);
            }
        }
        if (currentInput.Length is > 0 and <= CharacterSession.MaximumSourceCharacters)
        {
            character.AcceptInput("current-text-input", 1, currentInput);
        }
        var directive = CharacterDirector.Evaluate(character,
            turns.Length == 0 && currentInput.Length == 0 ? CharacterPhase.Opening : CharacterPhase.Responding,
            turns.Length + (currentInput.Length > 0 ? 1 : 0));
        return directive.PromptText;
    }
}
