# Consumer game UI

## Approved direction

**The Midnight Table** was approved on 26 October 2026 for Wand Night Stand's smartphone opening. The selected reference is the third six-screen board stored at [`reference/midnight-table-board.png`](reference/midnight-table-board.png). Its petrol felt, dark walnut, ivory paper, brass and crimson velvet form one continuous miniature portrait theatre. Options 1 and 2 are not selectable alternatives.

Design approval, implementation and verification are separate records. Design approval locks the target; it does not authorize implementation of unrelated screens or establish that code works.

| Screen ID | State | Design | Implementation | Verification |
| --- | --- | --- | --- | --- |
| WNS-OPEN-A | First arrival | Approved · Option 3 · 2026-10-26 | Implemented | [Browser verified](evidence/midnight-table/A-390.png); native-device acceptance pending |
| WNS-OPEN-B | Returning sign-in | Approved · Option 3 · 2026-10-26 | Implemented | [Browser verified](evidence/midnight-table/B-390.png); native-device acceptance pending |
| WNS-OPEN-C | Invitation and character form | Approved · Option 3 · 2026-10-26 | Implemented | [Browser verified](evidence/midnight-table/C-390.png); native-device acceptance pending |
| WNS-OPEN-D | Preparation | Approved · Option 3 · 2026-10-26 | Implemented | [Browser verified](evidence/midnight-table/D-390.png); native-device acceptance pending |
| WNS-OPEN-E | Portrait ready and waiting | Approved · Option 3 · 2026-10-26 | Implemented | [Browser verified](evidence/midnight-table/E-390.png); native-device acceptance pending |
| WNS-OPEN-F | Authorized group opening | Approved · Option 3 · 2026-10-26 | Implemented | [Browser verified](evidence/midnight-table/F-390.png); native-device acceptance pending |

The approved target is the local board above. Do not copy private design-space links, planning identifiers, invitation data or guest examples into this public repository. Fixture names and schedules are never production data.

## Implementation map

- Consumer tokens and theme-scoped appearance: `mobilni-aplikace/src/application/consumer-brand.css`; shared token outputs come from `design-system/tokens.json` via `python scripts/generate_design_tokens.py`.
- Scene composition: `mobilni-aplikace/src/components/scene/` (`SceneShell`, `PaperSurface`, `CharacterCountdown`).
- Feature-owned sign-in and invitation state: `mobilni-aplikace/src/features/identity/SignInView.vue`.
- Launch, preparation and group access presentation: `mobilni-aplikace/src/features/launch/`; curtain authority remains with that feature.
- Production art and provenance: `mobilni-aplikace/src/assets/brand/midnight/manifest.json` and adjacent master/runtime files.
- Motion: shared scene styles and `PortraitCurtain.vue`; reduced motion must keep the same actions and final state.

Presentation components compose approved visual structure. Identity, consent, invitation validation, portrait generation, group authorization and server state remain owned by their existing application features.

## Adding the next approved screen

Invoke `$wns-implement-locked-screen` for the single requested Wand Night Stand smartphone screen. Resolve its current approved local specification, read this guide and only the affected source, then compose the scene, paper, action, identity/countdown and portrait elements that apply. Add only missing artwork and recovery states. Render the real mobile view against the selected reference, run focused interaction/accessibility checks, and update that screen's three status fields with actual evidence. Approval of one direction never grants scope to implement other screens.

Use this short specification template for a screen awaiting design approval:

```text
Purpose:
Visual reference and revision:
Layout and hierarchy:
Actions and destinations:
Required states (loading, error, offline, recovery, reduced motion):
Data and access boundaries:
Mobile behavior (safe areas, keyboard, text size, narrow widths):
Acceptance criteria and evidence:
```

## Consumer boundary

This direction applies to the Wand Night Stand consumer journey only. Simulator, CAD/viewer, firmware and engineering surfaces keep Vapour Chrome and its existing shared tokens. Keep both persisted theme identifiers and ensure paper, controls, focus, and statuses remain readable in supported themes. See [`DESIGN.md`](../DESIGN.md), [`design-system/README.md`](README.md), and the focused [locked-screen skill](../.agents/skills/wns-implement-locked-screen/SKILL.md).

## Verification and development preview

The [implementation evidence](evidence/midnight-table/README.md) records actual checks, representative captures and native-device gaps. Run `npm run build --prefix mobilni-aplikace`, then `npm run preview:opening --prefix mobilni-aplikace`. Its loopback fixture picker exposes six real application states and the important failures without live private data or a production debug route.

Runtime art has nine variants totaling about 583 kB. Editable source PNGs and export instructions live in [`assets/midnight/`](assets/midnight/README.md); `scripts/prepare_midnight_assets.py` regenerates them and the manifest. The portrait aperture is exactly 4:5; independent wings move within it while the outer frame stays fixed. Opening art has a 550ms optional entrance through `SceneShell`'s `data-scene-enter` marker, pauses through its `active` prop and has a static reduced-motion state. Buttons work immediately.

For the next separately approved screen, the implementation normally starts with this composition, using its existing feature state:

```vue
<SceneShell labelled-by="screen-title">
  <PaperSurface>
    <h1 id="screen-title">{{ approvedTitle }}</h1>
    <!-- Existing feature-owned content and recovery states. -->
    <AwButton variant="primary" :busy="busy" @click="existingAction">
      {{ approvedAction }}
    </AwButton>
  </PaperSurface>
</SceneShell>
```

Example invocation: `$wns-implement-locked-screen Implement WNS-<screen-id> from its approved local specification; reuse the shared scene and connect the existing feature owner.` This requests only that screen. WNS-OPEN-D was used to exercise the workflow for this delivery; see the evidence record.
