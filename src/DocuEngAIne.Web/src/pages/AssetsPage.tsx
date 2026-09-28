import { useState } from 'react'
import { ArchiveButton } from '../components/ArchiveButton'
import { useAssets, type Asset } from '../hooks/useApi'

function isHttpUrl(value?: string | null): value is string {
  if (!value) return false
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

function DeepLinks({ asset }: { asset: Asset }) {
  const links: [string, string][] = []
  if (isHttpUrl(asset.haloAssetUrl)) links.push(['Open in Halo', asset.haloAssetUrl])
  if (isHttpUrl(asset.ninjaDeviceUrl)) links.push(['Open in Ninja', asset.ninjaDeviceUrl])
  if (links.length === 0) return <span>—</span>
  return (
    <div className="portal-links">
      {links.map(([label, href]) => (
        <a key={label} className="btn" href={href} target="_blank" rel="noopener noreferrer">
          {label}
        </a>
      ))}
    </div>
  )
}

export function AssetsPage() {
  const { data, error, isLoading } = useAssets()
  const assets = Array.isArray(data) ? data : []
  const [actionError, setActionError] = useState<string | null>(null)

  return (
    <div className="page">
      <h1>Assets</h1>
      <p className="muted">Open in Halo / Open in Ninja when a device or asset portal URL is stored. URLs only — no secrets.</p>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load assets.</p>}
      {actionError && <p className="error">{actionError}</p>}
      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Type</th>
              <th>Location</th>
              <th>Status</th>
              <th>Links</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {assets.length === 0 && (
              <tr>
                <td colSpan={6}>No assets match.</td>
              </tr>
            )}
            {assets.map((a) => (
              <tr key={a.id}>
                <td>{a.name}</td>
                <td>{a.assetType}</td>
                <td>{a.location}</td>
                <td>{a.status}</td>
                <td>
                  <DeepLinks asset={a} />
                </td>
                <td>
                  <ArchiveButton type="Asset" id={a.id} label={a.name} onError={setActionError} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
