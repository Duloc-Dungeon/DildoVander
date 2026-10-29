export const launchTimeZone = 'Europe/Prague';

function localParts(timestamp: number): number[] {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: launchTimeZone, year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
  }).formatToParts(timestamp);
  return ['year', 'month', 'day', 'hour', 'minute', 'second']
    .map(type => Number(parts.find(part => part.type === type)?.value ?? 0));
}

export function formatLaunchDate(timestamp: number | null): string {
  if (timestamp == null) return '';
  const [year, month, day, hour, minute] = localParts(timestamp);
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${year}-${pad(month)}-${pad(day)}T${pad(hour)}:${pad(minute)}`;
}

export function parseLaunchDate(value: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const desired = match.slice(1).map(Number);
  const naive = Date.UTC(desired[0], desired[1] - 1, desired[2], desired[3], desired[4]);
  let candidate = naive;
  for (let attempt = 0; attempt < 3; attempt++) {
    const p = localParts(candidate);
    const represented = Date.UTC(p[0], p[1] - 1, p[2], p[3], p[4], p[5]);
    candidate += naive - represented;
  }
  // Round-trip validation rejects nonexistent local times at the spring DST change.
  return localParts(candidate).slice(0, 5).every((part, index) => part === desired[index]) ? candidate : null;
}
