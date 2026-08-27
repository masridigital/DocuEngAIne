import { useKeeperLinks } from '../hooks/useApi'

export function KeeperPage() {
  const { data: links, error, isLoading } = useKeeperLinks()

  return (
    <div className="page">
      <h1>Keeper Links</h1>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load Keeper links.</p>}
      {links && (
        <div className="list">
          {links.map((l: any) => (
            <article key={l.id} className="list-item">
              <h3>{l.name}</h3>
              {l.usernameHint && <p>Username hint: {l.usernameHint}</p>}
              <a
                href="#"
                onClick={async (e) => {
                  e.preventDefault()
                  const res = await fetch(`/api/keeper/${l.id}/reveal`, { method: 'POST' })
                  const data = await res.json()
                  window.open(data.keeperRecordUrl, '_blank', 'noopener,noreferrer')
                }}
              >
                Open in Keeper
              </a>
            </article>
          ))}
        </div>
      )}
    </div>
  )
}
