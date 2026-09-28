import { useState, type FormEvent } from 'react'
import { ArchiveButton } from '../components/ArchiveButton'
import { FieldEditor, FieldSections, FieldValue } from '../components/AssetFields'
import { optionLookup, toFormValue, toSubmitValue, type FormField, type OptionLookup } from '../components/assetFieldForm'
import {
  canEditContent,
  createAsset,
  fieldErrors,
  layoutUsableFor,
  updateAssetFields,
  useAsset,
  useAssetLayouts,
  useAssets,
  useCompanies,
  useOptionLists,
  useProfile,
  useTerms,
  type Asset,
  type AssetFieldInput,
} from '../hooks/useApi'

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

/** Inputs for a set of layout fields, grouped by section, with each field's help and error. */
function FieldForm(props: {
  idPrefix: string
  fields: FormField[]
  values: Record<string, AssetFieldInput>
  initial: (field: FormField) => AssetFieldInput
  onChange: (fieldId: string, value: AssetFieldInput) => void
  errors: Record<string, string>
  lists: OptionLookup
  disabled: boolean
}) {
  return (
    <FieldSections
      fields={props.fields}
      render={(group) => (
        <div className="form-grid">
          {group.map((f) => {
            const id = `${props.idPrefix}-${f.fieldId}`
            const wide = f.fieldType === 'Markdown' || f.fieldType === 'MultiSelect'
            return (
              <div key={f.fieldId} className={wide ? 'form-field form-field-wide' : 'form-field'}>
                <label htmlFor={id}>
                  {f.name}
                  {f.isRequired ? ' *' : ''}
                </label>
                <FieldEditor
                  id={id}
                  field={f}
                  value={f.fieldId in props.values ? props.values[f.fieldId] : props.initial(f)}
                  onChange={(v) => props.onChange(f.fieldId, v)}
                  lists={props.lists}
                  disabled={props.disabled}
                />
                {f.helpText ? <span className="muted">{f.helpText}</span> : null}
                {props.errors[f.fieldId] ? <span className="error">{props.errors[f.fieldId]}</span> : null}
              </div>
            )
          })}
        </div>
      )}
    />
  )
}

