import type { AssetFieldInput, OptionList } from '../hooks/useApi'
import { fromZonedInput, toZonedInput } from './regional'

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

export function parseChoices(stored: string | null | undefined): string[] {
  if (!stored) return []
  try {
    const parsed = JSON.parse(stored) as unknown
    return Array.isArray(parsed) ? parsed.filter((v): v is string => typeof v === 'string') : []
  } catch {
    return []
  }
}

/**
 * The form state for a stored value. A date-time is shown in the tenant's zone (`timeZone`), so
 * everyone edits the same wall-clock time whatever their browser's zone.
 */
export function toFormValue(field: FormField, stored: string | null | undefined, timeZone: string): AssetFieldInput {
  switch (field.fieldType) {
    case 'Checkbox':
      return stored === 'true' ? true : stored === 'false' ? false : null
    case 'MultiSelect':
      return parseChoices(stored)
    case 'DateTime':
      return stored ? toZonedInput(stored, timeZone) : ''
    default:
      return stored ?? ''
  }
}

/** What to send for a form value: empty becomes null (clear). A date-time is read in the tenant's zone. */
export function toSubmitValue(field: FormField, value: AssetFieldInput, timeZone: string): AssetFieldInput {
  if (value === null || typeof value === 'boolean' || Array.isArray(value)) return value
  const text = value.trim()
  if (!text) return null
  if (field.fieldType === 'DateTime') return fromZonedInput(text, timeZone) ?? text
  return text
}

