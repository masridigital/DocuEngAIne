import { useState } from 'react'
import {
  canManageUsers,
  exportAuditCsv,
  useAuditActivity,
  useAuditEvents,
  useFormat,
  useProfile,
  type AuditEvent,
  type AuditFilters,
} from '../hooks/useApi'

const ACTIONS = [
  'KeeperLink.Reveal',
  'User.ChangeRole',
  'ApiToken.Create',
  'ApiToken.Revoke',
  'Integration.Sync',
  'Audit.Export',
]

const CATEGORIES = ['resource', 'access', 'archive', 'export', 'system', 'security']

function renderChanges(changesJson?: string | null) {
  if (!changesJson) return null
  try {
    const parsed = JSON.parse(changesJson) as Record<string, { from?: unknown; to?: unknown }>
    return (
      <ul className="audit-changes">
        {Object.entries(parsed).map(([field, change]) => (
          <li key={field}>
            <strong>{field}</strong>: {JSON.stringify(change.from)} → {JSON.stringify(change.to)}
          </li>
        ))}
      </ul>
    )
  } catch {
    return <pre className="audit-changes">{changesJson}</pre>
  }
}

function ActivityTimeline(props: { entityType: string; entityId: string; onClose: () => void }) {
  const { data, error, isLoading } = useAuditActivity(props.entityType, props.entityId)
  const fmt = useFormat()
  return (
    <div className="panel">
      <h2>
        Activity — {props.entityType} <button onClick={props.onClose}>Close</button>
      </h2>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load activity.</p>}
      {data && data.length === 0 && <p>No recorded activity for this record.</p>}
      {data && data.length > 0 && (
        <ol className="timeline">
          {data.map((e, i) => (
            <li key={e.id}>
              <div>
                <strong>{e.action}</strong>
                {e.category ? <span className="badge">{e.category}</span> : null}
                {i === 0 ? <span className="badge">latest</span> : null}
              </div>
              <div>{fmt.dateTime(e.occurredAt)} — {e.actorName || e.actorObjectId || 'System'}</div>
              {e.details ? <div>{e.details}</div> : null}
              {renderChanges(e.changesJson)}
            </li>
          ))}
        </ol>
      )}
    </div>
  )
}

export function AuditPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = canManageUsers(profile?.role)
  const fmt = useFormat()

  const [filters, setFilters] = useState<AuditFilters>({})
  const [page, setPage] = useState(1)
  const [expandedId, setExpandedId] = useState<string | null>(null)
  const [timeline, setTimeline] = useState<{ entityType: string; entityId: string } | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [exporting, setExporting] = useState(false)

  const { data, error, isLoading } = useAuditEvents({ ...filters, page }, allowed)
  const events = data?.items ?? []
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  function setFilter(patch: Partial<AuditFilters>) {
    setFilters((prev) => ({ ...prev, ...patch }))
    setPage(1)
  }

  async function onExport() {
    setErrorMessage(null)
    setExporting(true)
    try {
      await exportAuditCsv(filters, fmt.regional.timeZone)
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Export failed.')
    } finally {
      setExporting(false)
    }
  }

  if (profileLoading) {
    return (
      <div className="page">
        <h1>Audit</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>Audit</h1>
        <p>The audit trail is available to Admin and Owner.</p>
      </div>
    )
  }

  return (
    <div className="page">
      <h1>Audit</h1>
      <p>
        Every write, reveal, and export in this tenant — who did it, from where, and what changed.
        Exporting the trail is itself audited.
      </p>
      {errorMessage && <p className="error">{errorMessage}</p>}

      <div className="toolbar">
        <select value={filters.action ?? ''} onChange={(e) => setFilter({ action: e.target.value || undefined })}>
          <option value="">All actions</option>
          {ACTIONS.map((a) => (
            <option key={a} value={a}>{a}</option>
          ))}
        </select>
        <select value={filters.category ?? ''} onChange={(e) => setFilter({ category: e.target.value || undefined })}>
          <option value="">All categories</option>
          {CATEGORIES.map((c) => (
            <option key={c} value={c}>{c}</option>
          ))}
        </select>
        <input
          placeholder="Entity type (e.g. Document)"
          value={filters.entityType ?? ''}
          onChange={(e) => setFilter({ entityType: e.target.value || undefined })}
        />
        <label>
          From <input type="date" value={filters.from ?? ''} onChange={(e) => setFilter({ from: e.target.value || undefined })} />
        </label>
        <label>
          To <input type="date" value={filters.to ?? ''} onChange={(e) => setFilter({ to: e.target.value || undefined })} />
        </label>
        <button onClick={onExport} disabled={exporting}>
          {exporting ? 'Exporting…' : 'Export CSV'}
        </button>
      </div>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load audit events.</p>}

      {data && (
        <>
          <p>
            {data.total} event{data.total === 1 ? '' : 's'} · page {data.page} of {totalPages}
          </p>
          <table className="data-table">
            <thead>
              <tr>
                <th>When</th>
                <th>Action</th>
                <th>Category</th>
                <th>Target</th>
                <th>Actor</th>
                <th>IP</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {events.map((e: AuditEvent) => (
                <tr key={e.id}>
                  <td>{fmt.dateTime(e.occurredAt)}</td>
                  <td>{e.action}</td>
                  <td>{e.category ?? '—'}</td>
                  <td>
                    {e.targetLabel || e.entityType}
                    {e.targetLabel ? <span className="muted"> ({e.entityType})</span> : null}
                  </td>
                  <td>{e.actorName || e.actorObjectId || 'System'}</td>
                  <td>{e.ipAddress ?? '—'}</td>
                  <td>
                    <button onClick={() => setExpandedId(expandedId === e.id ? null : e.id)}>
                      {expandedId === e.id ? 'Hide' : 'Details'}
                    </button>
                    {e.entityId ? (
                      <button onClick={() => setTimeline({ entityType: e.entityType, entityId: e.entityId! })}>
                        Timeline
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {expandedId && (() => {
            const e = events.find((x) => x.id === expandedId)
            if (!e) return null
            return (
              <div className="panel">
                <h2>Event details</h2>
                <p>
                  {e.action} on {e.targetLabel || e.entityType} at {fmt.dateTime(e.occurredAt)}
                </p>
                {e.details ? <p>{e.details}</p> : null}
                {renderChanges(e.changesJson)}
                <p className="muted">
                  {e.requestMethod ?? ''} {e.requestPath ?? ''} {e.ipAddress ? `· ${e.ipAddress}` : ''}
                </p>
              </div>
            )
          })()}
          <div className="toolbar">
            <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</button>
            <button disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>Next</button>
          </div>
        </>
      )}

      {timeline && (
        <ActivityTimeline
          entityType={timeline.entityType}
          entityId={timeline.entityId}
          onClose={() => setTimeline(null)}
        />
      )}
    </div>
  )
}
