import { describe, expect, it } from 'vitest';
import { defineComponent, h, watch } from 'vue';
import { createMemoryHistory, createRouter, RouterView, useRoute } from 'vue-router';
import { flushPromises, mount } from '@vue/test-utils';
import { androidNavigationKey, captureAndroidAppLink } from '../src/platform/androidAppLinks';
import { consumeGuestClaim } from '../src/application/guestClaim';

describe('Android claim handoff through real Vue Router', () => {
  it('consumes each claim once on different paths sharing a component and on the same warm path', async () => {
    const consumed: { path: string; claim: string | null }[] = [];
    delete window.__dildovanderGuestClaim;
    const Keepsake = defineComponent({ setup() {
      const route = useRoute();
      const claim = consumeGuestClaim();
      consumed.push({ path: route.path, claim });
      // The real keepsake resets local claim state on a changed fullPath.
      watch(() => route.fullPath, () => {});
      return () => h('p', claim ?? 'empty');
    } });
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/keepsake/:id', component: Keepsake, name: 'keepsake' },
    ] });
    await router.push('/keepsake/old'); await router.isReady();
    const app = mount(defineComponent({ setup: () => () => h(RouterView, {}, {
      default: ({ Component, route }: { Component: object; route: { fullPath: string } }) =>
        h(Component, { key: androidNavigationKey(route.fullPath) }),
    }) }), { global: { plugins: [router] } });
    for (const [path, claim] of [['/keepsake/new', 'new-claim'], ['/keepsake/new', 'replacement-claim']]) {
      const destination = captureAndroidAppLink('https://dildovander.de' + path + '#' + claim)!;
      await router.replace(destination); await flushPromises();
      expect(app.text()).toBe(claim);
    }
    expect(consumed).toEqual([
      { path: '/keepsake/old', claim: null },
      { path: '/keepsake/new', claim: 'new-claim' },
      { path: '/keepsake/new', claim: 'replacement-claim' },
    ]);
    expect(window.__dildovanderGuestClaim).toBeUndefined();
    expect(router.currentRoute.value.hash).toBe('');
    app.unmount();
  });
});
