/**
 * Proposed presentation contract only. These are not existing server DTOs.
 * The integration adapter derives the phase from the authorized workshop
 * session and actual voice playback. It owns identity, revision and event
 * deduplication; this component does not make or persist any decision.
 */
export type TrunkStagePhase =
  | 'ready'
  | 'connecting'
  | 'listening'
  | 'speaking'
  | 'waiting'
  | 'forging'
  | 'revealed'
  | 'unavailable'
  | 'error';

export type WandArtworkStatus =
  | 'not-requested'
  | 'queued'
  | 'running'
  | 'ready'
  | 'failed'
  | 'unknown';

export interface TrunkStageProjection {
  stage: TrunkStagePhase;
  /** Actual currently played assistant output, normalized to 0..1.
   * Zero for silence, queued audio, interruption and disconnected output.
   * Never derive this value from the user's microphone or a random timer.
   */
  assistantOutputLevel?: number;
  /** Short factual label supplied by the owning workshop. */
  activityLabel?: string;
  /** A canonical protected same-origin URL, or an already-authorized local
   * blob URL. Fetching with credentials/headers belongs to the adapter.
   * Do not pass model/provider URLs, access tokens, or public share links.
   */
  wandArtworkUrl?: string | null;
  artworkStatus?: WandArtworkStatus;
  /** Confirmed stored name, not a predicted model transcript. */
  wandName?: string;
  /** Hide when the owning result view already presents the name or status. */
  showCaption?: boolean;
  showArtworkStatus?: boolean;
}

export interface TrunkStageAssets {
  trunkSrc?: string;
  trunkSrcSet?: string;
  /** Actual intrinsic dimensions. Keep both aligned to the exported asset. */
  trunkWidth?: number;
  trunkHeight?: number;
  /** Optional decorative tray from this asset pack. Contains no wand. */
  revealTraySrc?: string;
  /** Default is standalone. Enable tray compositing only after the adapter
   * verifies transparent artwork with a horizontal wand composition.
   * Opaque/portrait artwork and unknown composition must stay standalone.
   */
  artworkPlacement?: 'standalone' | 'tray-horizontal';
  /** Maximum image width as a percent of the stage when composing in the
   * tray. Default 76 leaves a 12% inset at each side of the generated rim.
   */
  trayArtworkWidthPercent?: number;
}

export type TrunkStageProps = TrunkStageProjection & TrunkStageAssets;
