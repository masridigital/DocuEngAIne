import { useState } from 'react'
import { archiveResource, type ArchiveResourceType } from '../hooks/useApi'

/**
 * Moves one resource to the Museum. Asks for an optional reason (recorded on the archive entry
 * and the audit row); cancelling the prompt cancels the archive.
 */
export function ArchiveButton(props: {
  type: ArchiveResourceType
  id: string
  label: string
  onError?: (message: string) => void
}) {
  const [busy, setBusy] = useState(false)

  async function onClick() {
    const reason = window.prompt(
      `Archive "${props.label}" to the Museum? It disappears from lists but can be restored.\n\nReason (optional):`,
      '',
    )
    if (reason === null) return
    setBusy(true)
    try {
      await archiveResource(props.type, props.id, reason)
    } catch (err) {
      props.onError?.(err instanceof Error ? err.message : 'Archive failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <button className="btn" type="button" disabled={busy} onClick={onClick}>
      {busy ? 'Archiving…' : 'Archive'}
    </button>
  )
}
