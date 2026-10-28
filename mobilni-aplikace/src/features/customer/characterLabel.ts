export function characterDisplayName(name: string, label?: string | null): string {
  return label ? `${name} · ${label}` : name;
}
