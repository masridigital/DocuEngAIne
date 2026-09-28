import { Fragment, useState } from 'react'
import {
  ARCHIVE_RESOURCE_TYPES,
  canManageUsers,
  permanentlyDeleteArchiveEntry,
  restoreArchiveEntry,
  useArchive,
  useProfile,
  useTerms,
  type ArchiveEntry,
  type ArchiveResourceType,
  type ArchiveState,
} from '../hooks/useApi'

function formatTimestamp(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

function useTypeLabel() {
  const term = useTerms()
  return (type: ArchiveResourceType | string) => {
    switch (type) {
      case 'Company':
        return term('company', 'singular')
      case 'Asset':
        return term('asset', 'singular')
      case 'Document':
        return term('document', 'singular')
      case 'Runbook':
        return term('runbook', 'singular')
      case 'KeeperLink':
        return 'Keeper link'
      default:
        return type
    }
  }
}

function stateText(e: ArchiveEntry) {
  if (e.state === 'restored') return `Restored ${formatTimestamp(e.restoredAt)}`
  if (e.state === 'deleted') return `Deleted ${formatTimestamp(e.permanentlyDeletedAt)}`
  return 'Archived'
}

function itemCount(n: number) {
  return `${n} item${n === 1 ? '' : 's'}`
}

type Actions = {
  isAdmin: boolean
  busyId: string | null
  onRestore: (entry: ArchiveEntry) => void
  onPermanentDelete: (entry: ArchiveEntry) => void
}

export function MuseumPage() {
  const term = useTerms()
  const typeLabel = useTypeLabel()
  const { data: profile } = useProfile()
  const isAdmin = canManageUsers(profile?.role)

  const [state, setState] = useState<ArchiveState>('archived')
  const [resourceType, setResourceType] = useState<ArchiveResourceType | undefined>(undefined)
  const [page, setPage] = useState(1)
  const [openId, setOpenId] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)

  const { data, error, isLoading } = useArchive({ state, resourceType, page })
  const entries = data?.items ?? []
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1

  async function run(entry: ArchiveEntry, action: () => Promise<unknown>, done: string) {
    setMessage(null)
    setErrorMessage(null)
    setBusyId(entry.id)
    try {
      await action()
      setMessage(done)
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusyId(null)
    }
  }

  function onRestore(entry: ArchiveEntry) {
    const done =
      entry.resourceType === 'Company' && entry.items
        ? `Restored "${entry.resourceLabel}" with what was archived with it.`
        : `Restored "${entry.resourceLabel}".`
    return run(entry, () => restoreArchiveEntry(entry.id), done)
  }

  function onPermanentDelete(entry: ArchiveEntry) {
    const what =
      entry.resourceType === 'Company'
        ? `"${entry.resourceLabel}" and every record it still has, archived with it or before`
        : `"${entry.resourceLabel}"`
    if (!window.confirm(`Permanently delete ${what}? This cannot be undone.`)) return
    if (window.prompt('Type DELETE to confirm.') !== 'DELETE') return
    return run(entry, () => permanentlyDeleteArchiveEntry(entry.id), `Permanently deleted "${entry.resourceLabel}".`)
  }

  const actions: Actions = { isAdmin, busyId, onRestore, onPermanentDelete }

  return (
    <div className="page">
      <h1>Museum</h1>
      <p>
        Archived {term('company').toLowerCase()}, {term('asset').toLowerCase()}, {term('document').toLowerCase()},{' '}
        {term('runbook').toLowerCase()} and Keeper links. Archived items are hidden everywhere else and sync leaves them
        alone, but they can be restored. Everything in an archived {term('company', 'singular').toLowerCase()} is archived
        with it: open its contents to see them. They come back, or go for good, with it. Permanent deletion is Admin-only
        and leaves a tombstone here for the record.
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
          {ARCHIVE_RESOURCE_TYPES.map((t) => (
            <option key={t} value={t}>{typeLabel(t)}</option>
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
                <Fragment key={e.id}>
                  <tr>
                    <td>{typeLabel(e.resourceType)}</td>
                    <td>
                      {e.resourceLabel}
                      {e.resourceType === 'Company' && (
                        <div className="muted">
                          {itemCount(e.items ?? 0)} archived with it{' '}
                          {(e.items ?? 0) > 0 && (
                            <button
                              className="btn btn-secondary"
                              type="button"
                              onClick={() => setOpenId(openId === e.id ? null : e.id)}
                            >
                              {openId === e.id ? 'Hide contents' : 'Contents'}
                            </button>
                          )}
                        </div>
                      )}
                    </td>
                    <td>{formatTimestamp(e.archivedAt)}</td>
                    <td>{e.archivedByName || e.archivedByObjectId || '—'}</td>
                    <td>{e.reason ?? '—'}</td>
                    <td>{stateText(e)}</td>
                    <td>
                      <EntryActions entry={e} actions={actions} />
                    </td>
                  </tr>
                  {openId === e.id && (
                    <tr>
                      <td colSpan={7}>
                        <CompanyContents parent={e} actions={actions} />
                      </td>
                    </tr>
                  )}
                </Fragment>
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

function EntryActions({ entry, actions }: { entry: ArchiveEntry; actions: Actions }) {
  if (entry.state !== 'archived') return null
  return (
    <>
      <button className="btn" disabled={actions.busyId === entry.id} onClick={() => actions.onRestore(entry)}>
        Restore
      </button>
      {actions.isAdmin && (
        <button className="btn" disabled={actions.busyId === entry.id} onClick={() => actions.onPermanentDelete(entry)}>
          Delete permanently
        </button>
      )}
    </>
  )
}

/**
 * What was archived with a company. These come back with the company, so while it is archived
 * they cannot be restored one by one; an Admin can still destroy one for good.
 */
function CompanyContents({ parent, actions }: { parent: ArchiveEntry; actions: Actions }) {
  const typeLabel = useTypeLabel()
  const [page, setPage] = useState(1)
  const { data, error, isLoading } = useArchive({ state: 'all', parentId: parent.id, page })
  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1
  const companyArchived = parent.state === 'archived'

  if (isLoading) return <p>Loading…</p>
  if (error) return <p className="error">Failed to load what was archived with it.</p>
  if (!data) return null

  return (
    <div className="panel">
      <p className="muted">
        Archived with {parent.resourceLabel}
        {companyArchived ? '. Restoring the company brings these back.' : '.'}
      </p>
      <table className="data-table">
        <thead>
          <tr>
            <th>Type</th>
            <th>Name</th>
            <th>State</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {data.items.map((e) => (
            <tr key={e.id}>
              <td>{typeLabel(e.resourceType)}</td>
              <td>{e.resourceLabel}</td>
              <td>{stateText(e)}</td>
              <td>
                {e.state === 'archived' && companyArchived && actions.isAdmin && (
                  <button className="btn" disabled={actions.busyId === e.id} onClick={() => actions.onPermanentDelete(e)}>
                    Delete permanently
                  </button>
                )}
                {e.state === 'archived' && !companyArchived && <EntryActions entry={e} actions={actions} />}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {totalPages > 1 && (
        <div className="toolbar">
          <button disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</button>
          <span>
            page {data.page} of {totalPages}
          </span>
          <button disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>Next</button>
        </div>
      )}
    </div>
  )
}
