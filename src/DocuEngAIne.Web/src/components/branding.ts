/** The app's page background; an accent is drawn on it (links) and under dark text (buttons). */
export const APP_BACKGROUND = '#0f172a'

/** WCAG AA for normal text — the same rule the API applies. */
export const MINIMUM_CONTRAST = 4.5

const HEX_COLOR = /^#[0-9a-f]{6}$/i

export function isHexColor(value: string | null | undefined): value is string {
  return typeof value === 'string' && HEX_COLOR.test(value)
}

function luminance(hex: string) {
  const channel = (offset: number) => {
    const value = Number.parseInt(hex.slice(offset, offset + 2), 16) / 255
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4
  }
  return 0.2126 * channel(1) + 0.7152 * channel(3) + 0.0722 * channel(5)
}

/** WCAG 2 contrast ratio between two `#rrggbb` colors, from 1 to 21. */
export function contrastRatio(first: string, second: string) {
  const a = luminance(first)
  const b = luminance(second)
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
}

/** Sets (or, with null, removes) the tenant accent on the page's CSS variables. */
export function applyAccent(color: string | null | undefined) {
  const root = document.documentElement
  if (isHexColor(color)) {
    root.style.setProperty('--accent', color)
    root.style.setProperty('--accent-hover', `color-mix(in srgb, ${color} 85%, black)`)
  } else {
    root.style.removeProperty('--accent')
    root.style.removeProperty('--accent-hover')
  }
}
