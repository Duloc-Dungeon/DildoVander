import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { focusRouteContent } from './routeFocus';

let content: HTMLElement;
let navigation: HTMLElement;
beforeEach(() => {
  document.body.innerHTML = '<a id="navigation" href="/demo">Ukázka</a><main id="content" tabindex="-1"><button id="next">Další krok</button></main>';
  content = document.getElementById('content')!;
  navigation = document.getElementById('navigation')!;
});
afterEach(() => { document.body.innerHTML = ''; });

describe('route announcement focus', () => {
  it('moves focus from navigation into the new route content', () => {
    navigation.focus();
    focusRouteContent(content);
    expect(document.activeElement).toBe(content);
  });

  it('preserves a control focused after navigation but before the queued announcement', () => {
    const announceRoute = () => focusRouteContent(content);
    const next = document.getElementById('next')!;
    navigation.focus();
    // A keyboard user can enter the new route before its animation-frame callback.
    next.focus();
    announceRoute();
    expect(document.activeElement).toBe(next);
  });

  it('leaves focus intact when a required-update gate has removed the route content', () => {
    navigation.focus();
    focusRouteContent(undefined);
    expect(document.activeElement).toBe(navigation);
  });
});
