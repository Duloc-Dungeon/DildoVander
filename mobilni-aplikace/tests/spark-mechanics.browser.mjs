// Real renderer + real replay states; this isolated preview never starts the app
// or talks to its backend. Complements the built-app spark.browser.mjs journey.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { createServer } from 'vite';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'wns-spark-v3-mechanics');
await mkdir(output, { recursive: true });
const server = await createServer({ configFile: false, root, logLevel: 'error',
  server: { host: '127.0.0.1', port: 0, fs: { allow: [path.resolve(root, '..')] } },
  plugins: [{ name: 'spark-mechanics-preview', configureServer(vite) {
    vite.middlewares.use('/__spark-mechanics', (_request, response) => {
      response.setHeader('Content-Type', 'text/html; charset=utf-8');
      response.end('<!doctype html><html lang="cs"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Simulace Noční inventury</title><style>body{margin:0;background:#101615;color:#fff;font:14px sans-serif}h1,p{margin:12px;font-size:14px}#field{width:min(100vw,420px);aspect-ratio:9/16;margin:auto;background:linear-gradient(#151c18,#33261e);border:1px solid #786143;box-sizing:border-box}</style><h1>Simulace Noční inventury</h1><p id="description"></p><div id="field"></div></html>');
    });
  } }],
});
await server.listen();
const address = server.httpServer.address();
const origin = `http://127.0.0.1:${address.port}`;
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader'],
  ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
const errors = [];
try {
  for (const width of [320, 390]) {
    const page = await browser.newPage({ viewport: { width, height: 844 }, reducedMotion: 'reduce' });
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/__spark-mechanics`);
    // JSON is served through Vite's explicit filesystem allowlist.
    await page.evaluate(async (fixturePath) => {
      const engine = await import('/src/features/arcade/sparkEngine.ts');
      const { createSparkRenderer } = await import('/src/features/arcade/sparkRenderer.ts');
      const { default: fixture } = await import(fixturePath);
      const renderer = await createSparkRenderer(document.querySelector('#field'), () => { throw new Error('WebGL context lost'); });
      const course = engine.createCourse(1);
      window.drawSpark = (score, extraTicks = 0) => {
        const state = engine.createSpark(); let input = 0;
        const taps = fixture.vectors[0].taps;
        while (state.score < score || extraTicks-- > 0) {
          const tap = taps[input] === state.tick;
          if (tap) input++;
          engine.stepSpark(state, course, tap ? 1 : 0);
          if (state.status !== 'flying') throw new Error(`Unexpected terminal state at ${state.tick}`);
        }
        renderer.draw(state, course, true);
        document.querySelector('#description').textContent = `${state.score} průletů · ${state.echoes.length} oznámené pečeti`;
        return { tick: state.tick, score: state.score, echoes: state.echoes,
          centers: course.slice(score, score + 3).map(gate => engine.gateCenterAt(gate, state.tick)) };
      };
    }, '/@fs/' + path.resolve(root, '../contracts/fixtures/spark-v3.json').replaceAll('\\', '/'));
    for (const [name, score] of [['patterns', 1], ['single-mover', 15], ['echo', 31], ['paired', 39], ['opposing', 87], ['moving-corridor', 97], ['two-echoes', 104]]) {
      const state = await page.evaluate(score => window.drawSpark(score), score);
      if (name === 'two-echoes') assert.equal(state.echoes.length, 2);
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
      await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true });
      if (name === 'single-mover') {
        const later = await page.evaluate(score => window.drawSpark(score, 12), score);
        assert.notDeepEqual(later.centers, state.centers, 'physical motion survives reduced-motion preference');
      }
    }
    await page.close();
  }
  assert.deepEqual(errors, []);
  console.log(`V3 real-renderer acceptance passed at 320 and 390 px: 14 screenshots in ${output}`);
} finally { await browser.close(); await server.close(); }
