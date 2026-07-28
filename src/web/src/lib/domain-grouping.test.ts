import { describe, expect, it } from 'vitest'

import { groupBySharedParent, parentOf } from '@/lib/domain-grouping'

/** Only the two fields the grouping reads, so the fixtures stay readable. */
const rows = (...names: string[]) => names.map((name, i) => ({ domainId: `id-${i}`, name }))

describe('parentOf', () => {
  it('drops one label', () => {
    expect(parentOf('booking.example.dk')).toBe('example.dk')
    expect(parentOf('smtp.eu.mail.example.dk')).toBe('eu.mail.example.dk')
  })

  it('returns null at the apex, where there is nothing to group under', () => {
    expect(parentOf('example.dk')).toBeNull()
    expect(parentOf('example')).toBeNull()
  })
})

describe('groupBySharedParent', () => {
  it('leaves a lone subdomain flat', () => {
    // A heading over a single child adds a row and says nothing. On the instance this
    // was built against, grouping everything would have meant 36 of them.
    const items = groupBySharedParent(rows('email.krifa.dk', 'krifa.example'))

    expect(items.map((i) => i.kind)).toEqual(['row', 'row'])
  })

  it('groups two subdomains that share a parent', () => {
    const items = groupBySharedParent(
      rows('booking.smalldanishhotels.dk', 'nyheder.smalldanishhotels.dk'),
    )

    expect(items).toHaveLength(1)
    const group = items[0]
    expect(group.kind).toBe('group')
    if (group.kind !== 'group') return
    expect(group.parent).toBe('smalldanishhotels.dk')
    expect(group.members.map((m) => m.name)).toEqual([
      'booking.smalldanishhotels.dk',
      'nyheder.smalldanishhotels.dk',
    ])
  })

  it('has no header row when the parent is not a monitored domain', () => {
    // The common case: reports only ever arrive for the sending subdomain, so the
    // organisational domain was never added. The heading is a label, not a row.
    const items = groupBySharedParent(rows('nyheder.fbg.dk', 'partners-nyheder.fbg.dk'))

    const group = items[0]
    expect(group.kind).toBe('group')
    if (group.kind !== 'group') return
    expect(group.header).toBeNull()
  })

  it("promotes a monitored parent's own row to the heading instead of listing it twice", () => {
    const items = groupBySharedParent(rows('yulsn.io', 'client.yulsn.io', 'gitlab.yulsn.io'))

    expect(items).toHaveLength(1)
    const group = items[0]
    expect(group.kind).toBe('group')
    if (group.kind !== 'group') return
    expect(group.header?.name).toBe('yulsn.io')
    expect(group.members.map((m) => m.name)).toEqual(['client.yulsn.io', 'gitlab.yulsn.io'])
  })

  it('renders every domain exactly once', () => {
    // The invariant that matters most: grouping must not drop or duplicate a row.
    const input = rows(
      'yulsn.io',
      'client.yulsn.io',
      'gitlab.yulsn.io',
      'booking.smalldanishhotels.dk',
      'nyheder.smalldanishhotels.dk',
      'email.krifa.dk',
      'apex.example',
    )

    const emitted = groupBySharedParent(input).flatMap((item) =>
      item.kind === 'row' ? [item.row] : [...(item.header ? [item.header] : []), ...item.members],
    )

    expect(emitted.map((r) => r.domainId).sort()).toEqual(input.map((r) => r.domainId).sort())
  })

  it('places a group where its first member fell in the sort, not at the end', () => {
    // Sort order is preserved rather than replaced, so worst-compliance-first still
    // puts the worst group first. Input order stands in for "already sorted".
    const items = groupBySharedParent(
      rows('aaa.example', 'client.yulsn.io', 'gitlab.yulsn.io', 'zzz.example'),
    )

    expect(items.map((i) => (i.kind === 'row' ? i.row.name : `group:${i.parent}`))).toEqual([
      'aaa.example',
      'group:yulsn.io',
      'zzz.example',
    ])
  })

  it('keeps members in the order they arrived', () => {
    const items = groupBySharedParent(rows('b.shared.example', 'a.shared.example'))

    const group = items[0]
    if (group.kind !== 'group') throw new Error('expected a group')
    expect(group.members.map((m) => m.name)).toEqual(['b.shared.example', 'a.shared.example'])
  })

  it('handles an empty list', () => {
    expect(groupBySharedParent([])).toEqual([])
  })
})
