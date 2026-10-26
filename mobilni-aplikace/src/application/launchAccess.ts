import { ref, watch } from 'vue';
import { account, accountRequest } from '../features/identity/session';
import { isCustomerAccount } from './access';

/** Null is deliberately treated as prelaunch while the server policy is unknown. */
export const launchAllowed = ref<boolean | null>(null);
export type CustomerMembershipState = 'active' | 'revoked' | 'unassigned' | null;
export const customerMembershipState = ref<CustomerMembershipState>(null);
export const customerFixedUniverse = ref<string | null>(null);
let membershipGeneration = 0;
let membershipAccountKey: string | null = null;
let membershipRevision = -1;
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
