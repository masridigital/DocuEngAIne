import type { ReactNode } from 'react'
import { useFormat, type AssetFieldInput, type OptionItem, type OptionList } from '../hooks/useApi'
import { parseChoices, type FormField, type OptionLookup } from './assetFieldForm'

function labelFor(item: OptionItem | undefined, value: string) {
  if (!item) return value
  return item.isActive ? item.label : `${item.label} (retired)`
}

function isHttpUrl(value: string) {
  try {
    const url = new URL(value)
    return url.protocol === 'http:' || url.protocol === 'https:'
  } catch {
    return false
  }
}

/** A stored value as people read it. */
export function FieldValue(props: { field: FormField; value: string | null | undefined; lists: OptionLookup }) {
  const { field, value, lists } = props
  const fmt = useFormat()
  if (value === null || value === undefined || value === '') return <span className="muted">—</span>
  const items = field.optionListId ? lists.get(field.optionListId)?.items ?? [] : []
  switch (field.fieldType) {
    case 'Checkbox':
      return <>{value === 'true' ? 'Yes' : 'No'}</>
    case 'Select':
      return <>{labelFor(items.find((i) => i.value === value), value)}</>
    case 'MultiSelect':
      return <>{parseChoices(value).map((v) => labelFor(items.find((i) => i.value === v), v)).join(', ')}</>
    case 'Date':
      return <>{fmt.day(value)}</>
    case 'DateTime':
      return <>{fmt.dateTime(value)}</>
    case 'Url':
      return isHttpUrl(value) ? (
        <a href={value} target="_blank" rel="noopener noreferrer">
          {value}
        </a>
      ) : (
        <>{value}</>
      )
    case 'Email':
      return <a href={`mailto:${value}`}>{value}</a>
    case 'Markdown':
    case 'Text':
      return <span style={{ whiteSpace: 'pre-wrap' }}>{value}</span>
    default:
      return <>{value}</>
  }
}

/** Options to offer: the active ones, plus any retired one the value still holds. */
function choosable(list: OptionList | undefined, current: string[]) {
  const items = [...(list?.items ?? [])].sort((a, b) => a.sortOrder - b.sortOrder)
  return items.filter((i) => (list?.isActive !== false && i.isActive) || current.includes(i.value))
}

/** The input for one field, by type. */
export function FieldEditor(props: {
  field: FormField
  value: AssetFieldInput
  onChange: (value: AssetFieldInput) => void
  lists: OptionLookup
  disabled?: boolean
  /** For a label's htmlFor; not used by a multi-select, whose options carry their own labels. */
  id?: string
}) {
  const { field, value, onChange, lists, disabled, id } = props
  const list = field.optionListId ? lists.get(field.optionListId) : undefined
  const text = typeof value === 'string' ? value : ''

  switch (field.fieldType) {
    case 'Markdown':
      return (
        <textarea id={id} className="input" rows={5} value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
      )
    case 'Number':
      return (
        <input id={id} className="input" type="number" step="any" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
      )
    case 'Date':
      return <input id={id} className="input" type="date" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
    case 'DateTime':
      return (
        <input id={id} className="input" type="datetime-local" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
      )
    case 'Url':
      return (
        <input
          id={id}
          className="input"
          type="url"
          placeholder="https://…"
          value={text}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value)}
        />
      )
    case 'Email':
      return <input id={id} className="input" type="email" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
    case 'Phone':
      return <input id={id} className="input" type="tel" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
    case 'Checkbox':
      return (
        <input id={id} type="checkbox" checked={value === true} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      )
    case 'Select': {
      const options = choosable(list, text ? [text] : [])
      return (
        <select id={id} className="input" value={text} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
          <option value="">{options.length === 0 ? 'No options available' : 'Choose…'}</option>
          {options.map((o) => (
            <option key={o.id} value={o.value}>
              {labelFor(o, o.value)}
            </option>
          ))}
        </select>
      )
    }
    case 'MultiSelect': {
      const chosen = Array.isArray(value) ? value : []
      const options = choosable(list, chosen)
      if (options.length === 0) return <span className="muted">No options available.</span>
      return (
        <div>
          {options.map((o) => (
            <label key={o.id} className="check-label">
              <input
                type="checkbox"
                checked={chosen.includes(o.value)}
                disabled={disabled}
                onChange={(e) => onChange(e.target.checked ? [...chosen, o.value] : chosen.filter((v) => v !== o.value))}
              />
              {labelFor(o, o.value)}
            </label>
          ))}
        </div>
      )
    }
    default:
      return <input id={id} className="input" value={text} maxLength={4000} disabled={disabled} onChange={(e) => onChange(e.target.value)} />
  }
}

/** Groups fields under their section headings; fields with no section come first. */
export function FieldSections<T extends { section?: string | null }>(props: {
  fields: T[]
  render: (fields: T[]) => ReactNode
}) {
  const groups = new Map<string, T[]>()
  for (const field of props.fields) {
    const key = field.section?.trim() ?? ''
    groups.set(key, [...(groups.get(key) ?? []), field])
  }
  const ordered = [...groups.entries()].sort(([a], [b]) => (a === '' ? -1 : b === '' ? 1 : 0))
  return (
    <>
      {ordered.map(([section, fields]) => (
        <section key={section || '(none)'}>
          {section ? <h3>{section}</h3> : null}
          {props.render(fields)}
        </section>
      ))}
    </>
  )
}
