import { useRunbooks } from '../hooks/useApi'

export function RunbooksPage() {
  const { data: runbooks, error, isLoading } = useRunbooks()

  return (
    <div className="page">
      <h1>Runbooks</h1>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load runbooks.</p>}
      {runbooks && (
        <div className="list">
          {runbooks.map((r: any) => (
            <article key={r.id} className="list-item">
              <h3>{r.title}</h3>
              <p>{r.description}</p>
            </article>
          ))}
        </div>
      )}
    </div>
  )
}
