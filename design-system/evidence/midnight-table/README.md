# Midnight Table implementation evidence

Verified 26 October 2026 with synthetic fixtures against the actual production Vue bundle. The [approved local board](../../reference/midnight-table-board.png) remains the design target. These are rendered application captures, with live controls and fixture content.

| State | 390px capture |
| --- | --- |
| A · First arrival through the anonymous home redirect | [Arrival](A-390.png) |
| B · Returning passkey sign-in | [Sign-in](B-390.png) |
| C · Combined invitation/character form | [Invitation](C-390.png) |
| D · Preparation | [Closed theatre](D-390.png) |
| E · Ready and waiting | [Revealed theatre](E-390.png) |
| F · Server-authorized opening | [Group opening](F-390.png) |

Additional captures: [fixed universe](failure-fixed-universe.png), [unreachable enrollment](failure-enrollment-error.png), [focused form at 390×440 with 125% text](C-keyboard-larger-olivander.png). The latter emulates keyboard space; it does not show a native keyboard.

Checks completed:

- `npm run build --prefix mobilni-aplikace`: contract generation check, Vue/TypeScript check and production build pass. Existing bundle-size and ineffective dynamic-import warnings remain.
- Targeted Vitest: 72 tests in seven files pass (`SignInView`, `invitationRegistration`, `LaunchView`, `PortraitCurtain`, `CharacterCountdown`, `launchPresentation`, `privateCharacterPortrait`). Includes cancellation and draft preservation, uncertain save reconciliation, consent, immediate save/visible intro timing, account isolation, pause/revocation, server authorization and countdown-zero behavior.
- `npm run test:opening:browser --prefix mobilni-aplikace`: 58 captures; all six states at 320/375/390/430px, both persisted themes, reduced motion, focused/larger-text form, fixed universe, enrollment error, consent, portrait failure, offline, pause and revocation. No JavaScript errors or horizontal/clipped-control failures.
- `npm run test:invitation:browser --prefix mobilni-aplikace`: 44 captures and real-motion recording; 80-character names, 160-character universes, full personalized copy, 125% text, stable countdown/frame, interrupted reveal/reload/return/revision, authorized image reads without generation. No JavaScript errors. Outputs default to ignored `.codex/artifacts/wand-invitation/`.
- `npm run test:launch:browser --prefix mobilni-aplikace`: mobile/tablet/desktop synthetic integration passes, including creation, portrait retry and invitation account switching. Existing operator fixture checks remain covered.
- `npm test --prefix design-system`: 15 JavaScript and 11 Python tests pass. `python scripts/check_design_system.py` and `python scripts/generate_design_tokens.py --check` pass.
- System skill-creator `quick_validate.py` passes for `.agents/skills/wns-implement-locked-screen`. The focused workflow was exercised against WNS-OPEN-D: resolved its approved local reference, inspected only launch/component sources, reused the scene/paper/countdown/stage, compared the actual 390px result, ran targeted checks and recorded this evidence.
- Independent read-only correctness review completed; entry navigation, pause recovery, truthful enrollment recovery and palette-scope findings were fixed and regression-tested.

Native iPhone/Android passkey prompts and cancellation, real keyboards, physical safe areas, pause/resume and deep-link handling on devices were not exercised. Browser fixtures and component tests preserve those integration paths but do not establish native acceptance. These local checks made no live character-generation, deployment or hardware calls.

Regenerate all opening captures with `npm run test:opening:browser --prefix mobilni-aplikace`; default output is the OS temporary directory under `wns-midnight-table`. Run `npm run preview:opening --prefix mobilni-aplikace` after building, then open the printed loopback URL. The fixture picker exposes A–F and failures, uses only synthetic data and is never bundled into the production app.
