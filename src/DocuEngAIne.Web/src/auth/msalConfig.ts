import {
  EventType,
  InteractionRequiredAuthError,
  PublicClientApplication,
  type AuthenticationResult,
  type Configuration,
  type EventMessage,
  type RedirectRequest,
  type SilentRequest,
} from '@azure/msal-browser'

const clientId = (import.meta.env.VITE_ENTRA_CLIENT_ID ?? '').trim()
const authority = (import.meta.env.VITE_ENTRA_AUTHORITY ?? '').trim()
const apiScope = (import.meta.env.VITE_ENTRA_API_SCOPE ?? '').trim()

export const authSettings = { clientId, authority, apiScope }

/** Names of the required build-time env vars that were not supplied. */
export const missingAuthConfig: string[] = [
  clientId ? null : 'VITE_ENTRA_CLIENT_ID',
  authority ? null : 'VITE_ENTRA_AUTHORITY',
  apiScope ? null : 'VITE_ENTRA_API_SCOPE',
].filter((name): name is string => name !== null)

export const isAuthConfigured = missingAuthConfig.length === 0

const redirectUri = typeof window === 'undefined' ? '/' : window.location.origin

export const msalConfig: Configuration = {
  auth: {
    clientId,
    authority,
    redirectUri,
    postLogoutRedirectUri: redirectUri,
  },
  cache: {
    cacheLocation: 'sessionStorage',
  },
}

/** Scopes requested when signing in and when calling the DocuEngAIne API. */
export const apiScopes = apiScope ? [apiScope] : []

export const loginRequest: RedirectRequest = { scopes: apiScopes }

/**
 * Module singleton so plain (non-React) helpers such as the SWR fetcher can
 * acquire tokens without going through the MSAL React hooks.
 * Null when the app was built without Entra configuration.
 */
export const msalInstance: PublicClientApplication | null = isAuthConfigured
  ? new PublicClientApplication(msalConfig)
  : null

let initialization: Promise<void> | null = null
let redirecting = false

/**
 * Initializes MSAL and drains any pending redirect response.
 * Must be awaited before rendering: MSAL v3+ refuses every other call until then.
 */
export function initializeMsal(): Promise<void> {
  const instance = msalInstance
  if (!instance) return Promise.resolve()
  initialization ??= (async () => {
    await instance.initialize()
    const result = await instance.handleRedirectPromise()
    if (result?.account) {
      instance.setActiveAccount(result.account)
    } else if (!instance.getActiveAccount()) {
      const [account] = instance.getAllAccounts()
      if (account) instance.setActiveAccount(account)
    }
    instance.addEventCallback((message: EventMessage) => {
      if (message.eventType === EventType.LOGIN_SUCCESS || message.eventType === EventType.ACQUIRE_TOKEN_SUCCESS) {
        const payload = message.payload as AuthenticationResult | null
        if (payload?.account) instance.setActiveAccount(payload.account)
      }
    })
  })()
  return initialization
}

/**
 * Thrown while the browser is being handed off to Entra. It exists so callers
 * (and SWR) surface something readable instead of hanging on a dead promise.
 */
export class InteractionInProgressError extends Error {
  constructor() {
    super('Redirecting to sign in…')
    this.name = 'InteractionInProgressError'
  }
}

async function redirectToSignIn(request: RedirectRequest): Promise<never> {
  if (!redirecting) {
    redirecting = true
    try {
      await msalInstance?.acquireTokenRedirect(request)
    } catch (err) {
      // acquireTokenRedirect normally never returns -- the page navigates away. If it rejects
      // instead (a concurrent interaction, a blocked navigation), the latch must be released or
      // every later acquisition throws without anyone ever being sent to sign in, until a reload.
      redirecting = false
      throw err
    }
  }
  throw new InteractionInProgressError()
}

/**
 * Acquires an access token for the API scope, silently where possible and via a
 * full-page redirect when Entra requires interaction.
 */
export async function acquireApiToken(): Promise<string> {
  const instance = msalInstance
  if (!instance) {
    throw new Error(`Authentication is not configured. Missing: ${missingAuthConfig.join(', ')}`)
  }
  await initializeMsal()

  const account = instance.getActiveAccount() ?? instance.getAllAccounts()[0] ?? null
  if (!account) return redirectToSignIn({ ...loginRequest })

  const silentRequest: SilentRequest = { scopes: apiScopes, account }
  try {
    const result = await instance.acquireTokenSilent(silentRequest)
    return result.accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      return redirectToSignIn({ ...loginRequest, account })
    }
    throw error
  }
}
