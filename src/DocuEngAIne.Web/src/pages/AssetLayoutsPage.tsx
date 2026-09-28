import { useState, type FormEvent } from 'react'
import {
  addAssetLayoutField,
  ASSET_FIELD_TYPES,
  canEditContent,
  createAssetLayout,
  deleteAssetLayout,
  deleteAssetLayoutField,
  disableAssetLayoutForCompany,
  enableAssetLayoutForCompany,
  layoutProblems,
  publishAssetLayout,
  unpublishAssetLayout,
  updateAssetLayout,
  updateAssetLayoutField,
  useAssetLayout,
  useAssetLayouts,
  useAssetLayoutVersion,
  useAssetLayoutVersions,
  useCompanies,
  useOptionLists,
  useProfile,
  usesOptions,
  useTerms,
  type AssetFieldType,
  type AssetLayoutField,
  type AssetLayoutProblem,
  type OptionList,
  type UpdateAssetLayoutFieldInput,
} from '../hooks/useApi'

const TYPE_HELP: Record<AssetFieldType, string> = {
  Text: 'one line of text',
  Markdown: 'longer notes',
  Number: 'a number',
  Date: 'a calendar date',
  DateTime: 'a date and time',
  Url: 'an http or https link',
  Email: 'an email address',
  Phone: 'a phone number',
  Checkbox: 'yes or no',
  Select: 'one option from a list',
  MultiSelect: 'any number of options from a list',
}

type Act = (action: () => Promise<void>) => void

function isDateLike(type: string) {
  return type === 'Date' || type === 'DateTime'
}

function ProblemList(props: { problems: AssetLayoutProblem[] }) {
  if (props.problems.length === 0) return null
  return (
    <ul className="problem-list">
      {props.problems.map((p, i) => (
        <li key={`${p.code}-${p.fieldId ?? ''}-${i}`} className="error">
          {p.message}
        </li>
      ))}
    </ul>
  )
}

function OptionListSelect(props: {
  lists: OptionList[]
  value: string
  onChange: (value: string) => void
  disabled?: boolean
}) {
  // Inactive lists offer nothing new, so only a field already on one keeps seeing it.
  const choices = props.lists.filter((l) => l.isActive || l.id === props.value)
  return (
    <select className="input" value={props.value} disabled={props.disabled} onChange={(e) => props.onChange(e.target.value)}>
      <option value="">{choices.length === 0 ? 'Create an option list first' : 'Choose a list…'}</option>
      {choices.map((l) => (
        <option key={l.id} value={l.id}>
          {l.name}
          {l.isActive ? '' : ' (inactive)'} — {l.items.filter((i) => i.isActive).length} option(s)
        </option>
      ))}
    </select>
  )
}

