import { useMemo, useState } from 'react'
import { useExpirations, type ExpirationItem } from '../hooks/useApi'

function formatDate(iso: string) {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toISOString().slice(0, 10)
}

function daysClass(days: number) {
  if (days < 0) return 'days-expired'
  if (days <= 14) return 'days-soon'
  return 'days-ok'
}

function daysLabel(days: number) {
  if (days < 0) return `${days}d`
  if (days === 0) return 'today'
  return `${days}d`
}

export function ExpirationsPage() {
  const [query, setQuery] = useState('')
  const [showExpired, setShowExpired] = useState(false)
  const { data, error, isLoading } = useExpirations({ q: query, showExpired })
  const items: ExpirationItem[] = Array.isArray(data) ? data : []

  const countLabel = useMemo(() => {
    const n = items.length
    return n === 1 ? '1 expiration' : `${n} expirations`
  }, [items.length])

  return (
    <div className="page">
      <h1>Expirations</h1>
      <p>Warranty, license, SSL, and contract dates across companies. Date fields marked as expirations plus any asset shortcut date.</p>

      <div className="toolbar expirations-toolbar">
        <input
          className="input"
          type="search"
          placeholder="Search name, company, or type…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <label className="check-label">
          <input
            type="checkbox"
            checked={showExpired}
            onChange={(e) => setShowExpired(e.target.checked)}
          />
          Show expired
        </label>
        <span className="muted">{countLabel}</span>
      </div>

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load expirations.</p>}
      {!isLoading && !error && items.length === 0 && (
        <p>No expirations match. Mark a date field as an expiration or set an asset expires-at date.</p>
      )}
      {items.length > 0 && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Company</th>
              <th>Type</th>
              <th>Date</th>
              <th>Days</th>
            </tr>
          </thead>
          <tbody>
            {items.map((item) => (
              <tr key={`${item.sourceType}-${item.id}`}>
                <td>{item.name}</td>
                <td>{item.companyName ?? '—'}</td>
                <td>{item.fieldName}</td>
                <td>{formatDate(item.expiresAt)}</td>
                <td className={daysClass(item.daysUntil)}>{daysLabel(item.daysUntil)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
