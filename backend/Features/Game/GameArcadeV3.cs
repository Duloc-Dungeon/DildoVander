namespace Kufr.Backend.Features.Game;

// Kept separate so released v1 and v2 trajectories remain byte-for-byte stable.
internal static class GameArcadeV3
{
    private const int GateCount = GameArcadeRules.MaximumGates;
    private const int PlayerRadius = 7;
    private const int PlayerY = 480;

    private sealed record Gate(int Index, int Distance, int CenterX, int Width, int Height,
        bool Moving = false, int MotionSign = 1, bool EchoSource = false, bool EchoTarget = false);
    private sealed record Echo(int TargetIndex, int X, int HalfWidth);

    internal static int Verify(uint seed, int endTick, IReadOnlyList<int> taps)
    {
        ValidateReplay(endTick, taps);
        var gates = BuildGates(seed);
        var echoSourceIndices = gates.Where(gate => gate.EchoSource).Select(gate => gate.Index).ToArray();
        var echoes = new List<Echo>();
        var tapIndex = 0;
        var direction = 1;
        var x = 180;
        var cleared = 0;
        var previousScroll = 0;
        var nextEchoSource = 0;

        for (var tick = 0; tick < endTick; tick++)
        {
            while (tapIndex < taps.Count && taps[tapIndex] == tick)
            {
                direction = -direction;
                tapIndex++;
            }

            var completedTicks = tick + 1;
            var movement = completedTicks % 5 == 0 ? 3 : 2;
            x += direction * movement;
            var scroll = completedTicks * 2 + completedTicks / 5;
            if (x - PlayerRadius <= 16 || x + PlayerRadius >= 344)
            {
                return CollisionMustEndReplay(tick, endTick, tapIndex, taps.Count, cleared);
            }

            for (var index = cleared; index < GateCount; index++)
            {
                var gate = gates[index];
                if (gate.Distance - 36 - PlayerRadius > scroll)
                {
                    break;
                }
                var centerY = PlayerY - gate.Distance + scroll;
                var centerX = gate.CenterX + (gate.Moving ? MotionOffset(gates, gate, completedTicks) : 0);
                if (VerticalContact(centerY, gate.Height)
                    && GateCollision(x, centerX, gate.Width, gate.Height, centerY))
                {
                    return CollisionMustEndReplay(tick, endTick, tapIndex, taps.Count, cleared);
                }
                if (VerticalContact(centerY, gate.Height))
                {
                    foreach (var echo in echoes)
                    {
                        if (echo.TargetIndex == gate.Index && SealCollision(x, echo.X, echo.HalfWidth, centerY, gate.Height))
                        {
                            return CollisionMustEndReplay(tick, endTick, tapIndex, taps.Count, cleared);
                        }
                    }
                }
            }

            // Captures happen after all collisions on the crossing tick.
            while (nextEchoSource < echoSourceIndices.Length)
            {
                var sourceIndex = echoSourceIndices[nextEchoSource];
                var source = gates[sourceIndex];
                if (previousScroll >= source.Distance || scroll < source.Distance)
                {
                    break;
                }
                var halfWidth = source.Index < 40 ? 8 : 12;
                echoes.Add(new Echo(source.Index + 2, x, halfWidth));
                nextEchoSource++;
            }

            while (cleared < GateCount
                && scroll > gates[cleared].Distance + gates[cleared].Height / 2 + PlayerRadius)
            {
                var clearedIndex = cleared++;
                echoes.RemoveAll(echo => echo.TargetIndex <= clearedIndex);
            }
            previousScroll = scroll;
        }

        if (endTick != GameArcadeRules.MaximumTick || tapIndex != taps.Count)
        {
            throw new GameFault(400, "arcade_replay_invalid");
        }
        return cleared;
    }

    private static void ValidateReplay(int endTick, IReadOnlyList<int> taps)
    {
        if (endTick is < 1 or > GameArcadeRules.MaximumTick || taps.Count > GameArcadeRules.MaximumInputs)
        {
            throw new GameFault(400, "arcade_replay_invalid");
        }
        var previous = -1;
        foreach (var tap in taps)
        {
            if (tap < 0 || tap >= endTick || tap < previous)
            {
                throw new GameFault(400, "arcade_replay_invalid");
            }
            previous = tap;
        }
    }

    private static int CollisionMustEndReplay(int tick, int endTick, int tapIndex, int tapCount, int score)
    {
        if (tick + 1 != endTick || tapIndex != tapCount)
        {
            throw new GameFault(400, "arcade_replay_invalid");
        }
        return score;
    }