function FieldRow(props: {
  layoutId: string
  field: AssetLayoutField
  lists: OptionList[]
  canEdit: boolean
  busy: boolean
  act: Act
}) {
  const { field } = props
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState(field.name)
  const [type, setType] = useState<AssetFieldType>(field.fieldType)
  const [section, setSection] = useState(field.section ?? '')
  const [helpText, setHelpText] = useState(field.helpText ?? '')
  const [isRequired, setIsRequired] = useState(field.isRequired)
  const [isExpiration, setIsExpiration] = useState(field.isExpiration)
  const [sortOrder, setSortOrder] = useState(String(field.sortOrder))
  const [optionListId, setOptionListId] = useState(field.optionListId ?? '')

  function begin() {
    setName(field.name)
    setType(field.fieldType)
    setSection(field.section ?? '')
    setHelpText(field.helpText ?? '')
    setIsRequired(field.isRequired)
    setIsExpiration(field.isExpiration)
    setSortOrder(String(field.sortOrder))
    setOptionListId(field.optionListId ?? '')
    setEditing(true)
  }

  function save(e: FormEvent) {
    e.preventDefault()
    // Only what changed is sent: every schema change on a published layout is a new version.
    const input: UpdateAssetLayoutFieldInput = {}
    if (name.trim() && name.trim() !== field.name) input.name = name.trim()
    if (type !== field.fieldType) input.fieldType = type
    if (section.trim() !== (field.section ?? '')) input.section = section.trim()
    if (helpText.trim() !== (field.helpText ?? '')) input.helpText = helpText.trim()
    if (isRequired !== field.isRequired) input.isRequired = isRequired
    const expiration = isDateLike(type) && isExpiration
    if (expiration !== field.isExpiration) input.isExpiration = expiration
    const order = Number.parseInt(sortOrder, 10)
    if (!Number.isNaN(order) && order !== field.sortOrder) input.sortOrder = order
    if (usesOptions(type) && optionListId && optionListId !== (field.optionListId ?? '')) input.optionListId = optionListId

    if (Object.keys(input).length === 0) {
      setEditing(false)
      return
    }
    props.act(async () => {
      await updateAssetLayoutField(props.layoutId, field.id, input)
      setEditing(false)
    })
  }

  function remove() {
    if (!window.confirm(`Remove the field "${field.name}"? A field that holds values cannot be removed.`)) return
    props.act(() => deleteAssetLayoutField(props.layoutId, field.id))
  }

  if (editing) {
    return (
      <tr>
        <td colSpan={7}>
          <form onSubmit={save}>
            <div className="form-grid">
              <label>
                Name
                <input className="input" required value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
              </label>
              <label>
                Type
                <select className="input" value={type} onChange={(e) => setType(e.target.value as AssetFieldType)}>
                  {ASSET_FIELD_TYPES.map((t) => (
                    <option key={t} value={t}>
                      {t} — {TYPE_HELP[t]}
                    </option>
                  ))}
                </select>
              </label>
              {usesOptions(type) ? (
                <label>
                  Option list
                  <OptionListSelect lists={props.lists} value={optionListId} onChange={setOptionListId} />
                </label>
              ) : null}
              <label>
                Section (optional)
                <input className="input" value={section} maxLength={100} onChange={(e) => setSection(e.target.value)} />
              </label>
              <label>
                Help text (optional)
                <input className="input" value={helpText} maxLength={500} onChange={(e) => setHelpText(e.target.value)} />
              </label>
              <label>
                Order
                <input className="input" type="number" value={sortOrder} onChange={(e) => setSortOrder(e.target.value)} />
              </label>
              <label className="check-label">
                <input type="checkbox" checked={isRequired} onChange={(e) => setIsRequired(e.target.checked)} />
                Required
              </label>
              {isDateLike(type) ? (
                <label className="check-label">
                  <input type="checkbox" checked={isExpiration} onChange={(e) => setIsExpiration(e.target.checked)} />
                  Tracks an expiration
                </label>
              ) : null}
            </div>
            <p className="muted">
              Changing the type or list converts the values already stored, and is refused if any of them would not fit.
            </p>
            <div className="toolbar">
              <button className="btn" type="submit" disabled={props.busy}>Save field</button>{' '}
              <button className="btn btn-secondary" type="button" disabled={props.busy} onClick={() => setEditing(false)}>
                Cancel
              </button>
            </div>
          </form>
        </td>
      </tr>
    )
  }

  return (
    <tr>
      <td>
        {field.name}
        {field.helpText ? <div className="muted">{field.helpText}</div> : null}
      </td>
      <td>{field.fieldType}</td>
      <td>{field.section || <span className="muted">—</span>}</td>
      <td>
        {field.isRequired ? 'Required' : ''}
        {field.isRequired && field.isExpiration ? ' · ' : ''}
        {field.isExpiration ? 'Tracks expiration' : ''}
      </td>
      <td>
        {usesOptions(field.fieldType) ? (
          field.optionListName ?? <span className="error">No list</span>
        ) : (
          <span className="muted">—</span>
        )}
      </td>
      <td>{field.sortOrder}</td>
      <td className="row-actions">
        {props.canEdit ? (
          <>
            <button className="btn" type="button" disabled={props.busy} onClick={begin}>Edit</button>{' '}
            <button className="btn" type="button" disabled={props.busy} onClick={remove}>Remove</button>
          </>
        ) : null}
      </td>
    </tr>
  )
}

