import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const api = vi.hoisted(() => ({ request: vi.fn(), transport: vi.fn(), reauthenticate: vi.fn(), setAccount: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string; roles: string[] } | null>({ id: 'admin', roles: ['Administrator'] });
  api.setAccount.mockImplementation(value => { account.value = value; });
  return { account, accountRequest: api.request, accountTransport: api.transport, authenticatePasskey: api.reauthenticate };
});
import CharacterAdminView from './CharacterAdminView.vue';
import type { AdminCharacter } from './characterAdministration';

const character = (): AdminCharacter => ({
  profileId: '12345678123442348234123456789abc', revision: 2,
  userInput: { name: 'Luna', world: 'Harry Potter', version: null, description: 'Pozoruje svět.', language: 'cs' },
  status: 'ready', enrichment: { summary: 'Připravený profil' }, errorCode: null, updatedAt: 1792540800, portrait: null,
});
const views: ReturnType<typeof mount>[] = [];
const originalConfirm = Object.getOwnPropertyDescriptor(window, 'confirm');
const createView = () => {
  const view = mount(CharacterAdminView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  views.push(view);
  return view;
};
const button = (view: ReturnType<typeof mount>, text: string) => view.findAll('button').find(item => item.text() === text)!;
async function select(view: ReturnType<typeof mount>) {
  await flushPromises();
  await view.get('.character-admin__list button').trigger('click');
  await flushPromises();
}
async function pick(view: ReturnType<typeof mount>, file: File) {
  const input = view.get<HTMLInputElement>('#admin-character-image');
  Object.defineProperty(input.element, 'files', { configurable: true, value: [file] });
  await input.trigger('change');
}

beforeEach(() => {
  vi.clearAllMocks();
  api.setAccount({ id: 'admin', roles: ['Administrator'] });
  Object.defineProperty(window, 'confirm', { configurable: true, value: vi.fn(() => true) });
  vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:private-portrait');
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
  api.request.mockImplementation(async (path: string) => path === '/app/v1/admin/characters' ? { items: [character()] } : character());
  api.reauthenticate.mockResolvedValue(undefined);
  api.transport.mockResolvedValue(new Response(new Blob(['image'], { type: 'image/webp' }), { headers: { 'Content-Type': 'image/webp' } }));
});
afterEach(() => {
  for (const view of views.splice(0)) view.unmount();
  vi.restoreAllMocks();
  if (originalConfirm) Object.defineProperty(window, 'confirm', originalConfirm);
  else Reflect.deleteProperty(window, 'confirm');
});

describe('správa postav', () => {
  it('loads the next compact page without losing the current draft', async () => {
    api.request.mockResolvedValueOnce({ items: [character()], nextCursor: character().profileId });
    const view = createView();
    await select(view);
    await view.get('#admin-character-name').setValue('Rozepsaná Luna');
    api.request.mockResolvedValueOnce({ items: [{ ...character(), profileId: 'abcdef0123456789abcdef0123456789', userInput: { name: 'Robin', world: 'Fiction' } }], nextCursor: null });
    await button(view, 'Načíst další postavy').trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith(`/app/v1/admin/characters?after=${character().profileId}`, 'GET', undefined, expect.any(AbortSignal));
    expect(view.findAll('.character-admin__list button')).toHaveLength(2);
    expect(view.get<HTMLInputElement>('#admin-character-name').element.value).toBe('Rozepsaná Luna');
    expect(view.text()).not.toContain('Načíst další postavy');
  });

  it('filters characters and submits a revision-checked manual edit without AI flags', async () => {
    const view = createView();
    await select(view);
    await view.get('#character-search').setValue('neexistuje');
    expect(view.text()).toContain('Hledání neodpovídá žádná postava.');
    await view.get('#character-search').setValue('potter');
    expect(view.findAll('.character-admin__list button')).toHaveLength(1);
    await view.get('#admin-character-name').setValue('Luna Lovegoodová');
    api.request.mockResolvedValueOnce({ ...character(), revision: 3, userInput: { ...character().userInput, name: 'Luna Lovegoodová' } });
    await view.get('form').trigger('submit');
    await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith(expect.stringContaining(character().profileId), 'PUT', {
      expectedRevision: 2, userInput: { ...character().userInput, name: 'Luna Lovegoodová' },
    }, expect.any(AbortSignal), expect.any(String));
    expect(view.text()).toContain('Údaje postavy byly uloženy.');
    expect(button(view, 'Uložit údaje').attributes('disabled')).toBeDefined();
  });

  it('preserves a conflicted draft and requires reloading before another mutation', async () => {
    const view = createView();
    await select(view);
    await view.get('#admin-character-description').setValue('Moje úprava');
    api.request.mockRejectedValueOnce({ code: 'character_revision_conflict', status: 409 });
    await view.get('form').trigger('submit');
    await flushPromises();
    expect(view.text()).toContain('Postavu mezitím někdo změnil');
    expect(view.get<HTMLTextAreaElement>('#admin-character-description').element.value).toBe('Moje úprava');
    expect(view.get('fieldset').attributes('disabled')).toBeDefined();
    api.request.mockResolvedValueOnce({ ...character(), revision: 5 });
    await button(view, 'Načíst aktuální verzi').trigger('click');
    await flushPromises();
    expect(view.text()).toContain('revize 5');
    expect(view.get('fieldset').attributes('disabled')).toBeUndefined();
  });

  it('keeps the draft through reauthentication and does not replay the write', async () => {
    const view = createView();
    await select(view);
    await view.get('#admin-character-name').setValue('Luna II');
    api.request.mockRejectedValueOnce({ code: 'recent_passkey_required' });
    await view.get('form').trigger('submit');
    await flushPromises();
    await button(view, 'Ověřit totožnost přístupovým klíčem').trigger('click');
    await flushPromises();
    expect(api.reauthenticate).toHaveBeenCalledWith('reauth');
    expect(view.get<HTMLInputElement>('#admin-character-name').element.value).toBe('Luna II');
    expect(api.request.mock.calls.filter(([, method]) => method === 'PUT')).toHaveLength(1);
  });

  it('validates selection and uploads the raw replacement against the selected revision', async () => {
    const view = createView();
    await select(view);
    await pick(view, new File(['bad'], 'bad.svg', { type: 'image/svg+xml' }));
    expect(view.text()).toContain('Vyberte neprázdný obrázek PNG');
    expect(view.find('img[alt="Náhled náhradního obrázku"]').exists()).toBe(false);
    const file = new File(['webp'], 'replacement.webp', { type: 'image/webp' });
    await pick(view, file);
    expect(view.find('img[alt="Náhled náhradního obrázku"]').exists()).toBe(true);
    const saved = { ...character(), revision: 3, portrait: { status: 'ready', hasImage: true, errorCode: null, updatedAt: 1792540800 } };
    api.transport.mockResolvedValueOnce(new Response(JSON.stringify(saved), { headers: { 'Content-Type': 'application/json' } }));
    await button(view, 'Nahrát náhradní obrázek').trigger('click');
    await flushPromises();
    expect(api.transport).toHaveBeenCalledWith(`/app/v1/admin/characters/${character().profileId}/portrait?expectedRevision=2`, expect.objectContaining({
      method: 'PUT', body: file, headers: { 'Content-Type': 'image/webp', 'X-DV-Confirm': 'yes' },
    }));
    expect(api.transport).toHaveBeenLastCalledWith(expect.stringContaining('/portrait?revision=3'), expect.anything());
    expect(view.text()).toContain('Náhradní obrázek byl uložen.');
  });

  it('clears private data and ignores late details after the administrator account changes', async () => {
    const view = createView();
    await flushPromises();
    let finish!: (value: AdminCharacter) => void;
    api.request.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    await view.get('.character-admin__list button').trigger('click');
    const detailSignal = api.request.mock.calls.at(-1)![3] as AbortSignal;
    api.setAccount({ id: 'operator', roles: ['Operator'] });
    await flushPromises();
    finish(character());
    await flushPromises();
    expect(detailSignal.aborted).toBe(true);
    expect(view.text()).not.toContain('Luna');
    expect(view.find('#admin-character-name').exists()).toBe(false);
    expect(view.text()).toContain('Správa postav je dostupná vlastníkovi a správci.');
  });
});
