import { useState } from 'react'
import { canManageUsers, useFormat, useProfile, useSecurityEvents, useSecurityEventTypes } from '../hooks/useApi'

const SEVERITY_CLASS: Record<string, string> = {
  info: 'status-completed',
  warning: 'status-partial',
  critical: 'status-failed',
}

export function SecurityEventsPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = canManageUsers(profile?.role)
  const fmt = useFormat()
  const [type, setType] = useState('')
  const [severity, setSeverity] = useState('')
  const [page, setPage] = useState(1)
  const { data, error, isLoading } = useSecurityEvents({ type, severity, page }, allowed)
  const { data: types } = useSecurityEventTypes(allowed)
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  if (profileLoading) {
    return (
      <div className="page">
        <h1>Security events</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>Security events</h1>
        <p>Security events are available to Admin and Owner.</p>
      </div>
    )
  }

  return (
    <div className="page">
      <h1>Security events</h1>
      <p>
        Attempts this tenant refused: from outside the IP allowlist, by a deactivated user, with an expired or revoked
        API token, or while the tenant was closed. Repeats from the same address and caller within a few minutes are
        counted on one row. Changes to security settings are in the audit log; sign-ins are in Entra&rsquo;s sign-in logs.
      </p>

      <div className="toolbar">
        <select
          value={type}
          onChange={(e) => {
            setType(e.target.value)
            setPage(1)
          }}
        >
          <option value="">All events</option>
          {(types ?? []).map((t) => (
            <option key={t.type} value={t.type}>
              {t.name}
            </option>
          ))}
        </select>
        <select
          value={severity}
          onChange={(e) => {
            setSeverity(e.target.value)
            setPage(1)
          }}
        >
          <option value="">Any severity</option>
          <option value="critical">Critical</option>
          <option value="warning">Warning</option>
          <option value="info">Info</option>
        </select>
      </div>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load security events.</p>}
      {data && (
        <>
          <p>
            {data.total} event{data.total === 1 ? '' : 's'} · page {data.page} of {totalPages}
          </p>
          <table className="data-table">
            <thead>
              <tr>
                <th>Last seen</th>
                <th>Severity</th>
                <th>Event</th>
                <th>From</th>
                <th>Who</th>
                <th>Times</th>
              </tr>
            </thead>
            <tbody>
              {data.items.length === 0 && (
                <tr>
                  <td colSpan={6}>Nothing recorded.</td>
                </tr>
              )}
              {data.items.map((e) => (
                <tr key={e.id}>
                  <td>{fmt.dateTime(e.lastSeenAt)}</td>
                  <td>
                    <span className={`tag ${SEVERITY_CLASS[e.severity] ?? ''}`}>{e.severity}</span>
                  </td>
                  <td>
                    {e.name}
                    <div className="muted">{e.description}</div>
                    {e.path ? <div className="muted">{e.path}</div> : null}
                  </td>
                  <td>{e.ipAddress ?? '—'}</td>
                  <td>{e.actorName ?? e.actorObjectId ?? '—'}</td>
                  <td>
                    {e.count}
                    {e.count > 1 ? <div className="muted">since {fmt.dateTime(e.firstSeenAt)}</div> : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div className="toolbar">
            <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</button>
            <button disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>Next</button>
          </div>
        </>
      )}
    </div>
  )
}
