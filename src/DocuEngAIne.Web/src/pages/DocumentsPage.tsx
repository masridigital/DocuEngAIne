import { useDocuments } from '../hooks/useApi'

export function DocumentsPage() {
  const { data: docs, error, isLoading } = useDocuments()

  return (
    <div className="page">
      <h1>Documents</h1>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load documents.</p>}
      {docs && (
        <div className="list">
          {docs.map((d: any) => (
            <article key={d.id} className="list-item">
              <h3>{d.title}</h3>
              <p>{d.summary}</p>
              {d.tags && <span className="tag">{d.tags}</span>}
            </article>
          ))}
        </div>
      )}
    </div>
  )
}
