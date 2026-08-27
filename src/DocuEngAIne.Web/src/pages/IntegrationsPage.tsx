import { useState, type FormEvent } from 'react'
import {
  createIntegration,
  createMcpServer,
  syncIntegration,
  testIntegration,
  updateIntegration,
  useIntegrations,
  useMcpServers,
  type IntegrationConnection,
  type IntegrationProvider,
  type McpTransport,
} from '../hooks/useApi'

const defaultPolicy = {
  skipInactive: true,
  skipContacts: false,
  skipLocations: false,
  skipAssets: false,
  autoUpdateAssetNames: false,
  updateCompanyDetails: false,
}

function formatTimestamp(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function IntegrationsPage() {
  const { data: serverData, error: serverError, isLoading: serversLoading, mutate: mutateServers } = useMcpServers()
  const { data: intData, error: intError, isLoading: intLoading, mutate: mutateIntegrations } = useIntegrations()
  const servers = Array.isArray(serverData) ? serverData : []
  const integrations = Array.isArray(intData) ? intData : []

  const [mcpName, setMcpName] = useState('')
  const [mcpTransport, setMcpTransport] = useState<McpTransport>('Http')
  const [mcpUrl, setMcpUrl] = useState('')
  const [mcpSecret, setMcpSecret] = useState('')
  const [mcpEnabled, setMcpEnabled] = useState(true)

  const [provider, setProvider] = useState<IntegrationProvider>('Halo')
  const [mcpServerId, setMcpServerId] = useState('')
  const [authSecretName, setAuthSecretName] = useState('')
  const [skipInactive, setSkipInactive] = useState(defaultPolicy.skipInactive)
  const [skipContacts, setSkipContacts] = useState(defaultPolicy.skipContacts)
  const [skipLocations, setSkipLocations] = useState(defaultPolicy.skipLocations)
  const [skipAssets, setSkipAssets] = useState(defaultPolicy.skipAssets)
  const [autoUpdateAssetNames, setAutoUpdateAssetNames] = useState(defaultPolicy.autoUpdateAssetNames)
  const [updateCompanyDetails, setUpdateCompanyDetails] = useState(defaultPolicy.updateCompanyDetails)
  const [editingId, setEditingId] = useState<string | null>(null)

  const [message, setMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [actionId, setActionId] = useState<string | null>(null)

  async function onCreateMcp(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    setErrorMessage(null)
    setBusy(true)
    try {
      await createMcpServer({
        name: mcpName.trim(),
        transport: mcpTransport,
        endpointUrl: mcpUrl.trim() || null,
        authSecretName: mcpSecret.trim() || null,
        enabled: mcpEnabled,
      })
      setMcpName('')
      setMcpTransport('Http')
      setMcpUrl('')
      setMcpSecret('')
      setMcpEnabled(true)
      setMessage('MCP server added.')
      await mutateServers()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to add MCP server.')
    } finally {
      setBusy(false)
    }
  }

  function startEdit(i: IntegrationConnection) {
    setEditingId(i.id)
    setProvider((i.provider as IntegrationProvider) || 'Halo')
    setMcpServerId(i.mcpServerId || '')
    setAuthSecretName(i.authSecretName || '')
    setSkipInactive(i.skipInactive ?? defaultPolicy.skipInactive)
    setSkipContacts(i.skipContacts ?? defaultPolicy.skipContacts)
    setSkipLocations(i.skipLocations ?? defaultPolicy.skipLocations)
    setSkipAssets(i.skipAssets ?? defaultPolicy.skipAssets)
    setAutoUpdateAssetNames(i.autoUpdateAssetNames ?? defaultPolicy.autoUpdateAssetNames)
    setUpdateCompanyDetails(i.updateCompanyDetails ?? defaultPolicy.updateCompanyDetails)
    setMessage(null)
    setErrorMessage(null)
  }

  function cancelEdit() {
    setEditingId(null)
    setProvider('Halo')
    setMcpServerId('')
    setAuthSecretName('')
    setSkipInactive(defaultPolicy.skipInactive)
    setSkipContacts(defaultPolicy.skipContacts)
    setSkipLocations(defaultPolicy.skipLocations)
    setSkipAssets(defaultPolicy.skipAssets)
    setAutoUpdateAssetNames(defaultPolicy.autoUpdateAssetNames)
    setUpdateCompanyDetails(defaultPolicy.updateCompanyDetails)
  }

  async function onCreateIntegration(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    setErrorMessage(null)
    if (provider === 'CustomMcp' && !mcpServerId) {
      setErrorMessage('Select an MCP server for CustomMcp.')
      return
    }
    setBusy(true)
    try {
      const selectedServer = servers.find((s) => s.id === mcpServerId)
      const displayName =
        provider === 'CustomMcp' ? (selectedServer?.name ?? 'Custom MCP') : provider
      const policy = {
        skipInactive,
        skipContacts,
        skipLocations,
        skipAssets,
        autoUpdateAssetNames,
        updateCompanyDetails,
      }
      if (editingId) {
        await updateIntegration(editingId, {
          displayName,
          authSecretName: authSecretName.trim() || null,
          mcpServerId: provider === 'CustomMcp' ? mcpServerId : null,
          ...policy,
        })
        setMessage('Integration updated.')
      } else {
        await createIntegration({
          provider,
          displayName,
          authSecretName: authSecretName.trim() || null,
          mcpServerId: provider === 'CustomMcp' ? mcpServerId : null,
          isEnabled: true,
          ...policy,
        })
        setMessage('Integration added.')
      }
      setProvider('Halo')
      setMcpServerId('')
      setAuthSecretName('')
      setSkipInactive(defaultPolicy.skipInactive)
      setSkipContacts(defaultPolicy.skipContacts)
      setSkipLocations(defaultPolicy.skipLocations)
      setSkipAssets(defaultPolicy.skipAssets)
      setAutoUpdateAssetNames(defaultPolicy.autoUpdateAssetNames)
      setUpdateCompanyDetails(defaultPolicy.updateCompanyDetails)
      setEditingId(null)
      await mutateIntegrations()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Failed to save integration.')
    } finally {
      setBusy(false)
    }
  }

  async function onTest(id: string) {
    setActionId(id)
    setMessage(null)
    setErrorMessage(null)
    try {
      const result = await testIntegration(id)
      setMessage(result?.message || (result?.ok === false ? 'Test failed' : 'Test OK'))
      await mutateIntegrations()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Test failed')
    } finally {
      setActionId(null)
    }
  }

  async function onSync(id: string) {
    setActionId(id)
    setMessage(null)
    setErrorMessage(null)
    try {
      const result = await syncIntegration(id)
      const counts = [
        result.itemsCreated != null ? `${result.itemsCreated} created` : null,
        result.itemsUpdated != null ? `${result.itemsUpdated} updated` : null,
        result.itemsSkipped != null ? `${result.itemsSkipped} skipped` : null,
      ].filter(Boolean)
      setMessage(result.errorSummary || result.status || (counts.length ? counts.join(', ') : 'Sync finished'))
      await mutateIntegrations()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Sync failed')
    } finally {
      setActionId(null)
    }
  }

  return (
    <div className="page">
      <h1>Integrations</h1>
      <p>Register MCP servers, then connect Halo, NinjaOne, or a custom MCP provider. Secrets stay in Key Vault names only.</p>
      {message && <p className="banner">{message}</p>}
      {errorMessage && <p className="error">{errorMessage}</p>}

      <section className="panel">
        <h2>MCP Servers</h2>
        {serversLoading && <p>Loading…</p>}
        {serverError && <p className="error">Failed to load MCP servers.</p>}
        {!serversLoading && !serverError && (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Transport</th>
                <th>EndpointUrl</th>
                <th>AuthSecretName</th>
                <th>Enabled</th>
              </tr>
            </thead>
            <tbody>
              {servers.length === 0 && (
                <tr>
                  <td colSpan={5}>No MCP servers registered.</td>
                </tr>
              )}
              {servers.map((s) => (
                <tr key={s.id}>
                  <td>{s.name}</td>
                  <td>{s.transport}</td>
                  <td>{s.endpointUrl || '—'}</td>
                  <td>{s.authSecretName || '—'}</td>
                  <td>{s.enabled === false ? 'No' : 'Yes'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <form className="form-grid" onSubmit={onCreateMcp}>
          <label>
            Name
            <input className="input" required value={mcpName} onChange={(e) => setMcpName(e.target.value)} />
          </label>
          <label>
            Transport
            <select
              className="input"
              value={mcpTransport}
              onChange={(e) => setMcpTransport(e.target.value as McpTransport)}
            >
              <option value="Http">http</option>
              <option value="Sse">sse</option>
              <option value="Stdio">stdio</option>
            </select>
          </label>
          <label>
            EndpointUrl
            <input className="input" value={mcpUrl} onChange={(e) => setMcpUrl(e.target.value)} placeholder="https://…" />
          </label>
          <label>
            AuthSecretName
            <input className="input" value={mcpSecret} onChange={(e) => setMcpSecret(e.target.value)} placeholder="Key Vault secret name" />
          </label>
          <label className="check-label">
            <input type="checkbox" checked={mcpEnabled} onChange={(e) => setMcpEnabled(e.target.checked)} />
            Enabled
          </label>
          <button className="btn" type="submit" disabled={busy}>Add MCP server</button>
        </form>
      </section>

      <section className="panel">
        <h2>Integrations</h2>
        {intLoading && <p>Loading…</p>}
        {intError && <p className="error">Failed to load integrations.</p>}
        {!intLoading && !intError && (
          <table className="data-table">
            <thead>
              <tr>
                <th>Provider</th>
                <th>Status</th>
                <th>LastSyncAt</th>
                <th>LastError</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {integrations.length === 0 && (
                <tr>
                  <td colSpan={5}>No integration connections.</td>
                </tr>
              )}
              {integrations.map((i) => (
                <tr key={i.id}>
                  <td>{i.provider}</td>
                  <td>{i.status || '—'}</td>
                  <td>{formatTimestamp(i.lastSyncAt)}</td>
                  <td>{i.lastError || '—'}</td>
                  <td className="row-actions">
                    <button className="btn btn-secondary" type="button" disabled={actionId === i.id} onClick={() => onTest(i.id)}>
                      Test
                    </button>
                    <button className="btn btn-secondary" type="button" disabled={actionId === i.id} onClick={() => onSync(i.id)}>
                      Sync
                    </button>
                    <button className="btn btn-secondary" type="button" onClick={() => startEdit(i)}>
                      Edit
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        <p>Sync policy defaults skip inactive accounts and contacts and refuse overwriting company details.</p>
        <form className="form-grid" onSubmit={onCreateIntegration}>
          <label>
            Provider
            <select
              className="input"
              value={provider}
              disabled={!!editingId}
              onChange={(e) => setProvider(e.target.value as IntegrationProvider)}
            >
              <option value="Halo">Halo</option>
              <option value="NinjaOne">NinjaOne</option>
              <option value="CustomMcp">CustomMcp</option>
            </select>
          </label>
          {provider === 'CustomMcp' && (
            <label>
              MCP server
              <select className="input" required value={mcpServerId} onChange={(e) => setMcpServerId(e.target.value)}>
                <option value="">Select a server…</option>
                {servers.map((s) => (
                  <option key={s.id} value={s.id}>{s.name}</option>
                ))}
              </select>
            </label>
          )}
          <label>
            Auth secret name (optional)
            <input className="input" value={authSecretName} onChange={(e) => setAuthSecretName(e.target.value)} />
          </label>
          <label className="check-label">
            <input type="checkbox" checked={skipInactive} onChange={(e) => setSkipInactive(e.target.checked)} />
            Skip inactive accounts
          </label>
          <label className="check-label">
            <input type="checkbox" checked={skipContacts} onChange={(e) => setSkipContacts(e.target.checked)} />
            Skip contacts
          </label>
          <label className="check-label">
            <input type="checkbox" checked={skipLocations} onChange={(e) => setSkipLocations(e.target.checked)} />
            Skip locations
          </label>
          <label className="check-label">
            <input type="checkbox" checked={skipAssets} onChange={(e) => setSkipAssets(e.target.checked)} />
            Skip assets / devices
          </label>
          <label className="check-label">
            <input type="checkbox" checked={autoUpdateAssetNames} onChange={(e) => setAutoUpdateAssetNames(e.target.checked)} />
            Auto-update asset names
          </label>
          <label className="check-label">
            <input type="checkbox" checked={updateCompanyDetails} onChange={(e) => setUpdateCompanyDetails(e.target.checked)} />
            Update basic company details
          </label>
          <button className="btn" type="submit" disabled={busy}>{editingId ? 'Save connection' : 'Add connection'}</button>
          {editingId && (
            <button className="btn btn-secondary" type="button" disabled={busy} onClick={cancelEdit}>Cancel</button>
          )}
        </form>
      </section>
    </div>
  )
}
