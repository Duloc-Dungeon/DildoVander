import type { Duel } from '../archive/showContracts';
import type { LabEngine, LabSession } from '../persona-lab/contracts';

export type InputMode = 'text' | 'voice' | 'hybrid';
export interface CharacterInput { name: string; world: string; version?: string; description?: string; language?: string }
export interface TestCharacter {
  id: string; longTermConsent?: boolean; enrichmentOperationId?: string | null;
  profile: { profileId: string; revision: number; userInput: CharacterInput; status: string; errorCode?: string | null; updatedAt?: number;
    enrichment?: { recognizedName: string; world?: string; summary: string; model: string; promptVersion?: string; evidenceKind: string;
      conversationIdeas: string[]; ambiguities: string[]; facts?: { text: string; sourceUrls: string[] }[];
      sources?: { title: string; url: string }[]; needsClarification?: boolean; processedAt?: number;
      inputRevision?: number; inputTokens?: number; outputTokens?: number } | null };
}
export interface CharacterConversationSnapshot {
  profileId: string; revision: number; userInput: CharacterInput;
  enrichment?: TestCharacter['profile']['enrichment']; capturedAt: number;
  context?: string;
}
export interface TestSession {
  id: string; ownerId: string; virtualDeviceId: string; labSessionId: string; engine: LabEngine; inputMode: InputMode;
  status: string; characterId: string | null; createdAt: string; endedAt: string | null; revision: number;
}
export interface VirtualEvent { sequence?: number; type: string; code?: string | null; createdAt?: string; compartment?: number | null; source?: string }
export interface VirtualSuitcase {
  connected?: boolean; ready?: boolean; state?: string; currentAction?: string; selectedCompartment?: number | null;
  compartments?: { slot: number; state: string; wandName: string | null }[]; ledPattern?: string; servoCommand?: string | null; vibrationCommand?: string | null; cameraAvailable?: boolean;
  events?: VirtualEvent[];
}
export interface GeneratedWand {
  wandId: string; physicalSlot: number; name: string; material: string; core: string; lore: string; temperament: string;
  designDescription: string; affinity: string; weakness: string; spells: { action: string; name: string; description: string }[];
}
export interface WandGeneration { sessionId: string; status: string; errorCode?: string | null; candidates: GeneratedWand[]; selectedWand: GeneratedWand | null }
export interface TestKeepsake { id: string; state: string; editions: {id:string; story:{title:string;text:string}; artworkAssetId:string; provider:string;model:string;synthetic:boolean}[]; portraits: {id:string;assetId:string;provider:string;model:string;synthetic:boolean}[]; assets:{id:string;kind:string;mediaType:string}[]; jobs:{id:string;kind:string;state:string;errorCode?:string|null}[] }
export interface TestSessionDetail {
  conversationActive?: boolean; conversationOperation?: { operationId: string; phase: string; elapsedMs: number } | null;
  session: TestSession; conversation: LabSession; character: TestCharacter | null;
  latestCharacter?: TestCharacter | null; characterSnapshot?: CharacterConversationSnapshot | null; hardware: VirtualSuitcase;
  wand: WandGeneration | null; keepsakes: TestKeepsake[]; memory: {status:string;records:unknown[]}; duels?: Duel[];
}
export interface TestingCatalog { engines: LabEngine[]; capabilities: Record<string, boolean>; limitations?: string[] }
