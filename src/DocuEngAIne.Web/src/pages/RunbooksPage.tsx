import { useState } from 'react'
import { Link } from 'react-router-dom'
import { startRunbookRun, useRunbooks, type Runbook } from '../hooks/useApi'

function runsLabel(count: number) {
  return count === 1 ? '1 run' : `${count} runs`
}

export function RunbooksPage() {
  const { data, error, isLoading, mutate } = useRunbooks()
  const runbooks: Runbook[] = Array.isArray(data) ? data : []
  const [startingId, setStartingId] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)

  async function onStart(id: string) {
    setActionError(null)
    setStartingId(id)
    try {
      await startRunbookRun(id)
      await mutate()
    } catch (err) {
      setActionError(err instanceof Error ? err.message : 'Failed to start run.')
    } finally {
      setStartingId(null)
    }
  }

  return (
    <div className="page">
      <h1>Runbooks</h1>
      <p>
        SOPs and checklists. Start a run to track a pass through the steps. Tenant-wide books are templates; company-linked books are per-client.{' '}
        <Link to="/runs">Process completion</Link> rolls up recent runs.
      </p>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load runbooks.</p>}
      {actionError && <p className="error">{actionError}</p>}
      {runbooks.length > 0 && (
        <div className="list">
          {runbooks.map((r) => (
            <article key={r.id} className="list-item">
              <h3>{r.title}</h3>
              <p>{r.description}</p>
              <div className="list-item-meta">
                <span className="muted">{runsLabel(r.runCount ?? 0)}</span>
                <button
                  className="btn"
                  type="button"
                  disabled={startingId === r.id}
                  onClick={() => onStart(r.id)}
                >
                  {startingId === r.id ? 'Starting…' : 'Start run'}
                </button>
              </div>
            </article>
          ))}
        </div>
      )}
      {!isLoading && !error && runbooks.length === 0 && <p>No published runbooks.</p>}
    </div>
  )
}
