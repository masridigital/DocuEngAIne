import { useMemo, useState, type FormEvent } from 'react'
import {
  createFlag,
  useFlagReview,
  useFlags,
  type FlagDefinition,
  type FlagReviewItem,
} from '../hooks/useApi'

const entityTypes = ['', 'Company', 'Asset', 'Document', 'Runbook', 'KeeperLink'] as const

function formatWhen(iso: string) {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString()
}

function FlagChip({ name, color }: { name: string; color: string }) {
  return (
    <span className="flag-chip">
      <span className="flag-swatch" style={{ background: color }} />
      {name}
    </span>
  )
}

export function FlagsPage() {
  const { data, error, isLoading, mutate } = useFlags()
  const flags: FlagDefinition[] = Array.isArray(data) ? data : []

  const [name, setName] = useState('')
  const [color, setColor] = useState('#dc2626')
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const [entityType, setEntityType] = useState('')
  const { data: reviewData, error: reviewError, isLoading: reviewLoading } = useFlagReview(entityType || undefined)
  const review: FlagReviewItem[] = Array.isArray(reviewData) ? reviewData : []

  const countLabel = useMemo(() => {
    const n = review.length
    return n === 1 ? '1 flagged record' : `${n} flagged records`
  }, [review.length])

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    const trimmed = name.trim()
    if (!trimmed) {
      setFormError('Name is required.')
      return
    }
    setSubmitting(true)
    try {
      await createFlag({ name: trimmed, color })
      setName('')
      setColor('#dc2626')
      await mutate()
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to create flag.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="page">
      <h1>Flags</h1>
      <p>Named color labels for review queues. Apply them to companies, assets, documents, runbooks, and Keeper links.</p>

      <section className="panel">
        <h2>Definitions</h2>
        <form className="form-grid" onSubmit={onCreate}>
          <label>
            Name
            <input className="input" value={name} onChange={(e) => setName(e.target.value)} placeholder="Needs Review" />
          </label>
          <label>
            Color
            <input className="input" type="color" value={color} onChange={(e) => setColor(e.target.value)} />
          </label>
          <button className="btn" type="submit" disabled={submitting}>
            {submitting ? 'Saving…' : 'Create flag'}
          </button>
        </form>
        {formError && <p className="error">{formError}</p>}

        {isLoading && <p>Loading…</p>}
        {error && <p className="error">Failed to load flags.</p>}
        {!isLoading && !error && flags.length === 0 && <p>No flags yet. Create Critical, Break-Glass, Needs Review, or Compliance as you need them.</p>}
        {flags.length > 0 && (
          <ul className="flag-list">
            {flags.map((flag) => (
              <li key={flag.id}>
                <FlagChip name={flag.name} color={flag.color} />
                {!flag.isActive && <span className="muted">inactive</span>}
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="panel">
        <h2>Review queue</h2>
        <div className="toolbar flags-toolbar">
          <label>
            Type
            <select className="input" value={entityType} onChange={(e) => setEntityType(e.target.value)}>
              {entityTypes.map((t) => (
                <option key={t || 'all'} value={t}>{t || 'All types'}</option>
              ))}
            </select>
          </label>
          <span className="muted">{countLabel}</span>
        </div>

        {reviewLoading && <p>Loading…</p>}
        {reviewError && <p className="error">Failed to load review queue.</p>}
        {!reviewLoading && !reviewError && review.length === 0 && (
          <p>Nothing flagged. Assign a flag from the API to put a record in this queue.</p>
        )}
        {review.length > 0 && (
          <table className="data-table">
            <thead>
              <tr>
                <th>Flag</th>
                <th>Type</th>
                <th>Name</th>
                <th>Company</th>
                <th>Flagged</th>
              </tr>
            </thead>
            <tbody>
              {review.map((item) => (
                <tr key={item.assignmentId}>
                  <td><FlagChip name={item.flagName} color={item.flagColor} /></td>
                  <td>{item.entityType}</td>
                  <td>{item.entityName}</td>
                  <td>{item.companyName ?? '—'}</td>
                  <td>{formatWhen(item.createdAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </div>
  )
}
