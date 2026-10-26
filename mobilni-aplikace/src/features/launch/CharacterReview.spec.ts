import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, expect, it, vi } from 'vitest';
import CharacterReview from './CharacterReview.vue';
const api = vi.hoisted(() => ({ request: vi.fn(), setAccount: vi.fn() }));
vi.mock('../identity/session', async () => {
 const { ref } = await import('vue'); const account = ref({ id: 'one', roles: ['Customer'] });
 api.setAccount.mockImplementation(id => { account.value = { id, roles: ['Customer'] }; });
 return { account, accountRequest: api.request };
});
const makeProfile = (revision = 1) => ({ profileId: 'p', revision, status: 'ready', userInput: { name: 'Raven', world: 'Fiction' }, enrichment: { recognizedName: 'Found Raven', world: 'Fiction', summary: 'Found summary', facts: [{ text: 'Found fact' }], conversationIdeas: ['Found idea'], ambiguities: [], sources: [] } });
beforeEach(() => { api.request.mockReset(); api.setAccount('one'); api.request.mockResolvedValue({ profile: makeProfile() }); });
it('shows findings and saves edits as actual manual data without AI consent', async () => {
 const view = mount(CharacterReview, { props: { profileId: 'p', revision: 1, status: 'ready' } });
 await flushPromises(); expect(view.text()).toContain('Found fact');
 await view.get('button').trigger('click');
 await view.get('#findings-summary').setValue('My correction');
 api.request.mockResolvedValue({ profile: makeProfile(2) });
 await view.get('form').trigger('submit'); await flushPromises();
 expect(api.request).toHaveBeenLastCalledWith('/app/v1/prelaunch/character/review', 'PUT', expect.objectContaining({ expectedRevision: 1, regenerate: false, openAiConsent: false, findings: expect.objectContaining({ summary: 'My correction' }) }), expect.any(AbortSignal));
 expect(view.emitted('saved')).toHaveLength(1); view.unmount();
});
it('regenerates with revised input only after explicit consent', async () => {
 const view = mount(CharacterReview, { props: { profileId: 'p', revision: 1, status: 'ready' } });
 await flushPromises(); await view.get('button').trigger('click');
 await view.get('#review-description').setValue('Different period');
 const regenerate = view.findAll('button').at(-1)!;
 expect(regenerate.attributes('disabled')).toBeDefined();
 await view.get('input[type="checkbox"]').setValue(true);
 api.request.mockResolvedValue({ profile: makeProfile(2) });
 await regenerate.trigger('click'); await flushPromises();
 expect(api.request).toHaveBeenLastCalledWith('/app/v1/prelaunch/character/review', 'PUT', expect.objectContaining({ regenerate: true, openAiConsent: true, userInput: expect.objectContaining({ description: 'Different period' }), findings: undefined }), expect.any(AbortSignal));
 view.unmount();
});
it('keeps drafts on failure and on status refresh, displays preparation errors', async () => {
 const view = mount(CharacterReview, { props: { profileId: 'p', revision: 1, status: 'running' } });
 await flushPromises(); await view.get('button').trigger('click');
 await view.get('#findings-summary').setValue('Unsaved correction');
 api.request.mockRejectedValueOnce(new Error('offline'));
 await view.get('form').trigger('submit'); await flushPromises();
 expect(view.get('[role="alert"]').text()).toContain('Uložení se nepodařilo potvrdit');
 expect((view.get('#findings-summary').element as HTMLTextAreaElement).value).toBe('Unsaved correction');
 api.request.mockResolvedValue({ profile: { ...makeProfile(), status: 'failed', errorCode: 'character_provider_error' } });
 await view.setProps({ status: 'failed' }); await flushPromises();
 expect(view.text()).toContain('character_provider_error');
 expect((view.get('#findings-summary').element as HTMLTextAreaElement).value).toBe('Unsaved correction');
 view.unmount();
});
it('discards late reads and private drafts on account switch', async () => {
 let resolve!: (value: unknown) => void;
 api.request.mockImplementationOnce(() => new Promise(r => { resolve = r; }));
 const view = mount(CharacterReview, { props: { profileId: 'p', revision: 1, status: 'ready' } });
 api.setAccount('two'); await flushPromises();
 resolve({ profile: { ...makeProfile(), enrichment: { ...makeProfile().enrichment, summary: 'OLD PRIVATE DATA' } } });
 await flushPromises(); expect(view.text()).not.toContain('OLD PRIVATE DATA'); view.unmount();
});

it('recovers a lost save response against a newer server revision without discarding the draft', async () => {
 const view = mount(CharacterReview, { props: { profileId: 'p', revision: 1, status: 'ready' } });
 await flushPromises(); await view.get('button').trigger('click');
 await view.get('#findings-summary').setValue('Keep my correction');
 api.request.mockRejectedValueOnce(new Error('lost response'));
 await view.get('form').trigger('submit'); await flushPromises();
 api.request.mockResolvedValue({ profile: makeProfile(2) });
 await view.get('[role="alert"] button').trigger('click'); await flushPromises();
 expect(view.emitted('saved')).toHaveLength(1);
 expect((view.get('#findings-summary').element as HTMLTextAreaElement).value).toBe('Keep my correction');
 await view.get('form').trigger('submit'); await flushPromises();
 expect(api.request).toHaveBeenLastCalledWith(expect.any(String), 'PUT', expect.objectContaining({ expectedRevision: 2, findings: expect.objectContaining({ summary: 'Keep my correction' }) }), expect.any(AbortSignal));
 view.unmount();
});
