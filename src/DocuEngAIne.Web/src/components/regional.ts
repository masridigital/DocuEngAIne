/**
 * The tenant's time zone and date / time formats, applied to every date the app shows. Instants
 * (stored as UTC) are shown in the tenant's zone; calendar days (`yyyy-MM-dd`, as Date fields and
 * expirations are stored) are never moved by a zone. Inputs work the other way round: what a
 * technician types into a date or time box is read in the tenant's zone, not the browser's.
 */

export type Regional = { timeZone: string; dateFormat: string; timeFormat: string }

export const DEFAULT_REGIONAL: Regional = { timeZone: 'UTC', dateFormat: 'yyyy-MM-dd', timeFormat: '24h' }

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

type Parts = { year: number; month: number; day: number; hour: number; minute: number; second: number }

const formatters = new Map<string, Intl.DateTimeFormat>()

function build(timeZone: string) {
  return new Intl.DateTimeFormat('en-US', {
    timeZone,
    hourCycle: 'h23',
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  })
}

function formatterFor(timeZone: string) {
  let formatter = formatters.get(timeZone)
  if (!formatter) {
    try {
      formatter = build(timeZone)
    } catch {
      // A zone this browser does not know: show UTC rather than break the page.
      formatter = build('UTC')
    }
    formatters.set(timeZone, formatter)
  }
  return formatter
}

/** The wall-clock fields of an instant in a zone. */
function partsIn(instant: Date, timeZone: string): Parts {
  const values: Record<string, number> = {}
  for (const part of formatterFor(timeZone).formatToParts(instant)) {
    if (part.type !== 'literal') values[part.type] = Number(part.value)
  }
  return {
    year: values.year,
    month: values.month,
    day: values.day,
    hour: values.hour % 24,
    minute: values.minute,
    second: values.second,
  }
}

function pad(n: number) {
  return String(n).padStart(2, '0')
}

function writeDate(year: number, month: number, day: number, format: string) {
  switch (format) {
    case 'dd/MM/yyyy':
      return `${pad(day)}/${pad(month)}/${year}`
    case 'MM/dd/yyyy':
      return `${pad(month)}/${pad(day)}/${year}`
    case 'dd.MM.yyyy':
      return `${pad(day)}.${pad(month)}.${year}`
    case 'd MMM yyyy':
      return `${day} ${MONTHS[month - 1]} ${year}`
    case 'MMM d, yyyy':
      return `${MONTHS[month - 1]} ${day}, ${year}`
    default:
      return `${year}-${pad(month)}-${pad(day)}`
  }
}

function writeTime(hour: number, minute: number, format: string) {
  if (format === '12h') return `${hour % 12 === 0 ? 12 : hour % 12}:${pad(minute)} ${hour < 12 ? 'AM' : 'PM'}`
  return `${pad(hour)}:${pad(minute)}`
}

function instant(value: string | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null
  const d = value instanceof Date ? value : new Date(value)
  return Number.isNaN(d.getTime()) ? null : d
}

const DAY = /^(\d{4})-(\d{2})-(\d{2})$/

/** A calendar day (`yyyy-MM-dd`) in the tenant's format. No zone moves it. */
export function formatDay(day: string | null | undefined, r: Regional): string {
  if (!day) return '—'
  const m = DAY.exec(day.trim().slice(0, 10))
  return m ? writeDate(Number(m[1]), Number(m[2]), Number(m[3]), r.dateFormat) : day
}

/** The day an instant falls on in the tenant's zone. */
export function formatDate(value: string | Date | null | undefined, r: Regional): string {
  const d = instant(value)
  if (!d) return typeof value === 'string' && value ? value : '—'
  const p = partsIn(d, r.timeZone)
  return writeDate(p.year, p.month, p.day, r.dateFormat)
}

/** An instant's date and time in the tenant's zone. */
export function formatDateTime(value: string | Date | null | undefined, r: Regional): string {
  const d = instant(value)
  if (!d) return typeof value === 'string' && value ? value : '—'
  const p = partsIn(d, r.timeZone)
  return `${writeDate(p.year, p.month, p.day, r.dateFormat)} ${writeTime(p.hour, p.minute, r.timeFormat)}`
}

/** Minutes the zone is ahead of UTC at an instant. */
function offsetMinutes(utcMs: number, timeZone: string) {
  const whole = Math.floor(utcMs / 1000) * 1000
  const p = partsIn(new Date(whole), timeZone)
  return Math.round((Date.UTC(p.year, p.month - 1, p.day, p.hour, p.minute, p.second) - whole) / 60000)
}

/** A wall-clock time in a zone as the instant it names. */
function zonedToUtc(year: number, month: number, day: number, hour: number, minute: number, timeZone: string) {
  const guess = Date.UTC(year, month - 1, day, hour, minute)
  // A second pass settles the offset when the first guess landed across a daylight-saving change.
  const first = guess - offsetMinutes(guess, timeZone) * 60000
  return new Date(guess - offsetMinutes(first, timeZone) * 60000)
}

/** An instant as the `yyyy-MM-ddTHH:mm` a datetime-local input shows, in the tenant's zone. */
export function toZonedInput(value: string | null | undefined, timeZone: string): string {
  const d = instant(value)
  if (!d) return ''
  const p = partsIn(d, timeZone)
  return `${p.year}-${pad(p.month)}-${pad(p.day)}T${pad(p.hour)}:${pad(p.minute)}`
}

const WALL = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/

/** A datetime-local value, read in the tenant's zone, as a UTC ISO instant; null when unreadable. */
export function fromZonedInput(wall: string, timeZone: string): string | null {
  const m = WALL.exec(wall.trim())
  if (!m) return null
  const [year, month, day, hour, minute] = m.slice(1, 6).map(Number)
  return zonedToUtc(year, month, day, hour, minute, timeZone).toISOString()
}

/** The first instant of a tenant day (`yyyy-MM-dd`), for filters and due dates. */
export function startOfDay(day: string, timeZone: string): string | null {
  const m = DAY.exec(day.trim())
  if (!m) return null
  return zonedToUtc(Number(m[1]), Number(m[2]), Number(m[3]), 0, 0, timeZone).toISOString()
}

/** The last millisecond of a tenant day, for inclusive "up to and including" filters and due dates. */
export function endOfDay(day: string, timeZone: string): string | null {
  const m = DAY.exec(day.trim())
  if (!m) return null
  // The next day's midnight in the zone, less a millisecond: days are not always 24 hours long.
  const next = new Date(Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]) + 1))
  const start = zonedToUtc(next.getUTCFullYear(), next.getUTCMonth() + 1, next.getUTCDate(), 0, 0, timeZone)
  return new Date(start.getTime() - 1).toISOString()
}

/** The tenant day an instant falls on, as a date input's `yyyy-MM-dd` value. */
export function dayInZone(value: string | Date | null | undefined, timeZone: string): string {
  const d = instant(value)
  if (!d) return ''
  const p = partsIn(d, timeZone)
  return `${p.year}-${pad(p.month)}-${pad(p.day)}`
}

/** Every zone this browser can show, UTC first; falls back to the tenant's own when it cannot list them. */
export function timeZoneChoices(current: string): string[] {
  let zones: string[] = []
  try {
    zones = Intl.supportedValuesOf('timeZone')
  } catch {
    zones = []
  }
  const all = new Set(['UTC', current, ...zones])
  return [...all]
}
