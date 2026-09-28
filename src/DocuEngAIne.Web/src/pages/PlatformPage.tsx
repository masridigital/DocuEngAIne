import { useState } from 'react'
import {
  archiveTenant,
  reactivateTenant,
  suspendTenant,
  usePlatformTenants,
  useProfile,
  type PlatformTenant,
} from '../hooks/useApi'

const STATUS_CLASS: Record<PlatformTenant['status'], string> = {
  Active: 'status-completed',
  Suspended: 'status-failed',
  Archived: 'status-cancelled',
}

export function PlatformPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = profile?.isPlatformOperator === true
  const { data, error, isLoading } = usePlatformTenants(allowed)
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  async function act(id: string, action: () => Promise<void>) {
    setMessage(null)
    setBusy(id)
    try {
      await action()
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusy(null)
    }
  }

  function suspend(t: PlatformTenant) {
    const reason = window.prompt(`Why is "${t.name}" being suspended? Its users will see this.`, t.statusReason ?? '')
    if (reason === null) return
    if (!reason.trim()) {
      setMessage('A suspension needs a reason.')
      return
    }
    void act(t.id, () => suspendTenant(t.id, reason.trim()))
  }

  function archive(t: PlatformTenant) {
    if (!window.confirm(`Archive "${t.name}"? Nobody can use it and its sync stops; its data is kept and it can be reactivated.`)) return
    const reason = window.prompt('Reason (optional, shown to its users):', '')
    if (reason === null) return
    void act(t.id, () => archiveTenant(t.id, reason.trim() || undefined))
  }

  function reactivate(t: PlatformTenant) {
    if (!window.confirm(`Reactivate "${t.name}"? Its users, tokens and sync work again at once.`)) return
    void act(t.id, () => reactivateTenant(t.id))
  }

  if (profileLoading) {
    return (
      <div className="page">
        <h1>Platform</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>Platform</h1>
        <p>The platform console is for the operators of this deployment.</p>
      </div>
    )
  }

  const tenants = data ?? []

  return (
    <div className="page">
      <h1>Platform</h1>
      <p>
        Every tenant on this deployment. A suspended or archived tenant is refused on every page and API token, and its
        sync stops; its data is kept, and reactivating restores everything. Each change is recorded in your audit log and
        in the tenant’s own. You cannot close the tenant you are signed in to.
      </p>
      {message && <p className="error">{message}</p>}
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load tenants.</p>}
      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Tenant</th>
              <th>Status</th>
              <th>Active users</th>
              <th>Companies</th>
              <th>Created</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {tenants.length === 0 && (
              <tr>
                <td colSpan={6}>No tenants yet.</td>
              </tr>
            )}
            {tenants.map((t) => (
              <tr key={t.id}>
                <td>
                  {t.name}
                  <div className="muted">{t.primaryDomain || t.slug}</div>
                </td>
                <td>
                  <span className={STATUS_CLASS[t.status]}>{t.status}</span>
                  {t.statusReason ? <div className="muted">{t.statusReason}</div> : null}
                  {t.statusChangedAt ? (
                    <div className="muted">since {new Date(t.statusChangedAt).toLocaleDateString()}</div>
                  ) : null}
                </td>
                <td>{t.activeUsers}</td>
                <td>{t.companies}</td>
                <td>{new Date(t.createdAt).toLocaleDateString()}</td>
                <td className="row-actions">
                  {t.id === profile?.tenant?.id ? (
                    <span className="muted">Your tenant</span>
                  ) : (
                    <>
                      {t.status !== 'Archived' ? (
                        <button className="btn" type="button" disabled={busy !== null} onClick={() => suspend(t)}>
                          {t.status === 'Suspended' ? 'Change reason' : 'Suspend'}
                        </button>
                      ) : null}{' '}
                      {t.status !== 'Archived' ? (
                        <button className="btn btn-secondary" type="button" disabled={busy !== null} onClick={() => archive(t)}>
                          Archive
                        </button>
                      ) : null}{' '}
                      {t.status !== 'Active' ? (
                        <button className="btn" type="button" disabled={busy !== null} onClick={() => reactivate(t)}>
                          Reactivate
                        </button>
                      ) : null}
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
