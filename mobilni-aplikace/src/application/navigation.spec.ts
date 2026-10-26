import { describe, expect, it } from 'vitest';
import { safeReturnPath } from './navigation';

describe('customer and staff sign-in return paths', () => {
  it('preserves local customer and staff destinations, including their query and fragment', () => {
    for (const path of ['/testing-sessions?session=test-1', '/me', '/me/', '/me/character', '/me/conversation?resume=1', '/me/keepsakes#saved', '/me/account', '/admin/customer-sessions']) {
      expect(safeReturnPath(path)).toBe(path);
    }
  });

  it('rejects foreign origins, controls, API paths and similar but unapproved prefixes', () => {
    for (const path of ['//evil.test/me', 'https://evil.test/me', '/me\\evil', '/me\naccount', '/me-too', '/administrator', '/app/v1/session', '/guest/v1', '/signin', '/player', '/playground', '//evil.test/play']) {
      expect(safeReturnPath(path)).toBe('/');
    }
  });

  it('preserves game entry and match destinations through authentication', () => {
    const id = 'a'.repeat(32);
    for (const path of ['/play', '/play?browse=1', '/play/challenge', '/play/practice', '/play/chronicle',
      '/play/teams', `/play/team/${id}`, `/play/match/${id}`, '/play/boss', `/play/boss/${id}`,
      '/play/keeper', '/play/messages', '/play/ranking#combat']) {
      expect(safeReturnPath(path)).toBe(path);
    }
  });
});
