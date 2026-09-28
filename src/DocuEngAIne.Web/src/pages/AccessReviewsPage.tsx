import { useState, type FormEvent } from 'react'
import { FeatureOff } from '../components/FeatureOff'
import {
  cancelAccessReview,
  canManageUsers,
  completeAccessReview,
  createAccessReview,
  decideAccessReviewItem,
  exportAccessReviewCsv,
  featureEnabled,
  startAccessReview,
  useAccessReview,
  useAccessReviews,
  useProfile,
  useTenantConfiguration,
  USER_ROLES,
  type AccessReviewItem,
  type UserRole,
} from '../hooks/useApi'

const STATUS_LABELS: Record<string, string> = {
  Draft: 'Draft',
  InProgress: 'In progress',
  Completed: 'Completed',
  Cancelled: 'Cancelled',
}

function formatTimestamp(value?: string | null) {
  if (!value) return '—'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return date.toLocaleString()
}

function DecisionControls(props: {
  reviewId: string
  item: AccessReviewItem
  isSelf: boolean
  onError: (message: string) => void
}) {
  const { reviewId, item, isSelf, onError } = props
  const [role, setRole] = useState<UserRole>(item.roleAtSnapshot)
  const [busy, setBusy] = useState(false)

  async function decide(decision: 'Retain' | 'Revoke' | 'ChangeRole') {
    if (decision === 'Revoke' && !window.confirm(`Revoke ${item.subjectEmail}? They are suspended immediately.`)) return
    const notes = window.prompt('Decision notes (optional):', '') ?? undefined
    setBusy(true)
    try {
      await decideAccessReviewItem(reviewId, item.id, {
        decision,
        requestedRole: decision === 'ChangeRole' ? role : undefined,
        notes,
      })
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Decision failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="portal-links">
      <button className="btn" type="button" disabled={busy} onClick={() => void decide('Retain')}>
        Retain
      </button>
      {!isSelf && (
        <>
          <button className="btn" type="button" disabled={busy} onClick={() => void decide('Revoke')}>
            Revoke
          </button>
          <select className="input" value={role} disabled={busy} onChange={(e) => setRole(e.target.value as UserRole)}>
            {USER_ROLES.map((r) => (
              <option key={r} value={r}>{r}</option>
            ))}
          </select>
          <button
            className="btn"
            type="button"
            disabled={busy || role === item.roleAtSnapshot}
            onClick={() => void decide('ChangeRole')}
          >
            Change role
          </button>
        </>
      )}
    </div>
  )
}

function ReviewDetail(props: { id: string; currentUserId?: string; onClose: () => void }) {
  const { data, error, isLoading } = useAccessReview(props.id)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function act(action: () => Promise<void>) {
    setErrorMessage(null)
    setBusy(true)
    try {
      await action()
    } catch (err) {
      setErrorMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusy(false)
    }
  }

  if (isLoading) return <p>Loading…</p>
  if (error || !data) return <p className="error">Failed to load the review.</p>

  const { review, items } = data
  const pending = review.pendingCount

  return (
    <div className="panel">
      <h2>
        {review.name} <span className="badge">{STATUS_LABELS[review.status] ?? review.status}</span>{' '}
        <button onClick={props.onClose}>Close</button>
      </h2>
      {errorMessage && <p className="error">{errorMessage}</p>}
      {data.notes ? <p>{data.notes}</p> : null}
      <p className="muted">
        Started {formatTimestamp(review.startedAt)} · {review.itemCount} user(s) · {pending} pending
        {review.dueAt ? ` · due ${formatTimestamp(review.dueAt)}` : ''}
      </p>

      <div className="toolbar">
        {review.status === 'Draft' && (
          <button className="btn" disabled={busy} onClick={() => void act(() => startAccessReview(review.id))}>
            Start (snapshot current access)
          </button>
        )}
        {review.status === 'InProgress' && (
          <button
            className="btn"
            disabled={busy || pending > 0}
            title={pending > 0 ? 'Every user must be decided first.' : undefined}
            onClick={() => void act(() => completeAccessReview(review.id))}
          >
            Complete
          </button>
        )}
        {(review.status === 'Draft' || review.status === 'InProgress') && (
          <button
            className="btn btn-secondary"
            disabled={busy}
            onClick={() => {
              if (window.confirm('Cancel this review? Decisions already applied stay applied.'))
                void act(() => cancelAccessReview(review.id))
            }}
          >
            Cancel review
          </button>
        )}
        {review.status !== 'Draft' && (
          <button className="btn btn-secondary" disabled={busy} onClick={() => void act(() => exportAccessReviewCsv(review.id))}>
            Export evidence (CSV)
          </button>
        )}
      </div>

      {review.status === 'Draft' ? (
        <p>Starting the review snapshots every active user's role and per-resource grants as they are right now.</p>
      ) : (
        <table className="data-table">
          <thead>
            <tr>
              <th>User</th>
              <th>Role at start</th>
              <th>Grants</th>
              <th>Decision</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={item.id}>
                <td>
                  {item.subjectName || item.subjectEmail}
                  {item.subjectName ? <div className="muted">{item.subjectEmail}</div> : null}
                </td>
                <td>{item.roleAtSnapshot}</td>
                <td>{item.grantCount}</td>
                <td>
                  {item.decision === 'Pending' ? (
                    'Pending'
                  ) : (
                    <>
                      {item.decision === 'ChangeRole' ? `Role → ${item.requestedRole}` : item.decision}
                      <div className="muted">
                        {item.decidedByName ?? '—'} · {formatTimestamp(item.decidedAt)}
                      </div>
                      {item.decisionNotes ? <div className="muted">{item.decisionNotes}</div> : null}
                    </>
                  )}
                </td>
                <td>
                  {review.status === 'InProgress' && item.decision === 'Pending' && (
                    <DecisionControls
                      reviewId={review.id}
                      item={item}
                      isSelf={item.subjectUserId === props.currentUserId}
                      onError={setErrorMessage}
                    />
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

export function AccessReviewsPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const { data: configuration, isLoading: configurationLoading } = useTenantConfiguration()
  const allowed = canManageUsers(profile?.role)
  const reviewsOn = featureEnabled(configuration, 'access_reviews')
  const { data, error, isLoading } = useAccessReviews(allowed && reviewsOn && !configurationLoading)
  const reviews = Array.isArray(data) ? data : []

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [dueAt, setDueAt] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setFormError(null)
    setSubmitting(true)
    try {
      const created = await createAccessReview({ name: name.trim(), dueAt: dueAt || undefined })
      setName('')
      setDueAt('')
      setSelectedId(created.id)
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to create the review.')
    } finally {
      setSubmitting(false)
    }
  }

  if (profileLoading || configurationLoading) {
    return (
      <div className="page">
        <h1>Access reviews</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!reviewsOn) return <FeatureOff title="Access reviews" name="Access reviews" />

  if (!allowed) {
    return (
      <div className="page">
        <h1>Access reviews</h1>
        <p>Access reviews are available to Admin and Owner.</p>
      </div>
    )
  }

  return (
    <div className="page">
      <h1>Access reviews</h1>
      <p>
        Periodic certification of who has access to this tenant. Starting a review snapshots every
        active user; each must then be retained, revoked (suspended immediately) or moved to a new
        role before the review can be completed. The CSV export is the audit evidence.
      </p>

      <form className="toolbar" onSubmit={onCreate}>
        <input placeholder="Review name, e.g. Q4 access review" value={name} onChange={(e) => setName(e.target.value)} />
        <label>
          Due <input type="date" value={dueAt} onChange={(e) => setDueAt(e.target.value)} />
        </label>
        <button className="btn" type="submit" disabled={submitting || !name.trim()}>
          {submitting ? 'Creating…' : 'New review'}
        </button>
      </form>
      {formError && <p className="error">{formError}</p>}

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load access reviews.</p>}

      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Status</th>
              <th>Users</th>
              <th>Pending</th>
              <th>Due</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {reviews.length === 0 && (
              <tr>
                <td colSpan={6}>No access reviews yet.</td>
              </tr>
            )}
            {reviews.map((r) => (
              <tr key={r.id} onClick={() => setSelectedId(r.id)} style={{ cursor: 'pointer' }}>
                <td>{r.name}</td>
                <td>{STATUS_LABELS[r.status] ?? r.status}</td>
                <td>{r.itemCount}</td>
                <td>{r.pendingCount}</td>
                <td>{formatTimestamp(r.dueAt)}</td>
                <td>{formatTimestamp(r.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {selectedId && (
        <ReviewDetail id={selectedId} currentUserId={profile?.id} onClose={() => setSelectedId(null)} />
      )}
    </div>
  )
}
