import { useState, type FormEvent } from 'react'
import {
  addIpAllowlistEntry,
  canManageUsers,
  deleteIpAllowlistEntry,
  setIpAllowlistEnabled,
  updateIpAllowlistEntry,
  useFormat,
  useIpAccess,
  useProfile,
  type IpAllowlistEntry,
} from '../hooks/useApi'

export function IpAccessPage() {
  const fmt = useFormat()
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = canManageUsers(profile?.role)
  const { data, error, isLoading } = useIpAccess(allowed)

  const [cidr, setCidr] = useState('')
  const [label, setLabel] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function act(action: () => Promise<void>, success?: string) {
    setMessage(null)
    setErrorMessage(null)
    setBusy(true)
    try {
      await action()
      if (success) setMessage(success)
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusy(false)
    }
  }

  function onAdd(e: FormEvent) {
    e.preventDefault()
    const value = cidr.trim()
    if (!value) return
    void act(async () => {
      await addIpAllowlistEntry({ cidr: value, label: label.trim() || undefined })
      setCidr('')
      setLabel('')
    }, `Added ${value}.`)
  }

  function onToggleEntry(entry: IpAllowlistEntry) {
    void act(
      () => updateIpAllowlistEntry(entry.id, { isActive: !entry.isActive }),
      `${entry.cidr} ${entry.isActive ? 'deactivated' : 'activated'}.`,
    )
  }

  function onRename(entry: IpAllowlistEntry) {
    const next = window.prompt('Label for this entry:', entry.label ?? '')
    if (next === null) return
    void act(() => updateIpAllowlistEntry(entry.id, { label: next }), 'Label updated.')
  }

  function onRemove(entry: IpAllowlistEntry) {
    if (!window.confirm(`Remove ${entry.cidr} from the allowlist?`)) return
    void act(() => deleteIpAllowlistEntry(entry.id), `Removed ${entry.cidr}.`)
  }

  function onTogglePolicy(enabled: boolean) {
    if (!enabled && !window.confirm('Turn the IP allowlist off? Requests from any address will be accepted again.')) return
    void act(() => setIpAllowlistEnabled(enabled), enabled ? 'IP allowlist is on.' : 'IP allowlist is off.')
  }

  if (profileLoading) {
    return (
      <div className="page">
        <h1>IP access</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>IP access</h1>
        <p>IP access is available to Admin and Owner.</p>
      </div>
    )
  }

  const entries = data?.entries ?? []
  const activeCount = entries.filter((e) => e.isActive).length
  const enableBlocker =
    activeCount === 0
      ? 'Add at least one active entry first.'
      : !data?.currentIpCovered
        ? `Your current IP (${data?.currentIp ?? 'unknown'}) is not covered by an active entry.`
        : null

  return (
    <div className="page">
      <h1>IP access</h1>
      <p>
        When the allowlist is on, this tenant's API — the web app, the client portal and the MCP endpoint —
        only accepts requests from the networks below. It cannot be turned on unless your own address is
        covered, and while it is on no change may leave your address uncovered.
      </p>
      {message && <p className="banner">{message}</p>}
      {errorMessage && <p className="error">{errorMessage}</p>}

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load IP access settings.</p>}

      {data && (
        <>
          {data.breakGlass && (
            <p className="error">
              Enforcement is disabled on the server (Security:DisableIpAllowlist). The setting below is kept but
              not applied until that is removed.
            </p>
          )}

          <div className="panel">
            <h2>
              Allowlist <span className="badge">{data.enabled ? 'On' : 'Off'}</span>
            </h2>
            <p>
              Your current IP: <code>{data.currentIp ?? 'unknown'}</code> —{' '}
              {data.currentIpCovered ? 'covered by an active entry.' : 'not covered by any active entry.'}
            </p>
            {data.enabled ? (
              <button className="btn" type="button" disabled={busy} onClick={() => onTogglePolicy(false)}>
                Turn off
              </button>
            ) : (
              <button
                className="btn"
                type="button"
                disabled={busy || enableBlocker !== null}
                title={enableBlocker ?? undefined}
                onClick={() => onTogglePolicy(true)}
              >
                Turn on
              </button>
            )}
            {!data.enabled && enableBlocker && <p className="muted">{enableBlocker}</p>}
          </div>

          <form className="toolbar" onSubmit={onAdd}>
            <input
              className="input"
              placeholder="203.0.113.7 or 203.0.113.0/24"
              value={cidr}
              onChange={(e) => setCidr(e.target.value)}
              aria-label="IP address or CIDR range"
            />
            <input
              className="input"
              placeholder="Label (optional)"
              value={label}
              maxLength={100}
              onChange={(e) => setLabel(e.target.value)}
              aria-label="Label"
            />
            <button className="btn" type="submit" disabled={busy || !cidr.trim()}>
              Add
            </button>
            {data.currentIp && !data.currentIpCovered && (
              <button className="btn btn-secondary" type="button" disabled={busy} onClick={() => setCidr(data.currentIp ?? '')}>
                Use my IP
              </button>
            )}
          </form>

          <table className="data-table">
            <thead>
              <tr>
                <th>Network</th>
                <th>Label</th>
                <th>Status</th>
                <th>Added</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {entries.length === 0 && (
                <tr>
                  <td colSpan={5}>No entries yet.</td>
                </tr>
              )}
              {entries.map((entry) => (
                <tr key={entry.id}>
                  <td>
                    <code>{entry.cidr}</code>
                  </td>
                  <td>{entry.label || '—'}</td>
                  <td>{entry.isActive ? 'Active' : 'Inactive'}</td>
                  <td>{fmt.dateTime(entry.createdAt)}</td>
                  <td>
                    <div className="portal-links">
                      <button className="btn" type="button" disabled={busy} onClick={() => onToggleEntry(entry)}>
                        {entry.isActive ? 'Deactivate' : 'Activate'}
                      </button>
                      <button className="btn" type="button" disabled={busy} onClick={() => onRename(entry)}>
                        Rename
                      </button>
                      <button className="btn" type="button" disabled={busy} onClick={() => onRemove(entry)}>
                        Remove
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </div>
  )
}
