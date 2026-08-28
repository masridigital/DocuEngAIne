import { useState } from 'react'
import { revealKeeperLink, useKeeperLinks } from '../hooks/useApi'

export function KeeperPage() {
  const { data: links, error, isLoading } = useKeeperLinks()
  const [revealError, setRevealError] = useState<string | null>(null)

  return (
    <div className="page">
      <h1>Keeper Links</h1>
      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load Keeper links.</p>}
      {revealError && <p className="error">{revealError}</p>}
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
                  try {
                    const data = await revealKeeperLink(l.id)
                    if (data?.keeperRecordUrl) {
                      window.open(data.keeperRecordUrl, '_blank', 'noopener,noreferrer')
                    } else {
                      setRevealError('That Keeper link has no record URL.')
                    }
                  } catch (err) {
                    setRevealError(err instanceof Error ? err.message : 'Could not open the Keeper record.')
                  }
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
