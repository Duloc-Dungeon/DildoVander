import { computed, ref } from 'vue';

export interface ApplicationVersionPolicy {
  minimumBuild: string;
  latestBuild: string;
  updateUrl: string;
}
export interface InstalledApplication { version: string; build: string }
interface UpdateDependencies {
  platform: 'ios' | 'android' | 'web';
  getInfo(): Promise<InstalledApplication>;
  loadPolicy(signal: AbortSignal): Promise<unknown>;
  storage?: Pick<Storage, 'getItem' | 'setItem'>;
}

export function parseApplicationBuild(value: unknown, platform: 'ios' | 'android' = 'ios'): number[] | null {
  if (typeof value !== 'string' || /\s/.test(value) || !/^\d{1,10}(?:\.\d{1,10}){0,2}$/.test(value)) return null;
  const parts = value.split('.').map(Number);
  const maximum = platform === 'android' ? 2100000000 : 2147483647;
  if (parts.some(part => part > maximum) || platform === 'android' && parts.length !== 1) return null;
  return parts;
}

export function compareApplicationBuilds(left: number[], right: number[]): number {
  for (let index = 0; index < Math.max(left.length, right.length); index++) {
    const difference = (left[index] ?? 0) - (right[index] ?? 0);
    if (difference !== 0) return Math.sign(difference);
  }
  return 0;
}

export function parseVersionPolicy(value: unknown, platform: 'ios' | 'android' = 'ios'): ApplicationVersionPolicy | null {
  if (!value || typeof value !== 'object') return null;
  const { minimumBuild, latestBuild, updateUrl } = value as ApplicationVersionPolicy;
  const minimum = parseApplicationBuild(minimumBuild, platform);
  const latest = parseApplicationBuild(latestBuild, platform);
  if (!minimum || !latest || compareApplicationBuilds(latest, minimum) < 0 || typeof updateUrl !== 'string') return null;
  if (!updateUrl && latest.every(part => part === 0)) return { minimumBuild, latestBuild, updateUrl };
  try {
    const url = new URL(updateUrl);
    if (url.protocol !== 'https:' || url.username || url.password) return null;
  } catch { return null; }
  return { minimumBuild, latestBuild, updateUrl };
}

export function createApplicationUpdates(dependencies: UpdateDependencies) {
  const native = dependencies.platform !== 'web';
  const platform = dependencies.platform === 'android' ? 'android' : 'ios';
  const installed = ref<InstalledApplication | null>(null);
  const policy = ref<ApplicationVersionPolicy | null>(null);
  const checking = ref(native);
  const initialCheckComplete = ref(!native);
  const error = ref('');
  const storageKey = `wns.application-version.${dependencies.platform}.v1`;
  let pending: Promise<void> | undefined;
  if (native) {
    try { policy.value = parseVersionPolicy(JSON.parse(dependencies.storage?.getItem(storageKey) ?? 'null'), platform); }
    catch { /* Storage can be unavailable; the live policy remains authoritative. */ }
  }
  const installedBuild = computed(() => {
    const parts = parseApplicationBuild(installed.value?.build, platform);
    return parts?.some(part => part > 0) ? parts : null;
  });
  const required = computed(() => {
    const minimum = policy.value && parseApplicationBuild(policy.value.minimumBuild, platform);
    return native && !!minimum && minimum.some(part => part > 0)
      && (installedBuild.value === null || compareApplicationBuilds(installedBuild.value, minimum) < 0);
  });
  const blocked = computed(() => native && (!initialCheckComplete.value || installedBuild.value === null || required.value));
  const available = computed(() => {
    const latest = policy.value && parseApplicationBuild(policy.value.latestBuild, platform);
    return installedBuild.value !== null && !!latest && compareApplicationBuilds(installedBuild.value, latest) < 0;
  });

  async function check(): Promise<void> {
    if (!native) return;
    if (pending) return pending;
    pending = refresh();
    try { await pending; } finally { pending = undefined; }
  }
  async function refresh(): Promise<void> {
    checking.value = true;
    error.value = '';
    const abort = new AbortController();
    let timeout: ReturnType<typeof setTimeout> | undefined;
    // Bound even an unavailable native plugin, not only the HTTP transport.
    const deadline = new Promise<never>((_, reject) => {
      timeout = setTimeout(() => { abort.abort(); reject(new Error('version_check_timeout')); }, 8000);
    });
    try {
      const info = await Promise.race([dependencies.getInfo(), deadline]);
      installed.value = info;
      if (installedBuild.value === null) throw new Error('invalid_installed_build');
      const response = await Promise.race([dependencies.loadPolicy(abort.signal), deadline]);
      const selected = response && typeof response === 'object'
        ? (response as Record<string, unknown>)[dependencies.platform] : null;
      const next = parseVersionPolicy(selected, platform);
      if (!next) throw new Error('invalid_version_policy');
      policy.value = next;
      try { dependencies.storage?.setItem(storageKey, JSON.stringify(next)); }
      catch { /* A storage failure must not discard a mandatory live policy. */ }
    } catch {
      error.value = installedBuild.value === null
        ? 'Verzi aplikace se nepodařilo zjistit. Zkuste kontrolu znovu.'
        : 'Aktualizace se nepodařilo ověřit. Zkontrolujte připojení a zkuste to znovu.';
      // Never clear a known minimum on a timeout, invalid response or offline resume.
    } finally {
      clearTimeout(timeout);
      initialCheckComplete.value = true;
      checking.value = false;
    }
  }
  return { installed, policy, checking, error, required, blocked, available, check };
}

export type ApplicationUpdates = ReturnType<typeof createApplicationUpdates>;
