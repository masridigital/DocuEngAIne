import { InteractionType } from '@azure/msal-browser'
import { MsalAuthenticationTemplate, useMsal, type MsalAuthenticationResult } from '@azure/msal-react'
import type { PropsWithChildren } from 'react'
import { isAuthConfigured, loginRequest, missingAuthConfig } from './msalConfig'

/** Shown when the bundle was built without the Entra environment variables. */
export function AuthConfigNotice() {
  return (
    <div className="app-main">
      <div className="page panel">
        <h1>Sign-in is not configured</h1>
        <p>
          The DocuEngAIne API requires a Microsoft Entra ID access token, but this build has no Entra
          configuration. Set the following environment variable{missingAuthConfig.length === 1 ? '' : 's'} and
          rebuild:
        </p>
        <ul>
          {missingAuthConfig.map((name) => (
            <li key={name}><code>{name}</code></li>
          ))}
        </ul>
        <p className="muted">
          Copy <code>.env.example</code> to <code>.env.local</code> for local development.
        </p>
      </div>
    </div>
  )
}

function AuthLoading() {
  return (
    <div className="app-main">
      <div className="page">
        <p className="muted">Signing in…</p>
      </div>
    </div>
  )
}

function AuthError({ error }: MsalAuthenticationResult) {
  const { instance } = useMsal()
  return (
    <div className="app-main">
      <div className="page panel">
        <h1>Sign-in required</h1>
        {error ? <p className="error">{error.errorMessage || error.message}</p> : null}
        <button type="button" className="btn" onClick={() => void instance.loginRedirect({ ...loginRequest })}>
          Sign in with Microsoft
        </button>
      </div>
    </div>
  )
}

/** Renders children only once an Entra account is signed in. */
export function AuthGate({ children }: PropsWithChildren) {
  if (!isAuthConfigured) return <AuthConfigNotice />
  return (
    <MsalAuthenticationTemplate
      interactionType={InteractionType.Redirect}
      authenticationRequest={{ ...loginRequest }}
      loadingComponent={AuthLoading}
      errorComponent={AuthError}
    >
      {children}
    </MsalAuthenticationTemplate>
  )
}
