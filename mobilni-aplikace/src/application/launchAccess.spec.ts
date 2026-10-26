import { beforeEach, describe, expect, it, vi } from 'vitest';

const state = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), accountRequest: state.request };
});
import { account } from '../features/identity/session';
import { customerMembershipState, refreshCustomerMembership } from './launchAccess';

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(done => { resolve = done; });
  return { promise, resolve };
}

beforeEach(() => {
  state.request.mockReset();
  customerMembershipState.value = null;
  account.value = { id: 'customer-a', displayName: 'A', roles: ['Customer'] } as never;
});

describe('customer membership refresh ordering', () => {
  it('does not let an old active read overwrite a revocation event, and accepts a later restore', async () => {
    const oldRead = deferred<{ state: 'active'; accessRevision: number }>();
    state.request.mockReturnValueOnce(oldRead.promise).mockResolvedValueOnce({ state: 'active', accessRevision: 8 });
    const pending = refreshCustomerMembership();
    window.dispatchEvent(new Event('customer-access-revoked'));
    expect(customerMembershipState.value).toBe('revoked');
    oldRead.resolve({ state: 'active', accessRevision: 7 });
    await pending;
    expect(customerMembershipState.value).toBe('revoked');
    await refreshCustomerMembership();
    expect(customerMembershipState.value).toBe('active');
  });

  it('ignores a lower access revision returned after a newer restored state', async () => {
    const olderRead = deferred<{ state: 'revoked'; accessRevision: number }>();
    state.request.mockReturnValueOnce(olderRead.promise).mockResolvedValueOnce({ state: 'active', accessRevision: 9 });
    const pendingOlder = refreshCustomerMembership();
    await refreshCustomerMembership();
    expect(customerMembershipState.value).toBe('active');
    olderRead.resolve({ state: 'revoked', accessRevision: 8 });
    await pendingOlder;
    expect(customerMembershipState.value).toBe('active');
  });

  it('ignores a response belonging to the previous account', async () => {
    const oldRead = deferred<{ state: 'active'; accessRevision: number }>();
    state.request.mockReturnValueOnce(oldRead.promise);
    const pending = refreshCustomerMembership();
    account.value = { id: 'customer-b', displayName: 'B', roles: ['Customer'] } as never;
    oldRead.resolve({ state: 'active', accessRevision: 20 });
    await pending;
    expect(customerMembershipState.value).toBe(null);
  });
});
