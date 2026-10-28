import { ref } from 'vue';
import type { Account } from '../features/identity/session';

// Display mode never changes the account's server-authorized roles.
const playerAccountId = ref<string | null>(null);

export function isOwnerPlayer(account: Account | null): boolean {
  return !!account?.roles.includes('Owner') && playerAccountId.value === account.id;
}

export function setOwnerPlayer(account: Account | null, player: boolean): void {
  playerAccountId.value = player && account?.roles.includes('Owner') ? account.id : null;
}
