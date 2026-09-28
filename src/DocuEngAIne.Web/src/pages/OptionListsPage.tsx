import { useState, type FormEvent } from 'react'
import {
  addOptionItem,
  canEditContent,
  createOptionList,
  deleteOptionList,
  updateOptionItem,
  updateOptionList,
  useOptionLists,
  useProfile,
  type OptionList,
} from '../hooks/useApi'

function ListDetail(props: { list: OptionList; canEdit: boolean; onClose: () => void }) {
  const { list, canEdit } = props
  const [label, setLabel] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const items = [...list.items].sort((a, b) => a.sortOrder - b.sortOrder || a.label.localeCompare(b.label))

  async function act(action: () => Promise<void>) {
    setMessage(null)
    setBusy(true)
    try {
      await action()
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Action failed.')
    } finally {
      setBusy(false)
    }
  }

  function rename() {
    const name = window.prompt('List name:', list.name)
    if (name === null || !name.trim()) return
    void act(() => updateOptionList(list.id, { name: name.trim() }))
  }

  function editDescription() {
    const description = window.prompt('Description:', list.description ?? '')
    if (description === null) return
    void act(() => updateOptionList(list.id, { description }))
  }

  function remove() {
    if (!window.confirm(`Delete the option list "${list.name}"?`)) return
    void act(async () => {
      await deleteOptionList(list.id)
      props.onClose()
    })
  }

  function renameItem(itemId: string, current: string) {
    const next = window.prompt('Option label (the stored value stays the same):', current)
    if (next === null || !next.trim() || next.trim() === current) return
    void act(() => updateOptionItem(list.id, itemId, { label: next.trim() }))
  }

  /** Swaps an option with its neighbour; sort orders are renumbered so ties cannot stick. */
  function move(index: number, direction: -1 | 1) {
    const target = index + direction
    if (target < 0 || target >= items.length) return
    const reordered = [...items]
    const [moved] = reordered.splice(index, 1)
    reordered.splice(target, 0, moved)
    void act(async () => {
      for (const [order, item] of reordered.entries()) {
        if (item.sortOrder !== order) await updateOptionItem(list.id, item.id, { sortOrder: order })
      }
    })
  }

  function onAdd(e: FormEvent) {
    e.preventDefault()
    if (!label.trim()) return
    void act(async () => {
      await addOptionItem(list.id, label.trim())
      setLabel('')
    })
  }

  return (
    <div className="panel">
      <h2>
        {list.name} <button onClick={props.onClose}>Close</button>
      </h2>
      <p>
        <span className="tag">{list.isActive ? 'Active' : 'Inactive'}</span>{' '}
        {list.description || <span className="muted">No description.</span>}
      </p>
      {message && <p className="error">{message}</p>}
      <p className="muted">
        {list.usedBy.length === 0 ? 'No layout field uses this list.' : `Used by ${list.usedBy.join(', ')}.`}
      </p>
      {canEdit ? (
        <div className="toolbar">
          <button className="btn" type="button" disabled={busy} onClick={rename}>Rename</button>{' '}
          <button className="btn" type="button" disabled={busy} onClick={editDescription}>Edit description</button>{' '}
          <button
            className="btn"
            type="button"
            disabled={busy}
            onClick={() => void act(() => updateOptionList(list.id, { isActive: !list.isActive }))}
          >
            {list.isActive ? 'Deactivate' : 'Activate'}
          </button>{' '}
          <button
            className="btn"
            type="button"
            disabled={busy || list.usedBy.length > 0}
            title={list.usedBy.length > 0 ? 'Fields use this list; point them at another list first.' : undefined}
            onClick={remove}
          >
            Delete list
          </button>
        </div>
      ) : null}

      <table className="data-table">
        <thead>
          <tr>
            <th>Label</th>
            <th>Stored value</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {items.length === 0 && (
            <tr>
              <td colSpan={4}>No options yet. A choice field needs at least one active option before its layout can be published.</td>
            </tr>
          )}
          {items.map((item, index) => (
            <tr key={item.id}>
              <td>{item.label}</td>
              <td>
                <code>{item.value}</code>
              </td>
              <td>{item.isActive ? 'Active' : <span className="muted">Retired</span>}</td>
              <td className="row-actions">
                {canEdit ? (
                  <>
                    <button className="btn" type="button" disabled={busy || index === 0} onClick={() => move(index, -1)} aria-label="Move up">
                      ↑
                    </button>{' '}
                    <button
                      className="btn"
                      type="button"
                      disabled={busy || index === items.length - 1}
                      onClick={() => move(index, 1)}
                      aria-label="Move down"
                    >
                      ↓
                    </button>{' '}
                    <button className="btn" type="button" disabled={busy} onClick={() => renameItem(item.id, item.label)}>
                      Rename
                    </button>{' '}
                    <button
                      className="btn"
                      type="button"
                      disabled={busy}
                      onClick={() => void act(() => updateOptionItem(list.id, item.id, { isActive: !item.isActive }))}
                    >
                      {item.isActive ? 'Retire' : 'Restore'}
                    </button>
                  </>
                ) : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {canEdit ? (
        <form className="toolbar" onSubmit={onAdd}>
          <input
            className="input"
            placeholder="New option label"
            value={label}
            maxLength={100}
            onChange={(e) => setLabel(e.target.value)}
            aria-label="New option label"
          />{' '}
          <button className="btn" type="submit" disabled={busy || !label.trim()}>Add option</button>
        </form>
      ) : null}
    </div>
  )
}

export function OptionListsPage() {
  const { data: profile } = useProfile()
  const canEdit = canEditContent(profile?.role)
  const { data, error, isLoading } = useOptionLists()
  const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [options, setOptions] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setMessage(null)
    setBusy(true)
    try {
      const labels = options
        .split('\n')
        .map((l) => l.trim())
        .filter(Boolean)
      const created = await createOptionList({ name: name.trim(), items: labels.map((l) => ({ label: l })) })
      setName('')
      setOptions('')
      setSelected(created.id)
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Failed to create the list.')
    } finally {
      setBusy(false)
    }
  }

  const lists = data ?? []
  const current = lists.find((l) => l.id === selected)

  return (
    <div className="page">
      <h1>Option lists</h1>
      <p>
        Reusable choices for Select and Multi-select fields, shared across layouts. Each option stores a fixed value, so
        a label can be renamed without touching the assets that chose it. Options are retired, never deleted, and a list
        cannot be emptied or switched off while a published layout needs it.
      </p>
      {message && <p className="error">{message}</p>}

      {canEdit ? (
        <form className="panel" onSubmit={onCreate}>
          <h2>New option list</h2>
          <div className="form-grid">
            <label>
              Name
              <input className="input" required value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
            </label>
            <label className="form-field-wide">
              Options (one per line)
              <textarea className="input" rows={4} value={options} onChange={(e) => setOptions(e.target.value)} />
            </label>
          </div>
          <div className="toolbar">
            <button className="btn" type="submit" disabled={busy || !name.trim()}>Create list</button>
          </div>
        </form>
      ) : (
        <p className="muted">Contributors and above can change option lists.</p>
      )}

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load option lists.</p>}

      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Options</th>
              <th>Used by</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {lists.length === 0 && (
              <tr>
                <td colSpan={5}>No option lists yet.</td>
              </tr>
            )}
            {lists.map((l) => (
              <tr key={l.id}>
                <td>
                  {l.name}
                  {l.description ? <div className="muted">{l.description}</div> : null}
                </td>
                <td>
                  {l.items.filter((i) => i.isActive).length} active
                  {l.items.some((i) => !i.isActive) ? ` · ${l.items.filter((i) => !i.isActive).length} retired` : ''}
                </td>
                <td>{l.usedBy.length === 0 ? <span className="muted">—</span> : l.usedBy.join(', ')}</td>
                <td>{l.isActive ? 'Active' : 'Inactive'}</td>
                <td>
                  <button className="btn" type="button" onClick={() => setSelected(l.id)}>Open</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {current && <ListDetail key={current.id} list={current} canEdit={canEdit} onClose={() => setSelected(null)} />}
    </div>
  )
}
