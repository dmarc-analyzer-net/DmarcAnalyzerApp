import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type ViewKey = 'clients' | 'domains' | 'mailbox-sources'
type Mode = 'list' | 'create' | 'edit'

type Client = {
  id: string
  name: string
  slug: string
  isActive: boolean
  retentionMonths: number
  timezone: string
}

type Domain = {
  id: string
  name: string
  isActive: boolean
  clientId: string
  clientName: string | null
}

type MailboxSource = {
  id: string
  name: string
  protocol: 'imap' | 'pop3'
  host: string
  port: number
  useTls: boolean
  username: string
  defaultClientId: string
  defaultClientName: string | null
  isActive: boolean
}

const views: Array<{ key: ViewKey; label: string }> = [
  { key: 'clients', label: 'Clients' },
  { key: 'domains', label: 'Domains' },
  { key: 'mailbox-sources', label: 'Mailbox Sources' },
]

const initialClientForm = {
  name: '',
  slug: '',
  timezone: 'UTC',
  retentionMonths: 27,
  isActive: true,
}

const initialDomainForm = {
  name: '',
  clientId: '',
  isActive: true,
}

const initialMailboxForm = {
  name: '',
  protocol: 'imap' as 'imap' | 'pop3',
  host: '',
  port: 993,
  useTls: true,
  username: '',
  password: '',
  defaultClientId: '',
  isActive: true,
}

