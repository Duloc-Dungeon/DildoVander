import { ref, watch } from 'vue';
import { account, accountRequest } from '../features/identity/session';

/** Null is deliberately treated as prelaunch while the server policy is unknown. */
export const launchAllowed = ref<boolean | null>(null);
watch(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : null, () => {
  launchAllowed.value = null;
}, { flush: 'sync' });

export async function refreshLaunchAccess(): Promise<boolean> {
  try {
    const result = await accountRequest<{ allowed: boolean }>('/app/v1/launch');
    launchAllowed.value = result.allowed === true;
  } catch {
    launchAllowed.value = null;
  }
  return launchAllowed.value === true;
}
