import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { promoteRunbookRun, useCompanies, useRunbookRuns, type RunbookRunRollup } from '../hooks/useApi'

const statuses = ['', 'Running', 'Completed', 'Cancelled'] as const

function formatWhen(iso?: string | null) {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString()
}

function statusClass(status: string) {
  const key = status.toLowerCase()
  if (key === 'completed') return 'status-completed'
  if (key === 'cancelled') return 'status-cancelled'
  return 'status-running'
}

export function RunsPage() {
  const [params, setParams] = useSearchParams()
  const [status, setStatus] = useState(params.get('status') ?? '')
  const [companyId, setCompanyId] = useState(params.get('companyId') ?? '')

  const { data: companyData } = useCompanies()
  const companies = Array.isArray(companyData) ? companyData : []

  const { data, error, isLoading, mutate } = useRunbookRuns({
    status: status || undefined,
    companyId: companyId || undefined,
  })
  const items: RunbookRunRollup[] = Array.isArray(data) ? data : []
  const [promotingId, setPromotingId] = useState<string | null>(null)
  const [promotedIds, setPromotedIds] = useState<Record<string, string>>({})
  const [actionError, setActionError] = useState<string | null>(null)

  const countLabel = useMemo(() => {
    const n = items.length
    return n === 1 ? '1 run' : `${n} runs`
  }, [items.length])

  function updateFilter(next: { status?: string; companyId?: string }) {
    const nextStatus = next.status ?? status
    const nextCompany = next.companyId ?? companyId
    setStatus(nextStatus)
    setCompanyId(nextCompany)
    const nextParams = new URLSearchParams()
    if (nextStatus) nextParams.set('status', nextStatus)
    if (nextCompany) nextParams.set('companyId', nextCompany)
    setParams(nextParams, { replace: true })
  }

  async function onPromote(item: RunbookRunRollup) {
    setActionError(null)
    setPromotingId(item.id)
    try {
      const doc = await promoteRunbookRun(item.runbookId, item.id)
      setPromotedIds((prev) => ({ ...prev, [item.id]: doc.id }))
      await mutate()
    } catch (err) {
      setActionError(err instanceof Error ? err.message : 'Failed to promote run.')
    } finally {
      setPromotingId(null)
    }
  }

  return (
    <div className="page">
      <h1>Process completion</h1>
      <p>
        Recent runbook runs across companies. Start a run from a{' '}
        <Link to="/runbooks">runbook</Link>; complete or cancel it from the API. Promote a completed run into a Document. Not a second process product.
      </p>

      <div className="toolbar flags-toolbar">
        <label>
          Status
          <select
            className="input"
            value={status}
            onChange={(e) => updateFilter({ status: e.target.value })}
          >
            {statuses.map((s) => (
              <option key={s || 'all'} value={s}>{s || 'All statuses'}</option>
            ))}
          </select>
        </label>
        <label>
          Company
          <select
            className="input"
            value={companyId}
            onChange={(e) => updateFilter({ companyId: e.target.value })}
          >
            <option value="">All companies</option>
            {companies.map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>
        </label>
        <span className="muted">{countLabel}</span>
      </div>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load runs.</p>}
      {actionError && <p className="error">{actionError}</p>}
      {!isLoading && !error && items.length === 0 && (
        <p>No runs match. Start a run from a runbook to track a pass through the steps.</p>
      )}
      {items.length > 0 && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Runbook</th>
              <th>Company</th>
              <th>Status</th>
              <th>Started</th>
              <th>Finished</th>
              <th>Document</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={item.id}>
                <td>
                  <Link to="/runbooks">{item.runbookTitle}</Link>
                </td>
                <td>
                  {item.companyId ? (
                    <Link to={`/companies/${item.companyId}`}>{item.companyName ?? '—'}</Link>
                  ) : (
                    item.companyName ?? '—'
                  )}
                </td>
                <td className={statusClass(item.status)}>{item.status}</td>
                <td>{formatWhen(item.startedAt)}</td>
                <td>{formatWhen(item.finishedAt)}</td>
                <td className="row-actions">
                  {item.status.toLowerCase() === 'completed' && (
                    promotedIds[item.id] ? (
                      <Link to="/documents">Document</Link>
                    ) : (
                      <button
                        className="btn btn-secondary"
                        type="button"
                        disabled={promotingId === item.id}
                        onClick={() => onPromote(item)}
                      >
                        {promotingId === item.id ? 'Promoting…' : 'Promote'}
                      </button>
                    )
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
