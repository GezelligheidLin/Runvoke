type ClassValue = string | false | null | undefined | ClassValue[] | Record<string, boolean | undefined | null>

function normalizeClass(value: ClassValue): string[] {
  if (typeof value === 'string')
    return [value]
  if (Array.isArray(value))
    return value.flatMap(normalizeClass)
  if (value && typeof value === 'object')
    return Object.entries(value).flatMap(([name, enabled]) => enabled ? [name] : [])
  return []
}

export function cn(...classes: ClassValue[]) {
  return classes.flatMap(normalizeClass).join(' ')
}
