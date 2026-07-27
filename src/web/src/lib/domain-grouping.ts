import type { DomainAnalytics } from '@/lib/analytics'

/**
 * Grouping for the Domains list: subdomains that share a parent are shown together.
 *
 * Lives here rather than in the page because it is the only non-obvious logic on that screen
 * and it is pure — given rows in their sorted order it returns the display order.
 */

export type GroupableRow = Pick<DomainAnalytics, 'domainId' | 'name'>

/** The name one label up: `booking.example.dk` -> `example.dk`. Null for an apex name. */
export function parentOf(name: string): string | null {
  const dot = name.indexOf('.')
  if (dot < 0) return null
  const parent = name.slice(dot + 1)
  // Two labels left means we are already at the apex, so there is no parent to group under.
  return parent.split('.').length >= 2 ? parent : null
}

export type ListItem<T extends GroupableRow> =
  | { kind: 'row'; row: T }
  | { kind: 'group'; parent: string; header: T | null; members: T[] }

/**
 * Collects subdomains that share a parent into groups, and leaves everything else alone.
 *
 * Only where two or more monitored domains share a parent. A lone subdomain under its own
 * heading would add a row and say nothing — on the instance this was built against that would
 * have meant 36 single-child headings against 3 real groups.
 *
 * The parent is frequently *not* a monitored domain, which is the case that matters:
 * `booking.` and `nyheder.smalldanishhotels.dk` are both monitored while
 * `smalldanishhotels.dk` is not, because reports only ever arrive for the sending subdomain.
 * Such a heading is a label, not a row — it has no metrics and nothing to click.
 *
 * Sort order is preserved rather than replaced: a group appears where its first member landed
 * after sorting, so worst-compliance-first still puts the worst group first, and the members
 * keep the order the comparator gave them.
 */
export function groupBySharedParent<T extends GroupableRow>(sorted: T[]): ListItem<T>[] {
  const byParent = new Map<string, T[]>()
  for (const row of sorted) {
    const parent = parentOf(row.name)
    if (!parent) continue
    const bucket = byParent.get(parent)
    if (bucket) bucket.push(row)
    else byParent.set(parent, [row])
  }

  const grouped = new Set<string>()
  const headerFor = new Map<string, T>()
  for (const [parent, members] of byParent) {
    if (members.length < 2) continue
    for (const m of members) grouped.add(m.domainId)
    // When the parent is monitored too, its own row becomes the heading instead of sitting
    // separately, so it is not listed twice.
    const monitored = sorted.find((r) => r.name === parent)
    if (monitored) {
      grouped.add(monitored.domainId)
      headerFor.set(parent, monitored)
    }
  }

  const emitted = new Set<string>()
  const items: ListItem<T>[] = []
  for (const row of sorted) {
    if (!grouped.has(row.domainId)) {
      items.push({ kind: 'row', row })
      continue
    }

    const parent = headerFor.get(row.name) === row ? row.name : parentOf(row.name)
    if (!parent || emitted.has(parent)) continue

    emitted.add(parent)
    items.push({
      kind: 'group',
      parent,
      header: headerFor.get(parent) ?? null,
      members: byParent.get(parent) ?? [],
    })
  }

  return items
}
