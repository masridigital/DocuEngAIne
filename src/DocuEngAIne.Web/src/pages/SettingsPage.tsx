import { useState, type FormEvent } from 'react'
import { APP_BACKGROUND, contrastRatio, isHexColor, MINIMUM_CONTRAST } from '../components/branding'
import { DEFAULT_REGIONAL, formatDateTime, timeZoneChoices } from '../components/regional'
import {
  canManageUsers,
  DEFAULT_PRODUCT_NAME,
  setTenantBranding,
  setTenantFeature,
  setTenantRegional,
  setTenantTerminology,
  useProfile,
  useTenantConfiguration,
  type TenantConfiguration,
  type TenantFeature,
  type TenantRegional,
  type TenantTerm,
} from '../hooks/useApi'

const DEFAULT_ACCENT = '#38bdf8'

function Features(props: { features: TenantFeature[] }) {
  const [message, setMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  async function toggle(feature: TenantFeature) {
    const next = !feature.enabled
    if (!next && !window.confirm(`Turn off ${feature.name}? Everyone in the tenant loses it until it is turned back on.`)) return
    setMessage(null)
    setBusy(feature.key)
    try {
      await setTenantFeature(feature.key, next)
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Failed to change the feature.')
    } finally {
      setBusy(null)
    }
  }

  return (
    <section className="panel">
      <h2>Features</h2>
      <p className="muted">
        Optional modules. A feature that is off is refused for everyone in the tenant, and its data is kept. Sign-in,
        users, roles, security groups, audit and IP access are always on.
      </p>
      {message && <p className="error">{message}</p>}
      <table className="data-table">
        <thead>
          <tr>
            <th>Feature</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {props.features.map((f) => (
            <tr key={f.key}>
              <td>
                {f.name}
                <div className="muted">{f.description}</div>
              </td>
              <td>{f.enabled ? 'On' : 'Off'}</td>
              <td>
                <button className="btn" type="button" disabled={busy !== null} onClick={() => void toggle(f)}>
                  {busy === f.key ? 'Saving…' : f.enabled ? 'Turn off' : 'Turn on'}
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  )
}

function Names(props: { terms: TenantTerm[] }) {
  const [drafts, setDrafts] = useState<Record<string, { singular: string; plural: string }>>(() =>
    Object.fromEntries(props.terms.map((t) => [t.key, { singular: t.singular, plural: t.plural }])),
  )
  const [message, setMessage] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  function update(key: string, form: 'singular' | 'plural', value: string) {
    setSaved(false)
    setDrafts((prev) => ({ ...prev, [key]: { ...prev[key], [form]: value } }))
  }

  function reset(term: TenantTerm) {
    setSaved(false)
    setDrafts((prev) => ({ ...prev, [term.key]: { singular: term.defaultSingular, plural: term.defaultPlural } }))
  }

  async function save(e: FormEvent) {
    e.preventDefault()
    // Only what changed is sent; a term put back to its default is sent as null (restore).
    const changed: Record<string, { singular: string; plural: string } | null> = {}
    for (const term of props.terms) {
      const draft = drafts[term.key]
      if (!draft) continue
      const singular = draft.singular.trim()
      const plural = draft.plural.trim()
      if (singular === term.singular && plural === term.plural) continue
      changed[term.key] =
        singular === term.defaultSingular && plural === term.defaultPlural ? null : { singular, plural }
    }
    if (Object.keys(changed).length === 0) return

    setMessage(null)
    setBusy(true)
    try {
      await setTenantTerminology(changed)
      setSaved(true)
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Failed to save the names.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel" onSubmit={save}>
      <h2>Names</h2>
      <p className="muted">What the app calls things, for everyone in the tenant — for example Clients instead of Companies.</p>
      {message && <p className="error">{message}</p>}
      <table className="data-table">
        <thead>
          <tr>
            <th>Names</th>
            <th>Singular</th>
            <th>Plural</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {props.terms.map((t) => (
            <tr key={t.key}>
              <td>
                {t.defaultPlural}
                <div className="muted">{t.describes}</div>
              </td>
              <td>
                <input
                  className="input"
                  value={drafts[t.key]?.singular ?? ''}
                  maxLength={40}
                  required
                  aria-label={`${t.defaultSingular}, singular`}
                  onChange={(e) => update(t.key, 'singular', e.target.value)}
                />
              </td>
              <td>
                <input
                  className="input"
                  value={drafts[t.key]?.plural ?? ''}
                  maxLength={40}
                  required
                  aria-label={`${t.defaultPlural}, plural`}
                  onChange={(e) => update(t.key, 'plural', e.target.value)}
                />
              </td>
              <td>
                <button className="btn btn-secondary" type="button" disabled={busy} onClick={() => reset(t)}>
                  Default
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="toolbar">
        <button className="btn" type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Save names'}
        </button>{' '}
        {saved ? <span className="muted">Saved.</span> : null}
      </div>
    </form>
  )
}

function Branding(props: { branding: TenantConfiguration['branding'] }) {
  const [displayName, setDisplayName] = useState(props.branding.displayName ?? '')
  const [accent, setAccent] = useState(props.branding.accentColor ?? '')
  const [message, setMessage] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)

  const color = accent.trim()
  const valid = color === '' || isHexColor(color)
  const ratio = isHexColor(color) ? contrastRatio(color, APP_BACKGROUND) : null
  const readable = ratio === null || ratio >= MINIMUM_CONTRAST

  async function save(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    setBusy(true)
    try {
      await setTenantBranding({ displayName: displayName.trim() || null, accentColor: color || null })
      setSaved(true)
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Failed to save the branding.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel" onSubmit={save}>
      <h2>Branding</h2>
      <p className="muted">
        The name in the header and the color of links and buttons. The color must stay readable on the dark background
        (contrast {MINIMUM_CONTRAST}:1 or more). Logos wait on file storage.
      </p>
      {message && <p className="error">{message}</p>}
      <div className="form-grid">
        <label>
          Name in the header
          <input
            className="input"
            value={displayName}
            maxLength={80}
            placeholder={DEFAULT_PRODUCT_NAME}
            onChange={(e) => {
              setSaved(false)
              setDisplayName(e.target.value)
            }}
          />
        </label>
        <label>
          Accent color
          <input
            className="input"
            value={accent}
            placeholder={DEFAULT_ACCENT}
            maxLength={7}
            onChange={(e) => {
              setSaved(false)
              setAccent(e.target.value)
            }}
          />
        </label>
        <label>
          Pick
          <input
            type="color"
            value={isHexColor(color) ? color : DEFAULT_ACCENT}
            onChange={(e) => {
              setSaved(false)
              setAccent(e.target.value)
            }}
          />
        </label>
      </div>
      <p>
        {!valid ? (
          <span className="error">Write the color like {DEFAULT_ACCENT}.</span>
        ) : ratio !== null ? (
          <span className={readable ? 'muted' : 'error'}>
            Contrast {ratio.toFixed(1)}:1 {readable ? '— readable.' : '— too dark to read on the background.'}
          </span>
        ) : (
          <span className="muted">No accent set: the default is used.</span>
        )}{' '}
        {isHexColor(color) ? (
          <span className="tag" style={{ background: color, color: APP_BACKGROUND }}>
            Button text
          </span>
        ) : null}
      </p>
      <div className="toolbar">
        <button className="btn" type="submit" disabled={busy || !valid || !readable}>
          {busy ? 'Saving…' : 'Save branding'}
        </button>{' '}
        <button
          className="btn btn-secondary"
          type="button"
          disabled={busy}
          onClick={() => {
            setSaved(false)
            setDisplayName('')
            setAccent('')
          }}
        >
          Use defaults
        </button>{' '}
        {saved ? <span className="muted">Saved.</span> : null}
      </div>
    </form>
  )
}

function Regional(props: { regional: TenantRegional }) {
  const [timeZone, setTimeZone] = useState(props.regional.timeZone)
  const [dateFormat, setDateFormat] = useState(props.regional.dateFormat)
  const [timeFormat, setTimeFormat] = useState(props.regional.timeFormat)
  const [message, setMessage] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [now] = useState(() => new Date())

  const zones = timeZoneChoices(timeZone)
  const preview = formatDateTime(now, { timeZone, dateFormat, timeFormat })

  async function save(e: FormEvent) {
    e.preventDefault()
    setMessage(null)
    setBusy(true)
    try {
      await setTenantRegional({ timeZone, dateFormat, timeFormat })
      setSaved(true)
    } catch (err) {
      setMessage(err instanceof Error ? err.message : 'Failed to save the regional settings.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="panel" onSubmit={save}>
      <h2>Time zone and formats</h2>
      <p className="muted">
        Every time in the app is shown in this zone, whatever each person's own device is set to, and what they type
        into a date or time box is read in it. It also decides what "today" is for expirations. Exports stay in UTC.
      </p>
      {message && <p className="error">{message}</p>}
      <div className="form-grid">
        <label>
          Time zone
          <select
            className="input"
            value={timeZone}
            onChange={(e) => {
              setSaved(false)
              setTimeZone(e.target.value)
            }}
          >
            {zones.map((z) => (
              <option key={z} value={z}>
                {z}
              </option>
            ))}
          </select>
        </label>
        <label>
          Dates
          <select
            className="input"
            value={dateFormat}
            onChange={(e) => {
              setSaved(false)
              setDateFormat(e.target.value)
            }}
          >
            {props.regional.dateFormats.map((f) => (
              <option key={f.key} value={f.key}>
                {f.example}
              </option>
            ))}
          </select>
        </label>
        <label>
          Times
          <select
            className="input"
            value={timeFormat}
            onChange={(e) => {
              setSaved(false)
              setTimeFormat(e.target.value)
            }}
          >
            {props.regional.timeFormats.map((f) => (
              <option key={f.key} value={f.key}>
                {f.name} ({f.example})
              </option>
            ))}
          </select>
        </label>
      </div>
      <p className="muted">When this page opened, it was {preview} there.</p>
      <div className="toolbar">
        <button className="btn" type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Save'}
        </button>{' '}
        <button
          className="btn btn-secondary"
          type="button"
          disabled={busy}
          onClick={() => {
            setSaved(false)
            setTimeZone(DEFAULT_REGIONAL.timeZone)
            setDateFormat(DEFAULT_REGIONAL.dateFormat)
            setTimeFormat(DEFAULT_REGIONAL.timeFormat)
          }}
        >
          Use defaults
        </button>{' '}
        {saved ? <span className="muted">Saved.</span> : null}
      </div>
    </form>
  )
}

export function SettingsPage() {
  const { data: profile, isLoading: profileLoading } = useProfile()
  const allowed = canManageUsers(profile?.role)
  const { data, error, isLoading } = useTenantConfiguration()

  if (profileLoading || isLoading) {
    return (
      <div className="page">
        <h1>Settings</h1>
        <p>Loading…</p>
      </div>
    )
  }

  if (!allowed) {
    return (
      <div className="page">
        <h1>Settings</h1>
        <p>Tenant settings are available to Admin and Owner.</p>
      </div>
    )
  }

  if (error || !data) {
    return (
      <div className="page">
        <h1>Settings</h1>
        <p className="error">Failed to load the tenant settings.</p>
      </div>
    )
  }

  return (
    <div className="page">
      <h1>Settings</h1>
      <p>Tenant-wide settings. Every change is recorded in the audit log with what it changed from.</p>
      <Features features={data.features} />
      <Names terms={data.terminology} />
      <Branding branding={data.branding} />
      {data.regional ? <Regional regional={data.regional} /> : null}
    </div>
  )
}
