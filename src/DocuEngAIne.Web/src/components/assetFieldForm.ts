import type { AssetFieldInput, OptionList } from '../hooks/useApi'

/** A layout field as the asset form needs it. */
export type FormField = {
  fieldId: string
  name: string
  fieldType: string
  section?: string | null
  helpText?: string | null
  isRequired: boolean
  optionListId?: string | null
}

export type OptionLookup = Map<string, OptionList>

export function optionLookup(lists: OptionList[] | undefined): OptionLookup {
  return new Map((lists ?? []).map((l) => [l.id, l]))
}

function pad(n: number) {
  return String(n).padStart(2, '0')
}

/** Stored round-trip UTC → the local `yyyy-MM-ddTHH:mm` a datetime-local input shows. */
function toLocalInput(iso: string) {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return ''
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export function parseChoices(stored: string | null | undefined): string[] {
  if (!stored) return []
  try {
    const parsed = JSON.parse(stored) as unknown
    return Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === 'string') : []
  } catch {
    return []
  }
}

/** The form state for a stored value. */
export function toFormValue(field: FormField, stored: string | null | undefined): AssetFieldInput {
  switch (field.fieldType) {
    case 'Checkbox':
      return stored === 'true' ? true : stored === 'false' ? false : null
    case 'MultiSelect':
      return parseChoices(stored)
    case 'DateTime':
      return stored ? toLocalInput(stored) : ''
    default:
      return stored ?? ''
  }
}

/** What to send for a form value: empty becomes null (clear). */
export function toSubmitValue(field: FormField, value: AssetFieldInput): AssetFieldInput {
  if (value === null || typeof value === 'boolean' || Array.isArray(value)) return value
  const text = value.trim()
  if (!text) return null
  if (field.fieldType === 'DateTime') {
    const moment = new Date(text)
    return Number.isNaN(moment.getTime()) ? text : moment.toISOString()
  }
  return text
}