function AddFieldForm(props: { layoutId: string; lists: OptionList[]; busy: boolean; act: Act }) {
  const [name, setName] = useState('')
  const [type, setType] = useState<AssetFieldType>('Text')
  const [section, setSection] = useState('')
  const [helpText, setHelpText] = useState('')
  const [isRequired, setIsRequired] = useState(false)
  const [isExpiration, setIsExpiration] = useState(false)
  const [optionListId, setOptionListId] = useState('')

  function submit(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    props.act(async () => {
      await addAssetLayoutField(props.layoutId, {
        name: name.trim(),
        type,
        isRequired,
        isExpiration: isDateLike(type) && isExpiration,
        section: section.trim() || null,
        helpText: helpText.trim() || null,
        optionListId: usesOptions(type) ? optionListId || null : null,
      })
      setName('')
      setSection('')
      setHelpText('')
      setIsRequired(false)
      setIsExpiration(false)
    })
  }

  return (
    <form onSubmit={submit}>
      <h3>Add a field</h3>
      <div className="form-grid">
        <label>
          Name
          <input className="input" required value={name} maxLength={100} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Type
          <select className="input" value={type} onChange={(e) => setType(e.target.value as AssetFieldType)}>
            {ASSET_FIELD_TYPES.map((t) => (
              <option key={t} value={t}>
                {t} — {TYPE_HELP[t]}
              </option>
            ))}
          </select>
        </label>
        {usesOptions(type) ? (
          <label>
            Option list
            <OptionListSelect lists={props.lists} value={optionListId} onChange={setOptionListId} />
          </label>
        ) : null}
        <label>
          Section (optional)
          <input className="input" value={section} maxLength={100} onChange={(e) => setSection(e.target.value)} />
        </label>
        <label>
          Help text (optional)
          <input className="input" value={helpText} maxLength={500} onChange={(e) => setHelpText(e.target.value)} />
        </label>
        <label className="check-label">
          <input type="checkbox" checked={isRequired} onChange={(e) => setIsRequired(e.target.checked)} />
          Required
        </label>
        {isDateLike(type) ? (
          <label className="check-label">
            <input type="checkbox" checked={isExpiration} onChange={(e) => setIsExpiration(e.target.checked)} />
            Tracks an expiration (shows on Expirations)
          </label>
        ) : null}
      </div>
      <div className="toolbar">
        <button className="btn" type="submit" disabled={props.busy || !name.trim()}>Add field</button>
      </div>
    </form>
  )
}

