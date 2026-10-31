// Static shader/uniform implementations preserve the application's strict CSP.
import 'pixi.js/unsafe-eval';
import { Application, Assets, Graphics, Sprite, type Texture } from 'pixi.js';
import sparkUrl from '../../assets/arcade/spark.webp';
import { FIELD, gateCenterAt, gateY, scrollAt, type Gate, type SparkState } from './sparkEngine';

export interface SparkRenderer { draw(state: SparkState, course: readonly Gate[], reduced: boolean): void; destroy(): void }

/** Pixi owns presentation only. Geometry is always drawn from simulation units. */
export async function createSparkRenderer(host: HTMLElement, onContextLost: () => void): Promise<SparkRenderer> {
  const app = new Application();
  try {
    await app.init({ width: FIELD.width, height: FIELD.height, backgroundAlpha: 0,
      resolution: Math.min(window.devicePixelRatio || 1, 2), autoDensity: true,
      antialias: true, preference: 'webgl', autoStart: false, sharedTicker: false });
  } catch (error) {
    if (app.renderer) app.destroy(true, { children: true });
    throw error;
  }
  app.canvas.setAttribute('aria-hidden', 'true');
  app.canvas.style.width = '100%'; app.canvas.style.height = '100%';
  host.append(app.canvas);
  const lost = (event: Event) => { event.preventDefault(); onContextLost(); };
  app.canvas.addEventListener('webglcontextlost', lost);
  const shelves = new Graphics();
  const effects = new Graphics();
  const core = new Graphics();
  app.stage.addChild(shelves, effects);
  let flame: Sprite | undefined;
  try {
    const texture = await Assets.load<Texture>(sparkUrl);
    flame = new Sprite(texture);
    flame.anchor.set(.5, .68); flame.width = 40; flame.height = 40;
    app.stage.addChild(flame);
  } catch { /* The precise visible core below remains playable without decorative art. */ }
  app.stage.addChild(core);
  const trail: { x: number; y: number }[] = [];
  let previousTick = 0;
  let previousScore = 0;
  let scoredAt = -100;
  let destroyed = false;
  return {
    draw(state, course, reduced) {
      if (destroyed) return;
      const advance = state.tick - previousTick;
      if (advance < 0) { trail.length = 0; previousScore = 0; scoredAt = -100; }
      if (advance > 0 && !reduced) {
        for (const point of trail) point.y += scrollAt(state.tick) - scrollAt(previousTick);
        trail.unshift({ x: state.x, y: FIELD.playerY });
        trail.length = Math.min(trail.length, 18);
      }
      if (state.score > previousScore) scoredAt = state.tick;
      previousScore = state.score; previousTick = state.tick;
      shelves.clear(); effects.clear(); core.clear();
      // These lines are the actual lethal side boundaries, not the background shelves.
      shelves.moveTo(FIELD.left, 0).lineTo(FIELD.left, 640).stroke({ color: 0xffc662, alpha: .6, width: 1 });
      shelves.moveTo(FIELD.right, 0).lineTo(FIELD.right, 640).stroke({ color: 0xffc662, alpha: .6, width: 1 });
      for (let i = Math.max(0, state.score - 1); i < Math.min(course.length, state.score + 5); i++) {
        const gate = course[i]!;
        const y = gateY(gate, state.tick) - gate.height / 2;
        if (y > 660 || y + gate.height < -20) continue;
        const center = gateCenterAt(gate, state.tick);
        const gapLeft = center - gate.width / 2;
        const gapRight = center + gate.width / 2;
        const cleared = i < state.score;
        const edge = cleared ? 0xb8f3d4 : 0xffc662;
        if (gate.motion) {
          // Rails show the full travel range. They stay outside the playable
          // opening and remain visible with reduced decorative motion.
          for (const side of [-1, 1]) {
            const railCenter = gate.center + side * gate.width / 2;
            const left = railCenter - gate.motion.amplitude;
            const right = railCenter + gate.motion.amplitude;
            shelves.moveTo(left, y - 7).lineTo(right, y - 7)
              .moveTo(left, y - 11).lineTo(left, y - 3)
              .moveTo(right, y - 11).lineTo(right, y - 3)
              .stroke({ color: 0xb8f3d4, width: 2 });
          }
          const direction = gateCenterAt(gate, state.tick + 8) >= center ? 1 : -1;
          const arrow = center;
          shelves.moveTo(arrow - direction * 4, y - 11)
            .lineTo(arrow, y - 7)
            .lineTo(arrow - direction * 4, y - 3)
            .stroke({ color: 0xb8f3d4, width: 2 });
        }
        for (const [x, width] of [[16, gapLeft - 16], [gapRight, 344 - gapRight]]) {
          shelves.rect(x!, y, width!, gate.height).fill({ color: 0x3e2e24, alpha: .98 });
          shelves.rect(x!, y, width!, gate.height).stroke({ color: edge, width: 2, alignment: 1 });
          shelves.moveTo(x! + 4, y + 5).lineTo(x! + width! - 4, y + 5).stroke({ color: 0x997249, alpha: .75, width: 1 });
          shelves.moveTo(x! + 4, y + gate.height - 5).lineTo(x! + width! - 4, y + gate.height - 5).stroke({ color: 0x997249, alpha: .5, width: 1 });
          // Small brass rivets make the solid surfaces readable without hiding their edges.
          shelves.circle(x! + 5, y + gate.height / 2, 1.8).fill(edge);
          shelves.circle(x! + width! - 5, y + gate.height / 2, 1.8).fill(edge);
        }
        shelves.moveTo(gapLeft + 4, y + gate.height / 2).lineTo(gapLeft + 10, y + gate.height / 2)
          .moveTo(gapRight - 4, y + gate.height / 2).lineTo(gapRight - 10, y + gate.height / 2)
          .stroke({ color: 0xb8f3d4, width: 2, alpha: .8 });
        if (gate.pattern) {
          // Distinct engraved silhouettes identify the three learned families.
          const x = FIELD.left + 14;
          const middle = y + gate.height / 2;
          if (gate.pattern === 'stairs') shelves.moveTo(x, middle + 4).lineTo(x + 4, middle + 4)
            .lineTo(x + 4, middle).lineTo(x + 8, middle).lineTo(x + 8, middle - 4).lineTo(x + 12, middle - 4);
          else if (gate.pattern === 's') shelves.moveTo(x + 12, middle - 4).lineTo(x, middle - 4)
            .lineTo(x, middle).lineTo(x + 12, middle).lineTo(x + 12, middle + 4).lineTo(x, middle + 4);
          else shelves.moveTo(x, middle - 6).lineTo(x, middle + 6)
            .moveTo(x + 10, middle - 6).lineTo(x + 10, middle + 6);
          shelves.stroke({ color: 0xffc662, width: 2 });
        }
        if (gate.echoTarget !== undefined || gate.echoSource !== undefined) {
          for (const x of [gapLeft - 12, gapRight + 12]) {
            const middle = y + gate.height / 2;
            shelves.poly([x, middle - 5, x + 5, middle, x, middle + 5, x - 5, middle], true)
              .stroke({ color: 0xf8b5a5, width: 2 });
            if (gate.echoTarget !== undefined) shelves.fill(0xf8b5a5);
          }
        }
        const echo = state.echoes.find(value => value.target === gate.index);
        if (echo) {
          shelves.rect(echo.x - echo.halfWidth, y, echo.halfWidth * 2, gate.height)
            .fill({ color: 0x653b48, alpha: .95 }).stroke({ color: 0xf8b5a5, width: 2, alignment: 1 });
          shelves.moveTo(echo.x - 4, y + 5).lineTo(echo.x + 4, y + gate.height - 5)
            .moveTo(echo.x + 4, y + 5).lineTo(echo.x - 4, y + gate.height - 5)
            .stroke({ color: 0xf8b5a5, width: 2 });
        }
      }
      if (!reduced) {
        trail.forEach((point, i) => effects.circle(point.x, point.y, Math.max(.8, 4 - i / 5))
          .fill({ color: 0xb8f3d4, alpha: (1 - i / 18) * .24 }));
        const pulse = (state.tick - scoredAt) / 30;
        if (pulse >= 0 && pulse < 1) effects.circle(state.x, FIELD.playerY, 12 + pulse * 30)
          .stroke({ color: 0xffc662, width: 2, alpha: 1 - pulse });
      }
      if (flame) { flame.position.set(state.x, FIELD.playerY); flame.rotation = state.direction * .24; flame.alpha = state.status === 'crashed' ? .45 : .9; }
      core.circle(state.x, FIELD.playerY, FIELD.radius).fill(0xf5f5f0).stroke({ color: 0xb8f3d4, width: 1 });
      const arrowX = state.x + state.direction * 19;
      core.moveTo(arrowX - state.direction * 4, FIELD.playerY - 3).lineTo(arrowX, FIELD.playerY)
        .lineTo(arrowX - state.direction * 4, FIELD.playerY + 3).stroke({ color: 0xf5f5f0, width: 2 });
      if (state.collision) {
        const { x, y } = state.collision;
        core.circle(x, y, 12).stroke({ color: 0xf8b5a5, width: 2 });
        core.moveTo(x - 5, y - 5).lineTo(x + 5, y + 5).moveTo(x + 5, y - 5).lineTo(x - 5, y + 5)
          .stroke({ color: 0xf8b5a5, width: 2 });
      }
      app.render();
    },
    destroy() {
      if (destroyed) return;
      destroyed = true;
      app.canvas.removeEventListener('webglcontextlost', lost);
      app.destroy(true, { children: true, texture: false, textureSource: false });
    },
  };
}
