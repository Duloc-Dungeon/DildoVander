import { describe, expect, it } from 'vitest';
import { androidAppLink, captureAndroidAppLink, androidNavigationKey } from './androidAppLinks';

describe('verified Android app links', () => {
  it('retains the one-use claim only for the existing pre-module capture', () => {
    const invite = 'https://dildovander.de/signin#invite=' + 'a'.repeat(43);
    expect(androidAppLink(invite)).toBe('/signin#invite=' + 'a'.repeat(43));
    expect(androidAppLink('https://dildovander.de/keepsake/evening/#claim')).toBe('/keepsake/evening/#claim');
  });
  it('rejects other origins, credentials, schemes and operational routes', () => {
    for (const url of ['https://evil.test/signin#invite=token', 'http://dildovander.de/signin',
      'https://user:secret@dildovander.de/signin', 'javascript:alert(1)',
      'https://dildovander.de:8443/signin', 'https://dildovander.de/settings/admin',
      'https://dildovander.de/keepsake/../settings', 'https://dildovander.de/signin-other']) {
      expect(androidAppLink(url)).toBeNull();
    }
  });
  it('captures cold and warm invitations in memory without tokens in router history', () => {
    expect(captureAndroidAppLink('https://dildovander.de/signin#invite=' + 'a'.repeat(43))).toBe('/signin');
    expect(window.__dildovanderEnrollment).toEqual({ kind: 'invite', token: 'a'.repeat(43) });
    expect(captureAndroidAppLink('https://dildovander.de/signin#recovery=' + 'b'.repeat(43))).toBe('/signin');
    expect(window.__dildovanderEnrollment).toBeUndefined();
    expect(androidNavigationKey('/signin')).toMatch(/^\/signin:\d+$/);
    delete window.__dildovanderEnrollment;
  });
});
