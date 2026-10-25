import { mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import PrivacyView from './PrivacyView.vue';
import SupportView from './SupportView.vue';
import { launchAllowed } from '../../application/launchAccess';

describe('prelaunch information pages', () => {
  beforeEach(() => { launchAllowed.value = null; });

  it.each([null, false])('shows only accurate prelaunch privacy and support information when access is %s', state => {
    launchAllowed.value = state;
    const privacy = mount(PrivacyView, { global: { stubs: ['RouterLink'] } }).text();
    const support = mount(SupportView, { global: { stubs: ['RouterLink'] } }).text();

    expect(privacy).toContain('OpenAI');
    expect(privacy).toContain('fiktivní jméno a univerzum');
    expect(privacy).not.toMatch(/konverzac|kufr|památk|příběh/i);
    expect(support).toContain('Pozvánka a přihlášení');
    expect(support).not.toMatch(/konverzac|kufr|památk|příběh/i);
  });

  it('restores full disclosures only after the server allows access', () => {
    launchAllowed.value = true;
    expect(mount(PrivacyView, { global: { stubs: ['RouterLink'] } }).text()).toContain('Rozhovory a příběhy');
    expect(mount(SupportView, { global: { stubs: ['RouterLink'] } }).text()).toContain('Připojení');
  });
});
