import { useState } from 'react'
import {
  canManageUsers,
  permanentlyDeleteArchiveEntry,
  restoreArchiveEntry,
  useArchive,
  useProfile,
  type ArchiveEntry,
  type ArchiveResourceType,
  type ArchiveState,
} from '../hooks/useApi'

const TYPE_LABELS: Record<ArchiveResourceType, string> = {
  Asset: 'Asset',
  Document: 'Document',
  Runbook: 'Runbook',
  KeeperLink: 'Keeper link',
}

function formatTimestamp(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

export function MuseumPage() {
  const { data: profile } = useProfile()
  const isAdmin = canManageUsers(profile?.role)

  const [state, setState] = useState<ArchiveState>('archived')
  const [resourceType, setResourceType] = useState<ArchiveResourceType | undefined>(undefined)
  const [page, setPage] = useState(1)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const { data, error, isLoading, mutate } = useArchive({ state, resourceType, page })
  const entries = data?.items ?? []
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  async function run(entry: ArchiveEntry, action: () => Promise<unknown>, done: string) {
    setMessage(null)
    setErrorMessage(null)
    setBusyId(entry.id)
    try {
      await action()
      setMessage(done)
      await mutate()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusyId(null)
    }
  }

  function onRestore(entry: ArchiveEntry) {
    return run(entry, () => restoreArchiveEntry(entry.id), `Restored "${entry.resourceLabel}".`)
  }

  function onPermanentDelete(entry: ArchiveEntry) {
    if (!window.confirm(`Permanently delete "${entry.resourceLabel}"? This cannot be undone.`)) return
    if (window.prompt('Type DELETE to confirm.') !== 'DELETE') return
    return run(entry, () => permanentlyDeleteArchiveEntry(entry.id), `Permanently deleted "${entry.resourceLabel}".`)
  }

  return (
    <div className="page">
      <h1>Museum</h1>
      <p>
        Archived assets, documents, runbooks and Keeper links. Archived items are hidden everywhere else
        but can be restored. Permanent deletion is Admin-only and leaves a tombstone here for the record.
      </p>
      {message && <p className="banner">{message}</p>}
      {errorMessage && <p className="error">{errorMessage}</p>}

      <div className="toolbar">
        <select
          value={state}
          onChange={(e) => {
            setState(e.target.value as ArchiveState)
            setPage(1)
          }}
        >
          <option value="archived">Archived</option>
          <option value="restored">Restored</option>
          <option value="deleted">Permanently deleted</option>
          <option value="all">All</option>
        </select>
        <select
          value={resourceType ?? ''}
          onChange={(e) => {
            setResourceType((e.target.value || undefined) as ArchiveResourceType | undefined)
            setPage(1)
          }}
        >
          <option value="">All types</option>
          {(Object.keys(TYPE_LABELS) as ArchiveResourceType[]).map((t) => (
            <option key={t} value={t}>{TYPE_LABELS[t]}</option>
          ))}
        </select>
      </div>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load the Museum.</p>}

      {data && (
        <>
          <p>
            {data.total} item{data.total === 1 ? '' : 's'} · page {data.page} of {totalPages}
          </p>
          <table className="data-table">
            <thead>
              <tr>
                <th>Type</th>
                <th>Name</th>
                <th>Archived</th>
                <th>By</th>
                <th>Reason</th>
                <th>State</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {entries.length === 0 && (
                <tr>
                  <td colSpan={7}>Nothing here.</td>
                </tr>
              )}
              {entries.map((e) => (
                <tr key={e.id}>
                  <td>{TYPE_LABELS[e.resourceType] ?? e.resourceType}</td>
                  <td>{e.resourceLabel}</td>
                  <td>{formatTimestamp(e.archivedAt)}</td>
                  <td>{e.archivedByName || e.archivedByObjectId || '—'}</td>
                  <td>{e.reason ?? '—'}</td>
                  <td>
                    {e.state === 'restored'
                      ? `Restored ${formatTimestamp(e.restoredAt)}`
                      : e.state === 'deleted'
                        ? `Deleted ${formatTimestamp(e.permanentlyDeletedAt)}`
                        : 'Archived'}
                  </td>
                  <td>
                    {e.state === 'archived' && (
                      <>
                        <button className="btn" disabled={busyId === e.id} onClick={() => onRestore(e)}>
                          Restore
                        </button>
                        {isAdmin && (
                          <button className="btn" disabled={busyId === e.id} onClick={() => onPermanentDelete(e)}>
                            Delete permanently
                          </button>
                        )}
                      </>
                    )}
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
