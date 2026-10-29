import { mount } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import ApplicationHeaderActions from './ApplicationHeaderActions.vue';
import { applicationHeaderActionsKey } from './applicationHeader';

afterEach(() => { document.body.innerHTML = ''; });

describe('screen actions in the shared header', () => {
  it('keeps controls inline when the screen is rendered without the application shell', () => {
    const wrapper = mount(ApplicationHeaderActions, { slots: { default: '<button>Invite</button>' } });
    expect(wrapper.get('button').text()).toBe('Invite');
    wrapper.unmount();
  });

  it('moves controls into the shell and removes them when the screen leaves', () => {
    document.body.innerHTML = '<div id="header-actions"></div>';
    const wrapper = mount(ApplicationHeaderActions, {
      slots: { default: '<button>Invite</button>' },
      global: { provide: { [applicationHeaderActionsKey as symbol]: '#header-actions' } },
    });
    expect(document.querySelector('#header-actions button')?.textContent).toBe('Invite');
    wrapper.unmount();
    expect(document.querySelector('#header-actions')?.childElementCount).toBe(0);
  });
});