function App() {
  const [view, setView] = useState<ViewKey>('clients')
  const [mode, setMode] = useState<Mode>('list')
  const [status, setStatus] = useState('Loading API status...')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [clients, setClients] = useState<Client[]>([])
  const [domains, setDomains] = useState<Domain[]>([])
  const [mailboxSources, setMailboxSources] = useState<MailboxSource[]>([])

  const [editingClientId, setEditingClientId] = useState<string | null>(null)
  const [editingDomainId, setEditingDomainId] = useState<string | null>(null)
  const [editingMailboxId, setEditingMailboxId] = useState<string | null>(null)

  const [clientForm, setClientForm] = useState(initialClientForm)
  const [domainForm, setDomainForm] = useState(initialDomainForm)
  const [mailboxForm, setMailboxForm] = useState(initialMailboxForm)

  const sortedClients = useMemo(
    () => [...clients].sort((a, b) => a.name.localeCompare(b.name)),
    [clients],
  )

  const selectedClient = useMemo(
    () => clients.find((x) => x.id === editingClientId) ?? null,
    [clients, editingClientId],
  )
  const selectedDomain = useMemo(
    () => domains.find((x) => x.id === editingDomainId) ?? null,
    [domains, editingDomainId],
  )
  const selectedMailbox = useMemo(
    () => mailboxSources.find((x) => x.id === editingMailboxId) ?? null,
    [mailboxSources, editingMailboxId],
  )

  const fetchJson = async <T,>(url: string, init?: RequestInit): Promise<T> => {
    const response = await fetch(url, init)
    if (!response.ok) {
      let message = `Request failed (${response.status})`
      try {
        const payload = (await response.json()) as { error?: string }
        if (payload.error) {
          message = payload.error
        }
      } catch {
        // ignore
      }
      throw new Error(message)
    }
    return (await response.json()) as T
  }

  const openCreate = () => {
    setMode('create')
    setError(null)
    setEditingClientId(null)
    setEditingDomainId(null)
    setEditingMailboxId(null)

    if (view === 'clients') {
      setClientForm(initialClientForm)
      return
    }

    if (view === 'domains') {
      setDomainForm((x) => ({
        ...initialDomainForm,
        clientId: x.clientId || sortedClients[0]?.id || '',
      }))
      return
    }

    setMailboxForm((x) => ({
      ...initialMailboxForm,
      defaultClientId: x.defaultClientId || sortedClients[0]?.id || '',
    }))
  }

  const closePanel = () => {
    setMode('list')
    setError(null)
    setEditingClientId(null)
    setEditingDomainId(null)
    setEditingMailboxId(null)
  }

  const loadData = async () => {
    setBusy(true)
    setError(null)
    try {
      const [clientData, domainData, mailboxData] = await Promise.all([
        fetchJson<Client[]>('/api/v1/clients'),
        fetchJson<Domain[]>('/api/v1/domains'),
        fetchJson<MailboxSource[]>('/api/v1/mailbox-sources'),
      ])
      setClients(clientData)
      setDomains(domainData)
      setMailboxSources(mailboxData)

      if (!domainForm.clientId && clientData.length > 0) {
        setDomainForm((x) => ({ ...x, clientId: clientData[0].id }))
      }
      if (!mailboxForm.defaultClientId && clientData.length > 0) {
        setMailboxForm((x) => ({ ...x, defaultClientId: clientData[0].id }))
      }
    } catch (loadError) {
      setError(loadError instanceof Error ? loadError.message : 'Failed to load data')
    } finally {
      setBusy(false)
    }
  }

  useEffect(() => {
    const boot = async () => {
      try {
        const payload = await fetchJson<{ service: string; mode: string; timestampUtc: string }>(
          '/api/v1/system/status',
        )
        setStatus(`${payload.service} (${payload.mode}) at ${payload.timestampUtc}`)
      } catch (statusError) {
        setStatus(
          statusError instanceof Error ? `API unavailable: ${statusError.message}` : 'API unavailable',
        )
      }
      await loadData()
    }

    void boot()
  }, [])

  const handleCreateClient = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      await fetchJson('/api/v1/clients', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(clientForm),
      })
      closePanel()
      await loadData()
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : 'Failed to create client')
    }
  }

  const handleEditClientOpen = (client: Client) => {
    setMode('edit')
    setError(null)
    setEditingClientId(client.id)
    setClientForm({
      name: client.name,
      slug: client.slug,
      timezone: client.timezone,
      retentionMonths: client.retentionMonths,
      isActive: client.isActive,
    })
  }

  const handleUpdateClient = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!editingClientId) {
      return
    }
    setError(null)
    try {
      await fetchJson(`/api/v1/clients/${editingClientId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(clientForm),
      })
      closePanel()
      await loadData()
    } catch (updateError) {
      setError(updateError instanceof Error ? updateError.message : 'Failed to update client')
    }
  }

  const handleCreateDomain = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      await fetchJson('/api/v1/domains', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(domainForm),
      })
      closePanel()
      await loadData()
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : 'Failed to create domain')
    }
  }

  const handleEditDomainOpen = (domain: Domain) => {
    setMode('edit')
    setError(null)
    setEditingDomainId(domain.id)
    setDomainForm({
      name: domain.name,
      clientId: domain.clientId,
      isActive: domain.isActive,
    })
  }

  const handleUpdateDomain = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!editingDomainId) {
      return
    }
    setError(null)
    try {
      await fetchJson(`/api/v1/domains/${editingDomainId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(domainForm),
      })
      closePanel()
      await loadData()
    } catch (updateError) {
      setError(updateError instanceof Error ? updateError.message : 'Failed to update domain')
    }
  }

  const handleCreateMailbox = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setError(null)
    try {
      await fetchJson('/api/v1/mailbox-sources', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(mailboxForm),
      })
      closePanel()
      await loadData()
    } catch (createError) {
      setError(createError instanceof Error ? createError.message : 'Failed to create mailbox source')
    }
  }

  const handleEditMailboxOpen = (source: MailboxSource) => {
    setMode('edit')
    setError(null)
    setEditingMailboxId(source.id)
    setMailboxForm({
      name: source.name,
      protocol: source.protocol,
      host: source.host,
      port: source.port,
      useTls: source.useTls,
      username: source.username,
      password: '',
      defaultClientId: source.defaultClientId,
      isActive: source.isActive,
    })
  }

  const handleUpdateMailbox = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!editingMailboxId) {
      return
    }
    setError(null)
    try {
      const payload = { ...mailboxForm }
      if (!payload.password) {
        delete (payload as { password?: string }).password
      }

      await fetchJson(`/api/v1/mailbox-sources/${editingMailboxId}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      })
      closePanel()
      await loadData()
    } catch (updateError) {
      setError(updateError instanceof Error ? updateError.message : 'Failed to update mailbox source')
    }
  }

  const renderListSection = () => {
    if (view === 'clients') {
      return (
        <section className="panel list-panel">
          <div className="panel-head">
            <h3>Clients</h3>
            <span>{clients.length} total</span>
          </div>
          <ul className="item-list">
            {sortedClients.map((client) => (
              <li key={client.id}>
                <div>
                  <strong>{client.name}</strong>
                  <p>
                    {client.slug} | {client.timezone} | {client.retentionMonths} mo |{' '}
                    {client.isActive ? 'active' : 'inactive'}
                  </p>
                </div>
                <button type="button" onClick={() => handleEditClientOpen(client)}>
                  Edit
                </button>
              </li>
            ))}
          </ul>
        </section>
      )
    }

    if (view === 'domains') {
      return (
        <section className="panel list-panel">
          <div className="panel-head">
            <h3>Domains</h3>
            <span>{domains.length} total</span>
          </div>
          <ul className="item-list">
            {domains.map((domain) => (
              <li key={domain.id}>
                <div>
                  <strong>{domain.name}</strong>
                  <p>
                    {domain.clientName ?? domain.clientId} | {domain.isActive ? 'active' : 'inactive'}
                  </p>
                </div>
                <button type="button" onClick={() => handleEditDomainOpen(domain)}>
                  Edit
                </button>
              </li>
            ))}
          </ul>
        </section>
      )
    }

    return (
      <section className="panel list-panel">
        <div className="panel-head">
          <h3>Mailbox Sources</h3>
          <span>{mailboxSources.length} total</span>
        </div>
        <ul className="item-list">
          {mailboxSources.map((source) => (
            <li key={source.id}>
              <div>
                <strong>{source.name}</strong>
                <p>
                  {source.protocol.toUpperCase()} {source.host}:{source.port} |{' '}
                  {source.defaultClientName ?? source.defaultClientId} |{' '}
                  {source.isActive ? 'active' : 'inactive'}
                </p>
              </div>
              <button type="button" onClick={() => handleEditMailboxOpen(source)}>
                Edit
              </button>
            </li>
          ))}
        </ul>
      </section>
    )
  }

  const renderModalForm = () => {
    if (mode === 'list') {
      return null
    }

    if (view === 'clients') {
      return (
        <div className="modal-backdrop" role="presentation" onClick={closePanel}>
          <article className="modal-card" onClick={(e) => e.stopPropagation()}>
            <h3>{mode === 'create' ? 'Create Client' : `Edit ${selectedClient?.name ?? 'Client'}`}</h3>
            <form className="form-grid" onSubmit={mode === 'create' ? handleCreateClient : handleUpdateClient}>
              <input
                placeholder="Name"
                value={clientForm.name}
                onChange={(e) => setClientForm((x) => ({ ...x, name: e.target.value }))}
                required
              />
              <input
                placeholder="Slug"
                value={clientForm.slug}
                onChange={(e) => setClientForm((x) => ({ ...x, slug: e.target.value }))}
                required
              />
              <input
                placeholder="Timezone"
                value={clientForm.timezone}
                onChange={(e) => setClientForm((x) => ({ ...x, timezone: e.target.value }))}
                required
              />
              <input
                type="number"
                min={1}
                value={clientForm.retentionMonths}
                onChange={(e) =>
                  setClientForm((x) => ({ ...x, retentionMonths: Number(e.target.value || 27) }))
                }
                required
              />
              <label>
                <input
                  type="checkbox"
                  checked={clientForm.isActive}
                  onChange={(e) => setClientForm((x) => ({ ...x, isActive: e.target.checked }))}
                />{' '}
                Active
              </label>
              <div className="row-actions">
                <button type="submit">{mode === 'create' ? 'Create' : 'Save'}</button>
                <button type="button" onClick={closePanel}>
                  Cancel
                </button>
              </div>
            </form>
          </article>
        </div>
      )
    }

    if (view === 'domains') {
      return (
        <div className="modal-backdrop" role="presentation" onClick={closePanel}>
          <article className="modal-card" onClick={(e) => e.stopPropagation()}>
            <h3>{mode === 'create' ? 'Create Domain' : `Edit ${selectedDomain?.name ?? 'Domain'}`}</h3>
            <form className="form-grid" onSubmit={mode === 'create' ? handleCreateDomain : handleUpdateDomain}>
              <input
                placeholder="Domain name"
                value={domainForm.name}
                onChange={(e) => setDomainForm((x) => ({ ...x, name: e.target.value }))}
                required
              />
              <select
                value={domainForm.clientId}
                onChange={(e) => setDomainForm((x) => ({ ...x, clientId: e.target.value }))}
                required
              >
                <option value="">Select client</option>
                {sortedClients.map((client) => (
                  <option key={client.id} value={client.id}>
                    {client.name}
                  </option>
                ))}
              </select>
              <label>
                <input
                  type="checkbox"
                  checked={domainForm.isActive}
                  onChange={(e) => setDomainForm((x) => ({ ...x, isActive: e.target.checked }))}
                />{' '}
                Active
              </label>
              <div className="row-actions">
                <button type="submit">{mode === 'create' ? 'Create' : 'Save'}</button>
                <button type="button" onClick={closePanel}>
                  Cancel
                </button>
              </div>
            </form>
          </article>
        </div>
      )
    }

    return (
      <div className="modal-backdrop" role="presentation" onClick={closePanel}>
        <article className="modal-card" onClick={(e) => e.stopPropagation()}>
          <h3>{mode === 'create' ? 'Create Mailbox Source' : `Edit ${selectedMailbox?.name ?? 'Source'}`}</h3>
          <form className="form-grid" onSubmit={mode === 'create' ? handleCreateMailbox : handleUpdateMailbox}>
            <input
              placeholder="Source name"
              value={mailboxForm.name}
              onChange={(e) => setMailboxForm((x) => ({ ...x, name: e.target.value }))}
              required
            />
            <select
              value={mailboxForm.protocol}
              onChange={(e) =>
                setMailboxForm((x) => ({ ...x, protocol: e.target.value as 'imap' | 'pop3' }))
              }
            >
              <option value="imap">IMAP</option>
              <option value="pop3">POP3</option>
            </select>
            <input
              placeholder="Host"
              value={mailboxForm.host}
              onChange={(e) => setMailboxForm((x) => ({ ...x, host: e.target.value }))}
              required
            />
            <input
              type="number"
              min={1}
              value={mailboxForm.port}
              onChange={(e) => setMailboxForm((x) => ({ ...x, port: Number(e.target.value || 993) }))}
              required
            />
            <input
              placeholder="Username"
              value={mailboxForm.username}
              onChange={(e) => setMailboxForm((x) => ({ ...x, username: e.target.value }))}
              required
            />
            <input
              placeholder={mode === 'create' ? 'Password' : 'New password (optional)'}
              type="password"
              value={mailboxForm.password}
              onChange={(e) => setMailboxForm((x) => ({ ...x, password: e.target.value }))}
              required={mode === 'create'}
            />
            <select
              value={mailboxForm.defaultClientId}
              onChange={(e) => setMailboxForm((x) => ({ ...x, defaultClientId: e.target.value }))}
              required
            >
              <option value="">Select default client</option>
              {sortedClients.map((client) => (
                <option key={client.id} value={client.id}>
                  {client.name}
                </option>
              ))}
            </select>
            <label>
              <input
                type="checkbox"
                checked={mailboxForm.useTls}
                onChange={(e) => setMailboxForm((x) => ({ ...x, useTls: e.target.checked }))}
              />{' '}
              Use TLS
            </label>
            <label>
              <input
                type="checkbox"
                checked={mailboxForm.isActive}
                onChange={(e) => setMailboxForm((x) => ({ ...x, isActive: e.target.checked }))}
              />{' '}
              Active
            </label>
            <div className="row-actions">
              <button type="submit">{mode === 'create' ? 'Create' : 'Save'}</button>
              <button type="button" onClick={closePanel}>
                Cancel
              </button>
            </div>
          </form>
        </article>
      </div>
    )
  }

  return (
    <main className="app-shell">
      <header>
        <img className="brand-logo" src="/dmarc-analyzer-net-logo.svg" alt="DMARC Analyzer .NET" />
        <p>Single-image ASP.NET + React baseline is ready.</p>
      </header>

      <section className="status-card">
        <h2>API Connectivity</h2>
        <p>{status}</p>
      </section>

      <nav className="tab-nav">
        {views.map((item) => (
          <button
            key={item.key}
            type="button"
            className={view === item.key ? 'tab-btn active' : 'tab-btn'}
            onClick={() => {
              setView(item.key)
              closePanel()
            }}
          >
            {item.label}
          </button>
        ))}
        <button className="tab-btn" type="button" onClick={() => void loadData()}>
          Refresh
        </button>
        <button className="tab-btn primary" type="button" onClick={openCreate}>
          + New
        </button>
      </nav>

      {busy && <p className="info">Loading data...</p>}
      {error && <p className="error">{error}</p>}

      {renderListSection()}
      {renderModalForm()}
    </main>
  )
}

export default App
