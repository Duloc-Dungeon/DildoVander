import { ref, watch } from 'vue';
import { account, accountRequest } from '../features/identity/session';
import { isCustomerAccount } from './access';

/** Null is deliberately treated as prelaunch while the server policy is unknown. */
export const launchAllowed = ref<boolean | null>(null);
export const launchPolicySnapshot = ref<{ allowed: boolean; audience?: string; launchAt: number | null; openedAt?: number | null; serverNow: number; observedAt?: number } | null>(null);
export type CustomerMembershipState = 'active' | 'revoked' | 'unassigned' | null;
export const customerMembershipState = ref<CustomerMembershipState>(null);
export const customerFixedUniverse = ref<string | null>(null);
let membershipGeneration = 0;
let membershipAccountKey: string | null = null;
let membershipRevision = -1;
let launchRequest = 0;
if (typeof window !== 'undefined') {
  window.addEventListener('customer-access-revoked', () => {
    membershipGeneration++;
    customerMembershipState.value = 'revoked';
  });
}
watch(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : null, (key) => {
  if (key !== membershipAccountKey) {
    membershipAccountKey = key;
    membershipGeneration++;
    membershipRevision = -1;
    customerMembershipState.value = null;
    customerFixedUniverse.value = null;
  }
  launchAllowed.value = null;
  launchPolicySnapshot.value = null;
  launchRequest++;
}, { flush: 'sync' });

export async function refreshLaunchAccess(): Promise<boolean> {
  const request = ++launchRequest;
  const owner = account.value?.id;
  try {
    const result = await accountRequest<{ allowed: boolean; audience?: string; launchAt: number | null; openedAt?: number | null; serverNow: number }>('/app/v1/launch');
    if (request === launchRequest && owner === account.value?.id) {
      launchAllowed.value = typeof result?.allowed === 'boolean' ? result.allowed : null;
      launchPolicySnapshot.value = { ...result, observedAt: performance.now() };
    }
  } catch {
    if (request === launchRequest && owner === account.value?.id) launchAllowed.value = null;
  }
  return launchAllowed.value === true;
}

export async function refreshCustomerMembership(): Promise<CustomerMembershipState> {
  const key = account.value ? `${account.value.id}:${account.value.roles.join(',')}` : null;
  if (key !== membershipAccountKey) {
    membershipAccountKey = key;
    membershipGeneration++;
    membershipRevision = -1;
    customerMembershipState.value = null;
    customerFixedUniverse.value = null;
  }
  if (!isCustomerAccount(account.value)) {
    customerMembershipState.value = null;
    customerFixedUniverse.value = null;
    return null;
  }
  const generation = membershipGeneration;
  try {
    const result = await accountRequest<{ state: Exclude<CustomerMembershipState, null>; accessRevision?: number; fixedUniverse?: string | null }>('/app/v1/customer/access');
    if (generation !== membershipGeneration || key !== (account.value ? `${account.value.id}:${account.value.roles.join(',')}` : null)) return customerMembershipState.value;
    if (typeof result.accessRevision === 'number' && result.accessRevision < membershipRevision) return customerMembershipState.value;
    if (typeof result.accessRevision === 'number') membershipRevision = result.accessRevision;
    customerMembershipState.value = ['active', 'revoked', 'unassigned'].includes(result.state) ? result.state : null;
    customerFixedUniverse.value = typeof result.fixedUniverse === 'string' && result.fixedUniverse.length > 0 ? result.fixedUniverse : null;
  } catch {
    if (generation === membershipGeneration) { customerMembershipState.value = null; customerFixedUniverse.value = null; }
  }
  return customerMembershipState.value;
}
