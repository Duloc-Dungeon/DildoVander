import { mount } from '@vue/test-utils';
import { expect, it } from 'vitest';
import CharacterCountdown from '../../components/scene/CharacterCountdown.vue';

it('renders four zero-padded columns without announcing every tick', () => {
  const view = mount(CharacterCountdown, { props: {
    name: 'Mira Nocturne', universe: 'Za zrcadlem', values: [0, 2, 14, 8],
    units: ['dní', 'hodin', 'minut', 'sekund'], title: 'Do otevření', label: 'Čas do otevření',
  } });

  expect(view.attributes('aria-label')).toBe('Čas do otevření');
  expect(view.findAll('.character-countdown-grid > div')).toHaveLength(4);
  expect(view.findAll('.character-countdown-grid strong').map(cell => cell.text())).toEqual(['00', '02', '14', '08']);
  expect(view.find('.character-countdown-grid').attributes('aria-live')).toBe('off');
});

it('keeps the real schedule explanation available when no opening time is set', () => {
  const view = mount(CharacterCountdown, { props: {
    name: 'A long character name that wraps safely', universe: 'Za zrcadlem', values: [0, 0, 0, 0],
    units: ['dní', 'hodin', 'minut', 'sekund'], title: 'Do otevření', label: 'Čas do otevření', unscheduled: 'Čas otevření zatím není stanovený.',
  } });

  expect(view.text()).toContain('A long character name that wraps safely');
  expect(view.text()).toContain('Čas otevření zatím není stanovený.');
  expect(view.find('.character-countdown-grid').exists()).toBe(false);
});
