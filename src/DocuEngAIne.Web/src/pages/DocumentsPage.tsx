import { useMemo, useState, type FormEvent } from 'react'
import {
  createFolder,
  useDocuments,
  useFolders,
  type DocumentFolder,
  type KbDocument,
} from '../hooks/useApi'

type FolderRow = { folder: DocumentFolder; depth: number }

function flattenFolders(folders: DocumentFolder[]): FolderRow[] {
  const byParent = new Map<string, DocumentFolder[]>()
  for (const folder of folders) {
    const key = folder.parentId ?? ''
    const list = byParent.get(key) ?? []
    list.push(folder)
    byParent.set(key, list)
  }
  for (const list of byParent.values()) {
    list.sort((a, b) => a.name.localeCompare(b.name))
  }

  const rows: FolderRow[] = []
  const seen = new Set<string>()

  function walk(parentKey: string, depth: number) {
    for (const folder of byParent.get(parentKey) ?? []) {
      if (seen.has(folder.id)) continue
      seen.add(folder.id)
      rows.push({ folder, depth })
      walk(folder.id, depth + 1)
    }
  }

  walk('', 0)
  for (const folder of folders) {
    if (seen.has(folder.id)) continue
    rows.push({ folder, depth: 0 })
  }
  return rows
}

export function DocumentsPage() {
  const { data: folderData, error: folderError, isLoading: foldersLoading, mutate: mutateFolders } = useFolders()
  const folders: DocumentFolder[] = useMemo(() => (Array.isArray(folderData) ? folderData : []), [folderData])
  const rows = useMemo(() => flattenFolders(folders), [folders])

  const [selectedFolderId, setSelectedFolderId] = useState<string | null>(null)
  const { data: docsData, error, isLoading } = useDocuments({
    folderId: selectedFolderId ?? undefined,
  })
  const docs: KbDocument[] = Array.isArray(docsData) ? docsData : []

  const [name, setName] = useState('')
  const [formError, setFormError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const selected = folders.find((f) => f.id === selectedFolderId)
  const articleHeading = selected ? selected.name : 'All articles'

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    setFormError(null)
    const trimmed = name.trim()
    if (!trimmed) {
      setFormError('Name is required.')
      return
    }
    setSubmitting(true)
    try {
      await createFolder({
        name: trimmed,
        parentId: selectedFolderId,
      })
      setName('')
      await mutateFolders()
    } catch (err) {
      setFormError(err instanceof Error ? err.message : 'Failed to create folder.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="page">
      <h1>Documents</h1>
      <p>Tenant knowledge base. Folders group articles; company-scoped folders belong to a client space.</p>

      <div className="kb-layout">
        <aside className="panel kb-folders">
          <h2>Folders</h2>
          <form className="folder-create" onSubmit={onCreate}>
            <input
              className="input"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder={selected ? `Folder under ${selected.name}` : 'New folder'}
            />
            <button className="btn" type="submit" disabled={submitting}>
              {submitting ? 'Saving…' : 'Add'}
            </button>
          </form>
          {formError && <p className="error">{formError}</p>}
          {foldersLoading && <p>Loading…</p>}
          {folderError && <p className="error">Failed to load folders.</p>}
          <ul className="folder-list">
            <li>
              <button
                type="button"
                className={!selectedFolderId ? 'active' : undefined}
                onClick={() => setSelectedFolderId(null)}
              >
                All articles
              </button>
            </li>
            {rows.map(({ folder, depth }) => (
              <li key={folder.id} style={{ paddingLeft: depth * 12 }}>
                <button
                  type="button"
                  className={selectedFolderId === folder.id ? 'active' : undefined}
                  onClick={() => setSelectedFolderId(folder.id)}
                >
                  {folder.name}
                </button>
              </li>
            ))}
          </ul>
          {!foldersLoading && !folderError && folders.length === 0 && (
            <p className="muted">No folders yet. Create one to group articles.</p>
          )}
        </aside>

        <section>
          <h2>{articleHeading}</h2>
          {isLoading && <p>Loading…</p>}
          {error && <p className="error">Failed to load documents.</p>}
          {docs.length > 0 && (
            <div className="list">
              {docs.map((d) => (
                <article key={d.id} className="list-item">
                  <h3>{d.title}</h3>
                  <p>{d.summary}</p>
                  {d.tags && <span className="tag">{d.tags}</span>}
                </article>
              ))}
            </div>
          )}
          {!isLoading && !error && docs.length === 0 && <p>No published articles in this view.</p>}
        </section>
      </div>
    </div>
  )
}
