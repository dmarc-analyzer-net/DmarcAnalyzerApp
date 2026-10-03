import { describe, expect, it } from 'vitest'

import { effectiveDigestModes, summarizeDigestModes } from './analytics'

describe('digest routing helpers', () => {
  it('fills clients without an override from the default', () => {
    const modes = effectiveDigestModes(
      { digestDefaultMode: 'rollup', clientModes: [{ clientId: 'b', mode: 'separate' }] },
      ['a', 'b', 'c'],
    )
    expect(modes).toEqual({ a: 'rollup', b: 'separate', c: 'rollup' })
  })

  it('treats every unlisted client as off for a chosen-clients recipient', () => {
    const modes = effectiveDigestModes(
      { digestDefaultMode: 'off', clientModes: [{ clientId: 'a', mode: 'rollup' }] },
      ['a', 'b'],
    )
    expect(modes).toEqual({ a: 'rollup', b: 'off' })
  })

  it('summarizes only the modes in use', () => {
    expect(summarizeDigestModes({ a: 'rollup', b: 'rollup', c: 'off' })).toBe('2 in roll-up, 1 off')
    expect(summarizeDigestModes({})).toBe('No clients')
  })
})