    private static Gate[] BuildGates(uint seed)
    {
        var gates = new Gate[GateCount];
        var random = seed == 0 ? 1u : seed;
        for (var index = 0; index < gates.Length; index++)
        {
            if (index == 0)
            {
                gates[index] = new Gate(0, 120, 244, 168, 22);
                continue;
            }

            var block = (index - 1) / 12;
            var slot = (index - 1) % 12;
            if (slot == 0)
            {
                random = NextRandom(random);
            }

            var pattern = block < 3 ? block : (int)(random % 3);
            var mirrored = index >= 35 && (random & 1) == 1;
            var offset = slot < 3 ? PatternOffset(pattern, slot)
                : index >= 60 && slot == 3 ? -48 : 0;
            if (mirrored)
            {
                offset = -offset;
            }
            var echoSource = index >= 20 && slot == 5 || index >= 100 && slot == 6;
            var sourceIndex = index - 2;
            var sourceSlot = (sourceIndex - 1) % 12;
            var echoTarget = sourceIndex >= 20 && sourceSlot == 5
                || sourceIndex >= 100 && sourceSlot == 6;
            var recovery = slot is >= 9 and <= 11;
            var longGate = pattern == 1 && slot == 2 || pattern == 2 && slot == 0
                || index >= 90 && slot == 2;
            var center = recovery || echoSource || echoTarget ? 180 : 180 + offset;
            var tier = index / 12;
            var width = echoTarget ? 264 : Math.Max(longGate ? 132 : 104,
                (longGate ? 172 : 164) - tier * 4);
            if (recovery)
            {
                width = Math.Max(160, width);
            }
            if (echoSource)
            {
                width = Math.Max(148, width);
            }
            var height = longGate ? 72 : 22;
            var moving = index >= 15 && slot == 3 || index >= 35 && slot == 4
                || index >= 90 && slot <= 2;
            if (moving)
            {
                width = Math.Max(index < 35 ? 156 : longGate ? 156 : 140, width);
            }
            var motionSign = 1;
            if (index >= 60 && slot == 4 && block % 2 == 1)
            {
                motionSign = -1;
            }
            gates[index] = new Gate(index, 120 + index * 180, center, width, height,
                moving, motionSign, echoSource, echoTarget);
        }
        return gates;
    }

    private static uint NextRandom(uint value)
    {
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        return value;
    }

    private static int PatternOffset(int pattern, int slot) => slot switch
    {
        0 => pattern switch { 0 => -48, 1 => -48, _ => 0 },
        1 => pattern switch { 0 => 0, 1 => 48, _ => 48 },
        2 => pattern switch { 0 => 48, 1 => -48, _ => 0 },
        3 => -48,
        _ => 0
    };

    private static int MotionOffset(Gate[] gates, Gate gate, int tick)
    {
        var anchorIndex = gate.Index - (gate.Index - 1) % 12 + 3;
        var anchorDistance = gates[Math.Min(anchorIndex, gates.Length - 1)].Distance;
        var phase = 48 - anchorDistance * 5 / 11;
        phase = (phase % 192 + 192) % 192;
        var u = (tick + phase) % 192;
        var amplitude = gate.Index >= 90 ? 24 : gate.Index >= 35 ? 18 : 12;
        var triangle = Math.Min(u, 192 - u) * 2 * amplitude / 96;
        return gate.MotionSign * (-amplitude + triangle);
    }

    private static bool VerticalContact(int centerY, int height)
        => Math.Abs(PlayerY - centerY) <= height / 2 + PlayerRadius;

    private static bool GateCollision(int x, int centerX, int width, int height, int centerY)
    {
        var left = centerX - width / 2;
        var right = centerX + width / 2;
        return RectangleCollision(x, PlayerY, 16, left, centerY - height / 2, centerY + height / 2)
            || RectangleCollision(x, PlayerY, right, 344, centerY - height / 2, centerY + height / 2);
    }

    private static bool SealCollision(int x, int sealX, int halfWidth, int centerY, int height)
        => RectangleCollision(x, PlayerY, sealX - halfWidth, sealX + halfWidth,
            centerY - height / 2, centerY + height / 2);

    private static bool RectangleCollision(int x, int y, int left, int right, int top, int bottom)
    {
        var nearestX = Math.Clamp(x, left, right);
        var nearestY = Math.Clamp(y, top, bottom);
        var dx = x - nearestX;
        var dy = y - nearestY;
        return dx * dx + dy * dy <= PlayerRadius * PlayerRadius;
    }
}
