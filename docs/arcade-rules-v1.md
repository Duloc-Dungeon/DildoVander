# Kouzlo bez dozoru: deterministic rules v1

The TypeScript simulation and C# replay verifier implement these integer rules.
Rendering, audio, wall-clock delays and screen size never affect the result.

- Logical field 360 x 640; spark starts at (180, 480), radius 7, direction +1.
- 60 simulation ticks/second. At the beginning of tick N (zero based), apply
  all recorded taps equal to N, each reversing direction. Then increment tick,
  move x by direction * 2. Scroll = tick * 2; spark y stays 480.
- Side walls are x=16 and x=344. Touching a wall (x-radius <= 16 or
  x+radius >= 344) ends the run before any score is awarded for that tick.
- Gate i distance = 120 + i*180; its screen center y = 480-distance+scroll.
  Gate 0 center x=244, gap width=168, height=22.
- Unsigned xorshift32 initialized to seed (zero becomes 1). For every gate
  after gate 0: state ^= state << 13; state ^= state >>> 17;
  state ^= state << 5; truncate each operation to uint32. Next center is
  clamp(previous center + ((state % 7)-3)*32, 92, 268).
- Gate i>0 width = max(88, 152-floor(i/5)*4). For i%7==6, height=72
  and width=max(104,width); otherwise height=22. A gate consists of two
  solid axis-aligned rectangles [16,center-width/2] and
  [center+width/2,344], with vertical extent center y +/- height/2.
- Exact circle/rectangle collision uses the nearest point on each rectangle;
  squared distance <= 49 means collision. Test the first uncleared gate.
  A gate clears only when scroll > distance+height/2+7. Add exactly one point.
- Tick 108000 ends a surviving run successfully (30 minutes). Generate
  1201 gates. Replay inputs are nondecreasing integer ticks >=0 and <endTick;
  duplicate ticks are allowed (two taps cancel). Maximum 24000 inputs.
- Submission must end exactly on the first collision or the maximum tick.
  Reject runs that stop early, continue after collision or contain unused taps.
  The server calculates score; no client-supplied score is trusted.

## API contract

Authenticated, existing eligible game participant/group checks apply.
All routes below are relative to `/app/v1/game/arcade`.

- GET empty path: `{rulesVersion:'spark-v1', playerId, groupId, best,
  ranking:[{id,displayName,score,rank}]}`. Rank by best gates, shared ranks
  on ties; exclude ineligible/other-group accounts; resolve public character names.
- POST `/runs` `{operationId}`: idempotently issues `{runId,seed,rulesVersion,
  expiresAt}` (Unix milliseconds). Group has one stable course seed per version.
  Bind run to authenticated user, group and rules; expire after 2 hours.
- POST `/runs/{runId}/finish` `{endTick,taps}`: replay and atomically save
  max(previousBest,verifiedScore), return `{score,best,newBest}`.
  Identical retry returns the same receipt; conflicting replay is rejected.
  Verify ownership before returning receipts. Bound payload, rate and replay work.

The client plays practice immediately when offline or issuance fails and labels
it as practice. A ranked run requires a successfully issued ticket before start.
Network work never blocks movement or a practice restart. Failed submission is
retryable with the same run and replay. Personal practice best is separate from
the server-verified group record. Pauses stop simulation and add no replay ticks.
