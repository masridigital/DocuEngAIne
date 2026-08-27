import { useAssets } from '../hooks/useApi'

export function AssetsPage() {
  const { data: assets, error, isLoading } = useAssets()

  return (
    <div className="page">
      <h1>Assets</h1>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load assets.</p>}
      {assets && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Type</th>
              <th>Location</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {assets.map((a: any) => (
              <tr key={a.id}>
                <td>{a.name}</td>
                <td>{a.assetType}</td>
                <td>{a.location}</td>
                <td>{a.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}
