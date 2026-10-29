// Older generated responses can contain serialized missing values as text.
export function characterWelcomeText(value: unknown): string {
  if (typeof value !== 'string') return '';
  const text = value.trim();
  return /^(null|undefined)$/i.test(text) ? '' : text;
}