function Versions(props: { layoutId: string }) {
  const { data: versions, error } = useAssetLayoutVersions(props.layoutId)
  const [open, setOpen] = useState<number | null>(null)
  const { data: detail } = useAssetLayoutVersion(props.layoutId, open)

  if (error) return <p className="error">Failed to load the version history.</p>
  if (!versions) return <p>Loading…</p>
  if (versions.length === 0) {
    return (
      <p className="muted">
        No versions yet. One is recorded when the layout is published, and again after every change while it is.
      </p>
    )
  }

  return (
    <>
      <table className="data-table">
        <thead>
          <tr>
            <th>Version</th>
            <th>Change</th>
            <th>By</th>
            <th>When</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {versions.map((v) => (
            <tr key={v.versionNumber}>
              <td>v{v.versionNumber}</td>
              <td>{v.summary}</td>
              <td>{v.createdByName ?? <span className="muted">—</span>}</td>
              <td>{new Date(v.createdAt).toLocaleString()}</td>
              <td>
                <button
                  className="btn"
                  type="button"
                  onClick={() => setOpen(open === v.versionNumber ? null : v.versionNumber)}
                >
                  {open === v.versionNumber ? 'Hide' : 'View'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {open !== null && detail && detail.versionNumber === open ? (
        <pre className="schema-json">{JSON.stringify(detail.schema, null, 2)}</pre>
      ) : null}
    </>
  )
}

function LayoutDetail(props: { id: string; canEdit: boolean; onClose: () => void }) {
  const { data, error, isLoading } = useAssetLayout(props.id)
  const { data: lists } = useOptionLists()
  const { data: companies } = useCompanies()
  const [companyId, setCompanyId] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [problems, setProblems] = useState<AssetLayoutProblem[]>([])
  const [busy, setBusy] = useState(false)

  async function run(action: () => Promise<void>) {
    setMessage(null)
    setProblems([])
    setBusy(true)
    try {
      await action()
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Action failed.')
      setProblems(layoutProblems(err))
    } finally {
      setBusy(false)
    }
  }
  const act: Act = (action) => void run(action)

  if (isLoading) return <p>Loading…</p>
  if (error || !data) return <p className="error">Failed to load the layout.</p>

  const { layout, companies: enabled, assetCount } = data
  const allLists = lists ?? []
  const enabledIds = new Set(enabled.map((c) => c.companyId))
  const companyChoices = (companies ?? []).filter((c) => !enabledIds.has(c.id))
  const fields = [...layout.fields].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))

  function rename() {
    const name = window.prompt('Layout name:', layout.name)
    if (name === null || !name.trim()) return
    act(() => updateAssetLayout(layout.id, { name: name.trim() }))
  }

  function editDescription() {
    const description = window.prompt('Description:', layout.description ?? '')
    if (description === null) return
    act(() => updateAssetLayout(layout.id, { description }))
  }

  function remove() {
    if (!window.confirm(`Delete the layout "${layout.name}"? This cannot be undone.`)) return
    act(async () => {
      await deleteAssetLayout(layout.id)
      props.onClose()
    })
  }

  function onEnableCompany(e: FormEvent) {
    e.preventDefault()
    if (!companyId) return
    act(async () => {
      await enableAssetLayoutForCompany(layout.id, companyId)
      setCompanyId('')
    })
  }

  return (
    <div className="panel">
      <h2>
        {layout.name} <button onClick={props.onClose}>Close</button>
      </h2>
      <p>
        <span className="tag">{layout.isPublished ? `Published · v${layout.currentVersion}` : 'Draft'}</span>{' '}
        <span className="muted">
          {assetCount} asset(s) use it · {layout.availableToAllCompanies ? 'every company' : 'chosen companies only'}
        </span>
      </p>
      <p>{layout.description || <span className="muted">No description.</span>}</p>
      {message && <p className="error">{message}</p>}
      <ProblemList problems={problems} />

      {!layout.isPublished ? (
        data.problems.length > 0 ? (
          <>
            <p className="banner">This draft is not offered for new assets yet. Before it can be published:</p>
            <ProblemList problems={data.problems} />
          </>
        ) : (
          <p className="banner">This draft is ready to publish. Once published, it is offered for new assets.</p>
        )
      ) : null}

      {props.canEdit ? (
        <div className="toolbar">
          {layout.isPublished ? (
            <button className="btn" type="button" disabled={busy} onClick={() => act(() => unpublishAssetLayout(layout.id))}>
              Unpublish
            </button>
          ) : (
            <button
              className="btn"
              type="button"
              disabled={busy || data.problems.length > 0}
              onClick={() => act(() => publishAssetLayout(layout.id))}
            >
              Publish
            </button>
          )}{' '}
          <button className="btn" type="button" disabled={busy} onClick={rename}>Rename</button>{' '}
          <button className="btn" type="button" disabled={busy} onClick={editDescription}>Edit description</button>{' '}
          <label className="check-label">
            <input
              type="checkbox"
              checked={!layout.availableToAllCompanies}
              disabled={busy}
              onChange={(e) => act(() => updateAssetLayout(layout.id, { availableToAllCompanies: !e.target.checked }))}
            />
            Limit to chosen companies
          </label>{' '}
          <button
            className="btn"
            type="button"
            disabled={busy || assetCount > 0}
            title={assetCount > 0 ? 'Assets use this layout; move or delete them first.' : undefined}
            onClick={remove}
          >
            Delete layout
          </button>
        </div>
      ) : null}

      <h3>Fields</h3>
      <table className="data-table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Type</th>
            <th>Section</th>
            <th>Rules</th>
            <th>Options</th>
            <th>Order</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {fields.length === 0 && (
            <tr>
              <td colSpan={7}>No fields yet. A layout needs at least one field before it can be published.</td>
            </tr>
          )}
          {fields.map((f) => (
            <FieldRow key={f.id} layoutId={layout.id} field={f} lists={allLists} canEdit={props.canEdit} busy={busy} act={act} />
          ))}
        </tbody>
      </table>
      {props.canEdit ? <AddFieldForm layoutId={layout.id} lists={allLists} busy={busy} act={act} /> : null}

      {!layout.availableToAllCompanies ? (
        <>
          <h3>Companies</h3>
          <p className="muted">
            Only these companies can have new assets of this layout. Choosing a company needs Manage access to it.
          </p>
          <table className="data-table">
            <thead>
              <tr>
                <th>Company</th>
                <th>Enabled</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {enabled.length === 0 && (
                <tr>
                  <td colSpan={3}>No companies yet, so no new assets can use this layout.</td>
                </tr>
              )}
              {enabled.map((c) => (
                <tr key={c.companyId}>
                  <td>{c.companyName}</td>
                  <td>{new Date(c.activatedAt).toLocaleDateString()}</td>
                  <td>
                    {props.canEdit ? (
                      <button
                        className="btn"
                        type="button"
                        disabled={busy}
                        onClick={() => act(() => disableAssetLayoutForCompany(layout.id, c.companyId))}
                      >
                        Disable
                      </button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {props.canEdit ? (
            <form className="toolbar" onSubmit={onEnableCompany}>
              <select className="input" value={companyId} onChange={(e) => setCompanyId(e.target.value)} aria-label="Company">
                <option value="">Enable for a company…</option>
                {companyChoices.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.name}
                  </option>
                ))}
              </select>{' '}
              <button className="btn" type="submit" disabled={busy || !companyId}>Enable</button>
            </form>
          ) : null}
        </>
      ) : null}

      <h3>Version history</h3>
      <Versions layoutId={layout.id} />
    </div>
  )
}

export function AssetLayoutsPage() {
  const term = useTerms()
  const { data: profile } = useProfile()
  const canEdit = canEditContent(profile?.role)
  const { data, error, isLoading } = useAssetLayouts()
  const [selected, setSelected] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [limited, setLimited] = useState(false)
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onCreate(e: FormEvent) {
    e.preventDefault()
    if (!name.trim()) return
    setMessage(null)
    setBusy(true)
    try {
      const created = await createAssetLayout({
        name: name.trim(),
        description: description.trim() || undefined,
        availableToAllCompanies: !limited,
      })
      setName('')
      setDescription('')
      setLimited(false)
      setSelected(created.layout.id)
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Failed to create the layout.')
    } finally {
      setBusy(false)
    }
  }

  const layouts = data ?? []

  return (
    <div className="page">
      <h1>{term('asset', 'singular')} layouts</h1>
      <p>
        A layout is the set of fields an asset of one kind carries — typed, grouped into sections, with choices drawn
        from option lists. New layouts start as drafts; publish one to offer it for new assets. While a layout is
        published, every change is checked and kept as a version, and no change may leave stored values the field would
        reject.
      </p>
      {message && <p className="error">{message}</p>}

      {canEdit ? (
        <form className="toolbar" onSubmit={onCreate}>
          <input
            className="input"
            placeholder="Layout name"
            value={name}
            maxLength={200}
            onChange={(e) => setName(e.target.value)}
            aria-label="Layout name"
          />{' '}
          <input
            className="input"
            placeholder="Description (optional)"
            value={description}
            maxLength={1000}
            onChange={(e) => setDescription(e.target.value)}
            aria-label="Description"
          />{' '}
          <label className="check-label">
            <input type="checkbox" checked={limited} onChange={(e) => setLimited(e.target.checked)} /> Limit to chosen companies
          </label>{' '}
          <button className="btn" type="submit" disabled={busy || !name.trim()}>Create draft</button>
        </form>
      ) : (
        <p className="muted">Contributors and above can change layouts.</p>
      )}

      {isLoading && <p>Loading…</p>}
      {error && <p className="error">Failed to load asset layouts.</p>}

      {!isLoading && !error && (
        <table className="data-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Status</th>
              <th>Fields</th>
              <th>Companies</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {layouts.length === 0 && (
              <tr>
                <td colSpan={5}>No layouts yet.</td>
              </tr>
            )}
            {layouts.map((l) => (
              <tr key={l.id}>
                <td>
                  {l.name}
                  {l.description ? <div className="muted">{l.description}</div> : null}
                </td>
                <td>{l.isPublished ? `Published · v${l.currentVersion}` : 'Draft'}</td>
                <td>{l.fields.length}</td>
                <td>{l.availableToAllCompanies ? 'Every company' : `${l.enabledCompanyIds.length} chosen`}</td>
                <td>
                  <button className="btn" type="button" onClick={() => setSelected(l.id)}>Open</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {selected && <LayoutDetail key={selected} id={selected} canEdit={canEdit} onClose={() => setSelected(null)} />}
    </div>
  )
}