function AssetPanel(props: { id: string; canEdit: boolean; onClose: () => void }) {
  const { data: asset, error, isLoading } = useAsset(props.id)
  const { data: lists } = useOptionLists()
  const lookup = optionLookup(lists)
  const [editing, setEditing] = useState(false)
  const [values, setValues] = useState<Record<string, AssetFieldInput>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (isLoading) {
    return (
      <div className="panel">
        <p>Loading…</p>
      </div>
    )
  }
  if (error || !asset) {
    return (
      <div className="panel">
        <p className="error">Failed to load the asset.</p>
        <button className="btn" type="button" onClick={props.onClose}>Close</button>
      </div>
    )
  }

  const current = asset
  // Values whose field has left the layout are shown but cannot be written.
  const editable = current.fields.filter((f) => f.onLayout)
  const stored = new Map(current.fields.map((f) => [f.fieldId, f.value]))

  function startEditing() {
    setValues({})
    setErrors({})
    setMessage(null)
    setEditing(true)
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    // Only what changed is sent, so an untouched field is never re-validated or cleared.
    const changed: Record<string, AssetFieldInput> = {}
    for (const f of editable) {
      if (!(f.fieldId in values)) continue
      const before = toSubmitValue(f, toFormValue(f, stored.get(f.fieldId)))
      const after = toSubmitValue(f, values[f.fieldId])
      if (JSON.stringify(before) !== JSON.stringify(after)) changed[f.fieldId] = after
    }
    if (Object.keys(changed).length === 0) {
      setEditing(false)
      return
    }

    setBusy(true)
    setErrors({})
    setMessage(null)
    try {
      await updateAssetFields(current.id, changed)
      setEditing(false)
    } catch (err) {
      setErrors(fieldErrors(err))
      setMessage(err instanceof Error ? err.message : 'Failed to save the fields.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel">
      <h2>
        {current.name} <button onClick={props.onClose}>Close</button>
      </h2>
      <p className="muted">
        {current.assetType?.name ? `Layout: ${current.assetType.name}` : 'No layout'}
        {current.status ? ` · ${current.status}` : ''}
        {current.location ? ` · ${current.location}` : ''}
      </p>
      {message && <p className="error">{message}</p>}

      {current.fields.length === 0 ? (
        <p className="muted">This asset’s layout has no fields.</p>
      ) : editing ? (
        <form onSubmit={save}>
          <FieldForm
            idPrefix={`asset-${current.id}`}
            fields={editable}
            values={values}
            initial={(f) => toFormValue(f, stored.get(f.fieldId))}
            onChange={(fieldId, v) => setValues((prev) => ({ ...prev, [fieldId]: v }))}
            errors={errors}
            lists={lookup}
            disabled={busy}
          />
          <div className="toolbar">
            <button className="btn" type="submit" disabled={busy}>
              {busy ? 'Saving…' : 'Save fields'}
            </button>{' '}
            <button className="btn btn-secondary" type="button" disabled={busy} onClick={() => setEditing(false)}>
              Cancel
            </button>
          </div>
        </form>
      ) : (
        <>
          <FieldSections
            fields={current.fields}
            render={(group) => (
              <dl className="detail-grid">
                {group.map((f) => (
                  <div key={f.fieldId} className="detail-row">
                    <dt>
                      {f.name}
                      {f.onLayout ? '' : ' (no longer on the layout)'}
                    </dt>
                    <dd>
                      <FieldValue field={f} value={f.value} lists={lookup} />
                    </dd>
                  </div>
                ))}
              </dl>
            )}
          />
          {props.canEdit && editable.length > 0 ? (
            <div className="toolbar">
              <button className="btn" type="button" onClick={startEditing}>Edit fields</button>
            </div>
          ) : null}
        </>
      )}
    </div>
  )
}

function NewAssetForm(props: { onCreated: (id: string) => void; onCancel: () => void }) {
  const term = useTerms()
  const { data: layouts } = useAssetLayouts()
  const { data: companies } = useCompanies()
  const { data: lists } = useOptionLists()
  const lookup = optionLookup(lists)
  const [name, setName] = useState('')
  const [companyId, setCompanyId] = useState('')
  const [layoutId, setLayoutId] = useState('')
  const [values, setValues] = useState<Record<string, AssetFieldInput>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  // Only published layouts that may be used where the asset will live.
  const usable = (layouts ?? []).filter((l) => layoutUsableFor(l, companyId || null))
  const layout = usable.find((l) => l.id === layoutId)
  const fields: FormField[] = (layout?.fields ?? []).map((f) => ({
    fieldId: f.id,
    name: f.name,
    fieldType: f.fieldType,
    section: f.section,
    helpText: f.helpText,
    isRequired: f.isRequired,
    optionListId: f.optionListId,
  }))
  // A new checkbox starts unticked, which is a value (false), so a required one can be left unticked.
  const initial = (f: FormField): AssetFieldInput => (f.fieldType === 'Checkbox' ? false : toFormValue(f, null))

  function chooseCompany(next: string) {
    setCompanyId(next)
    const stillUsable = (layouts ?? []).some((l) => l.id === layoutId && layoutUsableFor(l, next || null))
    if (!stillUsable) {
      setLayoutId('')
      setValues({})
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!layout || !name.trim()) return
    const submitted: Record<string, AssetFieldInput> = {}
    for (const f of fields) {
      const value = toSubmitValue(f, f.fieldId in values ? values[f.fieldId] : initial(f))
      if (value !== null && !(Array.isArray(value) && value.length === 0)) submitted[f.fieldId] = value
    }

    setBusy(true)
    setErrors({})
    setMessage(null)
    try {
      const created = await createAsset({
        name: name.trim(),
        assetTypeId: layout.id,
        companyId: companyId || null,
        fields: submitted,
      })
      props.onCreated(created.id)
    } catch (err) {
      setErrors(fieldErrors(err))
      setMessage(err instanceof Error ? err.message : 'Failed to create the asset.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel" onSubmit={submit}>
      <h2>New {term('asset', 'singular')}</h2>
      {message && <p className="error">{message}</p>}
      <div className="form-grid">
        <label>
          Name
          <input className="input" required value={name} maxLength={200} onChange={(e) => setName(e.target.value)} />
        </label>
        <label>
          Company
          <select className="input" value={companyId} onChange={(e) => chooseCompany(e.target.value)}>
            <option value="">Tenant-wide (no company)</option>
            {(companies ?? []).map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          Layout
          <select
            className="input"
            required
            value={layout ? layoutId : ''}
            onChange={(e) => {
              setLayoutId(e.target.value)
              setValues({})
              setErrors({})
            }}
          >
            <option value="">{usable.length === 0 ? 'No published layout is available here' : 'Choose…'}</option>
            {usable.map((l) => (
              <option key={l.id} value={l.id}>
                {l.name}
              </option>
            ))}
          </select>
        </label>
      </div>
      {layout && fields.length > 0 ? (
        <FieldForm
          idPrefix="new-asset"
          fields={fields}
          values={values}
          initial={initial}
          onChange={(fieldId, v) => setValues((prev) => ({ ...prev, [fieldId]: v }))}
          errors={errors}
          lists={lookup}
          disabled={busy}
        />
      ) : null}
      <div className="toolbar">
        <button className="btn" type="submit" disabled={busy || !layout || !name.trim()}>
          {busy ? 'Creating…' : `Create ${term('asset', 'singular')}`}
        </button>{' '}
        <button className="btn btn-secondary" type="button" disabled={busy} onClick={props.onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

export function AssetsPage() {
  const term = useTerms()
  const { data, error, isLoading } = useAssets()
  const { data: profile } = useProfile()
  const canEdit = canEditContent(profile?.role)
  const assets = Array.isArray(data) ? data : []
  const [actionError, setActionError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  return (
    <div className="page">
      <h1>{term('asset')}</h1>
      <p className="muted">Open in Halo / Open in Ninja when a device or asset portal URL is stored. URLs only — no secrets.</p>
      {canEdit && !creating ? (
        <div className="toolbar">
          <button className="btn" type="button" onClick={() => setCreating(true)}>
            New {term('asset', 'singular')}
          </button>
        </div>
      ) : null}
      {creating ? (
        <NewAssetForm
          onCreated={(id) => {
            setCreating(false)
            setSelected(id)
          }}
          onCancel={() => setCreating(false)}
        />
      ) : null}
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
                <td className="row-actions">
                  <button className="btn" type="button" onClick={() => setSelected(a.id)}>Open</button>{' '}
                  <ArchiveButton type="Asset" id={a.id} label={a.name} onError={setActionError} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {selected && <AssetPanel key={selected} id={selected} canEdit={canEdit} onClose={() => setSelected(null)} />}
    </div>
  )
}
