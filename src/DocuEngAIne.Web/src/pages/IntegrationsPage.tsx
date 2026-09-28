import { Fragment, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import {
  createIntegration,
  createMcpServer,
  MCP_ENDPOINTS,
  mcpKindForProvider,
  refreshIntegrationHistory,
  syncIntegration,
  testIntegration,
  UNLIMITED_CALL_LIMIT,
  updateIntegration,
  useIntegrationMappings,
  useIntegrations,
  useLlmConfig,
  useMcpServers,
  useSyncRuns,
  type IntegrationConnection,
  type IntegrationMapping,
  type IntegrationProvider,
  type McpServer,
  type McpServerKind,
  type SyncRun,
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

/**
 * Providers the API resolves — or registers once — StackJack Compact for, so creating one needs
 * nothing but a Key Vault secret name. Mirrors McpServerDefaults.IsCompactBacked on the server.
 */
const builtInCompactProviders: IntegrationProvider[] = ['Halo', 'NinjaOne', 'Cipp', 'Meraki', 'UniFi', 'Action1', 'Autotask', 'Blackpoint', 'DefensX', 'Pax8', 'Slide']

/** "44m", "12h", "30d" — short enough for a table cell. */
function formatInterval(minutes: number) {
  if (minutes % 1440 === 0) return `${minutes / 1440}d`
  if (minutes % 60 === 0) return `${minutes / 60}h`
  return `${minutes}m`
}

/**
 * "Business · every 44m". The cadence is the scheduled-check interval: override if set,
 * otherwise the interval the detected plan can afford.
 */
function planSummary(i: IntegrationConnection) {
  const plan = i.stackJackPlan && i.stackJackPlan !== 'Unknown' ? i.stackJackPlan : 'Plan not detected'
  const cadence = i.syncIntervalMinutes != null ? `every ${formatInterval(i.syncIntervalMinutes)}` : 'no cadence yet'
  return `${plan} · ${cadence}`
}

function planTitle(i: IntegrationConnection) {
  const lines: string[] = []
  if (i.monthlyCallLimit != null) {
    lines.push(
      i.monthlyCallLimit >= UNLIMITED_CALL_LIMIT
        ? 'Allowance: unlimited calls per cycle'
        : `Allowance: ${i.monthlyCallLimit.toLocaleString()} calls per cycle`,
    )
  }
  if (i.planDetectedAt) lines.push(`Read from StackJack ${formatTimestamp(i.planDetectedAt)}`)
  if (i.syncIntervalMinutesOverride != null) lines.push(`Override: ${i.syncIntervalMinutesOverride} minutes`)
  if (i.nextSyncDueAt) lines.push(`Next scheduled check ${formatTimestamp(i.nextSyncDueAt)}`)
  if (lines.length === 0) lines.push('Press Test to read the plan and allowance from StackJack.')
  return lines.join('\n')
}

function syncStatusClass(status: string) {
  const key = status.toLowerCase()
  if (key === 'succeeded') return 'status-completed'
  if (key === 'failed') return 'status-failed'
  if (key === 'partial') return 'status-partial'
  return 'status-running'
}

/** Last SyncRun status + time from GET /api/integrations/{id}/runs. SWR shares the cache with History. */
function LastRunCell({ integrationId, lastSyncAt }: { integrationId: string; lastSyncAt?: string | null }) {
  const { data, isLoading } = useSyncRuns(integrationId)
  const runs: SyncRun[] = Array.isArray(data) ? data : []
  const last = runs[0]
  if (!last) {
    return <span>{isLoading ? '…' : formatTimestamp(lastSyncAt)}</span>
  }
  return (
    <>
      <span className={`tag ${syncStatusClass(last.status)}`}>{last.status}</span>
      {' '}
      {formatTimestamp(last.finishedAt ?? last.startedAt)}
    </>
  )
}

const mappingLimit = 25

/** "12 Company, 3 Site" — how many mappings of each external type this integration holds. */
function mappingSummary(mappings: IntegrationMapping[]) {
  const counts = new Map<string, number>()
  for (const m of mappings) {
    counts.set(m.externalType, (counts.get(m.externalType) ?? 0) + 1)
  }
  return Array.from(counts.entries()).map(([type, count]) => `${count} ${type}`)
}

/**
 * Run history + mappings for one integration, expanded in place under its row so the
 * Sync button and what that sync did stay on the same screen.
 */
function IntegrationHistory({ integrationId }: { integrationId: string }) {
  const { data: runData, error: runError, isLoading: runsLoading } = useSyncRuns(integrationId)
  const { data: mapData, error: mapError, isLoading: mapsLoading } = useIntegrationMappings(integrationId)
  const runs: SyncRun[] = Array.isArray(runData) ? runData : []
  const mappings: IntegrationMapping[] = Array.isArray(mapData) ? mapData : []

  const summary = mappingSummary(mappings)

  return (
    <div className="sync-history">
      <h3>Sync runs</h3>
      {runsLoading && <p>Loading…</p>}
      {runError && <p className="error">Failed to load sync runs.</p>}
      {!runsLoading && !runError && runs.length === 0 && (
        <p className="muted">No syncs yet. Use Sync to run one.</p>
      )}
      {runs.length > 0 && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Status</th>
              <th>Provider</th>
              <th>Started</th>
              <th>Finished</th>
              <th>Created</th>
              <th>Updated</th>
              <th>Skipped</th>
              <th>Error</th>
            </tr>
          </thead>
          <tbody>
            {runs.map((r) => (
              <tr key={r.id}>
                <td>
                  <span className={`tag ${syncStatusClass(r.status)}`}>{r.status}</span>
                </td>
                <td>{r.provider || '—'}</td>
                <td>{formatTimestamp(r.startedAt)}</td>
                <td>{formatTimestamp(r.finishedAt)}</td>
                <td>{r.itemsCreated ?? 0}</td>
                <td>{r.itemsUpdated ?? 0}</td>
                <td>{r.itemsSkipped ?? 0}</td>
                <td className="sync-error">
                  {r.errorSummary ? <span className="error">{r.errorSummary}</span> : '—'}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <h3>Mappings</h3>
      {mapsLoading && <p>Loading…</p>}
      {mapError && <p className="error">Failed to load mappings.</p>}
      {!mapsLoading && !mapError && mappings.length === 0 && (
        <p className="muted">No external records mapped to local records yet.</p>
      )}
      {mappings.length > 0 && (
        <>
          <p className="muted">
            {mappings.length === 1 ? '1 mapping' : `${mappings.length} mappings`}
            {summary.length > 0 ? ` — ${summary.join(', ')}` : ''}
          </p>
          <table className="data-table">
            <thead>
              <tr>
                <th>External type</th>
                <th>External id</th>
                <th>Local entity</th>
              </tr>
            </thead>
            <tbody>
              {mappings.slice(0, mappingLimit).map((m) => (
                <tr key={m.id}>
                  <td>{m.externalType}</td>
                  <td>{m.externalId}</td>
                  <td>
                    {m.localEntityType === 'Company' ? (
                      <Link to={`/companies/${m.localEntityId}`}>{m.localEntityId}</Link>
                    ) : (
                      `${m.localEntityType} ${m.localEntityId}`
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {mappings.length > mappingLimit && (
            <p className="muted">Showing the first {mappingLimit} of {mappings.length}.</p>
          )}
        </>
      )}
    </div>
  )
}

function LlmSettingsReadout() {
  const { data, error, isLoading } = useLlmConfig()

  return (
    <section className="panel">
      <h2>LLM</h2>
      <p className="muted">
        Provider and model come from app settings and Key Vault. They cannot be changed here.
      </p>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load LLM config.</p>}
      {data && (
        <p>
          Provider: <strong>{data.provider}</strong>
          {' · '}
          Model: <strong>{data.model}</strong>
        </p>
      )}
    </section>
  )
}

export function IntegrationsPage() {
  const { data: serverData, error: serverError, isLoading: serversLoading, mutate: mutateServers } = useMcpServers()
  const { data: intData, error: intError, isLoading: intLoading, mutate: mutateIntegrations } = useIntegrations()
  const servers = Array.isArray(serverData) ? serverData : []
  const integrations = Array.isArray(intData) ? intData : []

  const [mcpName, setMcpName] = useState('')
  const [mcpKind, setMcpKind] = useState<McpServerKind>('StackJackCompact')
  const [mcpUrl, setMcpUrl] = useState(MCP_ENDPOINTS.StackJackCompact)
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
  const [syncOverride, setSyncOverride] = useState('')
  const [editingId, setEditingId] = useState<string | null>(null)

  const hasCompactServer = servers.some((s) => (s.kind || 'StackJackCompact') === 'StackJackCompact')
  const builtInCompact = builtInCompactProviders.includes(provider)

  const [message, setMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [actionId, setActionId] = useState<string | null>(null)
  const [historyId, setHistoryId] = useState<string | null>(null)

  async function onCreateMcp(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    setErrorMessage(null)
    setBusy(true)
    try {
      await createMcpServer({
        name: mcpName.trim(),
        kind: mcpKind,
        transport: 'Http',
        endpointUrl: mcpUrl.trim() || MCP_ENDPOINTS[mcpKind],
        authSecretName: mcpSecret.trim() || null,
        enabled: mcpEnabled,
      })
      setMcpName('')
      setMcpKind('StackJackCompact')
      setMcpUrl(MCP_ENDPOINTS.StackJackCompact)
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
    setSyncOverride(i.syncIntervalMinutesOverride != null ? String(i.syncIntervalMinutesOverride) : '')
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
    setSyncOverride('')
  }

  async function onCreateIntegration(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    setErrorMessage(null)
    // Compact is built in, so these two checks only cover what the server genuinely cannot work out:
    // a provider with no built-in connector, and the very first Compact integration for a tenant that
    // has no registration and gave no secret name to make one from.
    if (!mcpServerId && !builtInCompact) {
      setErrorMessage(
        provider === 'Composio'
          ? 'Select a Composio MCP server under Advanced.'
          : 'Select an MCP server for CustomMcp under Advanced.',
      )
      return
    }
    if (!mcpServerId && builtInCompact && !authSecretName.trim() && !hasCompactServer) {
      setErrorMessage('Enter the Key Vault secret name holding this tenant\'s StackJack Compact API key.')
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
        const trimmedOverride = syncOverride.trim()
        const overrideMinutes = trimmedOverride === '' ? null : Number(trimmedOverride)
        await updateIntegration(editingId, {
          displayName,
          authSecretName: authSecretName.trim() || null,
          mcpServerId: mcpServerId || null,
          // Left out entirely when the box is empty: the API reads 0 as "clear the override" and a
          // missing field as "leave it alone", and an empty box means the latter.
          ...(overrideMinutes != null && Number.isFinite(overrideMinutes)
            ? { syncIntervalMinutesOverride: Math.trunc(overrideMinutes) }
            : {}),
          ...policy,
        })
        setMessage('Integration updated.')
      } else {
        await createIntegration({
          provider,
          displayName,
          authSecretName: authSecretName.trim() || null,
          mcpServerId: mcpServerId || null,
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
      setSyncOverride('')
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
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Sync failed')
    } finally {
      setActionId(null)
      // A failed sync still writes status and lastError on the connection AND records a run, so both
      // the row and the history must refresh either way. Refreshing only on success left the row
      // showing stale state next to a history panel displaying the failure.
      setHistoryId(id)
      void mutateIntegrations().catch(() => undefined)
      void refreshIntegrationHistory(id).catch(() => undefined)
    }
  }

  return (
    <div className="page">
      <h1>Integrations</h1>
      <p>
        StackJack Compact is built in. Pick a provider — Halo, NinjaOne, CIPP, Meraki, UniFi, Action1, Autotask, Blackpoint, DefensX, Pax8 or Slide — and give the
        Key Vault secret name holding this tenant&rsquo;s Compact API key; the Compact MCP server is registered
        the first time and reused after that. A secret name is never a secret value: nothing but the name is stored.
        Composio and CustomMcp still need a server registered under Advanced.
      </p>
      <p className="muted">
        Enabled connections with a Compact server and a detected plan sync on the cadence shown
        (or the override, if set). You can still press Sync to run one now. Test reads the StackJack
        plan behind the connection.
      </p>
      {message && <p className="banner">{message}</p>}
      {errorMessage && <p className="error">{errorMessage}</p>}

      <LlmSettingsReadout />

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
                <th>Plan · cadence</th>
                <th>Last run</th>
                <th>LastError</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {integrations.length === 0 && (
                <tr>
                  <td colSpan={6}>No integration connections.</td>
                </tr>
              )}
              {integrations.map((i) => (
                <Fragment key={i.id}>
                  <tr>
                    <td>{i.provider}</td>
                    <td>{i.status || '—'}</td>
                    <td title={planTitle(i)}>{planSummary(i)}</td>
                    <td><LastRunCell integrationId={i.id} lastSyncAt={i.lastSyncAt} /></td>
                    <td>{i.lastError || '—'}</td>
                    <td className="row-actions">
                      <button className="btn btn-secondary" type="button" disabled={actionId === i.id} onClick={() => onTest(i.id)}>
                        Test
                      </button>
                      <button className="btn btn-secondary" type="button" disabled={actionId === i.id} onClick={() => onSync(i.id)}>
                        Sync
                      </button>
                      <button
                        className="btn btn-secondary"
                        type="button"
                        onClick={() => setHistoryId(historyId === i.id ? null : i.id)}
                      >
                        {historyId === i.id ? 'Hide history' : 'History'}
                      </button>
                      <button className="btn btn-secondary" type="button" onClick={() => startEdit(i)}>
                        Edit
                      </button>
                    </td>
                  </tr>
                  {historyId === i.id && (
                    <tr>
                      <td className="sync-history-cell" colSpan={6}>
                        <IntegrationHistory integrationId={i.id} />
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        )}

        <p>Halo company pull uses Compact <code>halo_list_clients</code>. NinjaOne company pull uses Compact <code>ninja_list_organizations</code>. CIPP tenant pull uses Compact <code>cipp_list_tenants</code>. Meraki organization pull uses Compact <code>meraki_get_organizations</code> (orgs → companies; networks later). UniFi console pull uses Compact <code>unifi_sm_list_hosts</code> (hosts → companies; not sites). Action1 organization pull uses Compact <code>action1_list_organizations</code> (skips the MSP default organization). Autotask company pull uses Compact <code>at_list_companies</code> (not the active/customer pre-filters; <code>SkipInactive</code> drops <code>isActive</code> false). Blackpoint tenant pull uses Compact <code>compassone_list_tenants</code> (id/name/domain; installer URLs are not stored). DefensX customer pull uses Compact <code>dfx_list_customers</code> (id/name/<code>domains[0]</code>; <code>SkipInactive</code> drops <code>enabled</code> false). Pax8 company pull uses Compact <code>pax8_list_companies</code> (id/name/website/city/state; <code>SkipInactive</code> drops <code>status</code> Inactive and Deleted). Slide client pull uses Compact <code>slide_list_clients</code> (<code>client_id</code>/<code>name</code>; no inactive flag, so <code>SkipInactive</code> does not invent a drop). Sync policy defaults skip inactive accounts and refuse overwriting company details.</p>
        <form onSubmit={onCreateIntegration}>
          <div className="form-grid">
            <label>
              Provider
              <select
                className="input"
                value={provider}
                disabled={!!editingId}
                onChange={(e) => {
                  setProvider(e.target.value as IntegrationProvider)
                  setMcpServerId('')
                }}
              >
                <option value="Halo">Halo</option>
                <option value="NinjaOne">NinjaOne</option>
                <option value="Cipp">CIPP</option>
                <option value="Meraki">Meraki</option>
                <option value="Action1">Action1</option>
                <option value="Autotask">Autotask</option>
                <option value="UniFi">UniFi</option>
                <option value="Blackpoint">Blackpoint</option>
                <option value="DefensX">DefensX</option>
                <option value="Pax8">Pax8</option>
                <option value="Slide">Slide</option>
                <option value="Composio">Composio</option>
                <option value="CustomMcp">CustomMcp</option>
              </select>
            </label>
            <label>
              API credential (Key Vault secret name)
              <input
                className="input"
                value={authSecretName}
                onChange={(e) => setAuthSecretName(e.target.value)}
                placeholder={builtInCompact ? 'kv-stackjack-compact' : 'Key Vault secret name'}
              />
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
          </div>

          <details className="advanced">
            <summary>Advanced — MCP server{editingId ? ' and cadence' : ''}</summary>
            <div className="form-grid">
              <label>
                MCP server
                <select className="input" value={mcpServerId} onChange={(e) => setMcpServerId(e.target.value)}>
                  <option value="">
                    {builtInCompact ? 'Built-in StackJack Compact (default)' : 'Select a server…'}
                  </option>
                  {servers
                    .filter((s: McpServer) => {
                      const kind = mcpKindForProvider(provider)
                      if (!kind) return true
                      return (s.kind || 'StackJackCompact') === kind
                    })
                    .map((s) => (
                      <option key={s.id} value={s.id}>{s.name} ({s.kind || 'MCP'})</option>
                    ))}
                </select>
              </label>
              {editingId && (
                <label>
                  Check interval override (minutes)
                  <input
                    className="input"
                    type="number"
                    min={0}
                    value={syncOverride}
                    onChange={(e) => setSyncOverride(e.target.value)}
                    placeholder="derived from the plan"
                  />
                </label>
              )}
            </div>
            {editingId && (
              <p className="muted">
                An override may be slower than the plan allows, never faster. Enter 0 to clear it and go back to the
                derived cadence. The scheduler uses this interval.
              </p>
            )}
          </details>

          <div className="form-grid">
            <button className="btn" type="submit" disabled={busy}>{editingId ? 'Save connection' : 'Add connection'}</button>
            {editingId && (
              <button className="btn btn-secondary" type="button" disabled={busy} onClick={cancelEdit}>Cancel</button>
            )}
          </div>
        </form>
      </section>

      <details className="panel">
        <summary>Advanced — MCP servers</summary>
        <p className="muted">
          Compact-backed providers register their StackJack Compact server automatically. Add one here only for
          Composio or CustomMcp, or to point a connection at a specific server. Do not add a second
          StackJack server.
        </p>
        {serversLoading && <p>Loading…</p>}
        {serverError && <p className="error">Failed to load MCP servers.</p>}
        {!serversLoading && !serverError && (
          <table className="data-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Kind</th>
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
                  <td>{s.kind || '—'}</td>
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
            Kind
            <select
              className="input"
              value={mcpKind}
              onChange={(e) => {
                const kind = e.target.value as McpServerKind
                setMcpKind(kind)
                setMcpUrl(MCP_ENDPOINTS[kind])
              }}
            >
              <option value="StackJackCompact">StackJack Compact</option>
              <option value="Composio">Composio</option>
            </select>
          </label>
          <label>
            Name
            <input className="input" required value={mcpName} onChange={(e) => setMcpName(e.target.value)} placeholder={mcpKind === 'Composio' ? 'Composio Connect' : 'StackJack Compact'} />
          </label>
          <label>
            EndpointUrl
            <input className="input" value={mcpUrl} onChange={(e) => setMcpUrl(e.target.value)} placeholder={MCP_ENDPOINTS[mcpKind]} />
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
      </details>
    </div>
  )
}
