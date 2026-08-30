import { useMemo, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import {
  useExpirations,
  useFlagReview,
  useRecents,
  useRunbookRuns,
  type ExpirationItem,
  type FlagReviewItem,
  type RecentItem,
  type RunbookRunRollup,
} from '../hooks/useApi'

const widgetTake = 10
const favoriteFlagName = 'favorite'

function hrefFor(entityType: string, id: string) {
  switch (entityType) {
    case 'Asset':
      return '/assets'
    case 'Document':
      return '/documents'
    case 'Runbook':
      return '/runbooks'
    case 'Company':
      return `/companies/${id}`
    case 'KeeperLink':
      return '/keeper'
    default:
      return '/'
  }
}

function formatWhen(iso?: string | null) {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toISOString().slice(0, 10)
}

function daysLabel(days: number) {
  if (days < 0) return `${days}d`
  if (days === 0) return 'today'
  return `${days}d`
}

function daysClass(days: number) {
  if (days < 0) return 'days-expired'
  if (days <= 14) return 'days-soon'
  return 'days-ok'
}

function pickFavorites(review: FlagReviewItem[]) {
  const named = review.filter((item) => item.flagName.toLowerCase() === favoriteFlagName)
  return (named.length > 0 ? named : review).slice(0, widgetTake)
}

export function DashboardPage() {
  const { data: favoriteData, error: favoriteError, isLoading: favoriteLoading } = useFlagReview()
  const { data: recentData, error: recentError, isLoading: recentLoading } = useRecents()
  const { data: taskData, error: taskError, isLoading: taskLoading } = useRunbookRuns({ status: 'Running' })
  const { data: expirationData, error: expirationError, isLoading: expirationLoading } = useExpirations()

  const favorites = useMemo(
    () => pickFavorites(Array.isArray(favoriteData) ? favoriteData : []),
    [favoriteData],
  )
  const recents = useMemo(
    () => (Array.isArray(recentData) ? recentData : []).slice(0, widgetTake),
    [recentData],
  )
  const tasks = useMemo(
    () => (Array.isArray(taskData) ? taskData : []).slice(0, widgetTake),
    [taskData],
  )
  const expirations = useMemo(
    () => (Array.isArray(expirationData) ? expirationData : []).slice(0, widgetTake),
    [expirationData],
  )

  return (
    <div className="page">
      <h1>Dashboard</h1>
      <p>Your workspace — favorites, recents, running runbooks, and upcoming expirations.</p>
      <div className="card-grid dashboard-widgets">
        <Widget
          title="My Favorites"
          to="/flags"
          loading={favoriteLoading}
          error={favoriteError}
          empty={favorites.length === 0 ? 'Nothing flagged. Assign a Favorite flag, or any flag, to pin items here.' : null}
        >
          {favorites.length > 0 && (
            <ul className="widget-list">
              {favorites.map((item) => (
                <FavoriteRow key={item.assignmentId} item={item} />
              ))}
            </ul>
          )}
        </Widget>
        <Widget
          title="My Recents"
          loading={recentLoading}
          error={recentError}
          empty={recents.length === 0 ? 'No recent assets, documents, or runbooks.' : null}
        >
          {recents.length > 0 && (
            <ul className="widget-list">
              {recents.map((item) => (
                <RecentRow key={`${item.entityType}-${item.id}`} item={item} />
              ))}
            </ul>
          )}
        </Widget>
        <Widget
          title="My Tasks"
          to="/runs?status=Running"
          loading={taskLoading}
          error={taskError}
          empty={tasks.length === 0 ? 'No running runbooks.' : null}
        >
          {tasks.length > 0 && (
            <ul className="widget-list">
              {tasks.map((item) => (
                <TaskRow key={item.id} item={item} />
              ))}
            </ul>
          )}
        </Widget>
        <Widget
          title="Expiring Soon"
          to="/expirations"
          loading={expirationLoading}
          error={expirationError}
          empty={expirations.length === 0 ? 'No upcoming expirations.' : null}
        >
          {expirations.length > 0 && (
            <ul className="widget-list">
              {expirations.map((item) => (
                <ExpirationRow key={`${item.sourceType}-${item.id}`} item={item} />
              ))}
            </ul>
          )}
        </Widget>
      </div>
    </div>
  )
}

function Widget({
  title,
  to,
  loading,
  error,
  empty,
  children,
}: {
  title: string
  to?: string
  loading: boolean
  error: unknown
  empty: string | null
  children?: ReactNode
}) {
  return (
    <div className="card">
      <h3>{to ? <Link to={to}>{title}</Link> : title}</h3>
      {loading && <p>Loading…</p>}
      {error ? <p className="error">Failed to load.</p> : null}
      {!loading && !error && empty && <p>{empty}</p>}
      {children}
    </div>
  )
}

function FavoriteRow({ item }: { item: FlagReviewItem }) {
  return (
    <li>
      <Link to={hrefFor(item.entityType, item.entityId)}>{item.entityName}</Link>
      <span className="widget-meta">{item.flagName} · {item.entityType}</span>
    </li>
  )
}

function RecentRow({ item }: { item: RecentItem }) {
  return (
    <li>
      <Link to={hrefFor(item.entityType, item.id)}>{item.name}</Link>
      <span className="widget-meta">{item.entityType} · {formatWhen(item.updatedAt)}</span>
    </li>
  )
}

function TaskRow({ item }: { item: RunbookRunRollup }) {
  return (
    <li>
      <Link to={`/runs?status=${encodeURIComponent(item.status)}`}>{item.runbookTitle}</Link>
      <span className="widget-meta">{item.companyName ?? '—'} · {formatWhen(item.startedAt)}</span>
    </li>
  )
}

function ExpirationRow({ item }: { item: ExpirationItem }) {
  return (
    <li>
      <Link to="/expirations">{item.name}</Link>
      <span className={`widget-meta ${daysClass(item.daysUntil)}`}>
        {item.fieldName} · {daysLabel(item.daysUntil)}
      </span>
    </li>
  )
}
