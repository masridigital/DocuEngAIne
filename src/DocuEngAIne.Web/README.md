# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some Oxlint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the Oxlint configuration

If you are developing a production application, we recommend enabling type-aware lint rules by installing `oxlint-tsgolint` and editing `.oxlintrc.json`:

```json
{
  "$schema": "./node_modules/oxlint/configuration_schema.json",
  "plugins": ["react", "typescript", "oxc"],
  "options": {
    "typeAware": true
  },
  "rules": {
    "react/rules-of-hooks": "error",
    "react/only-export-components": ["warn", { "allowConstantExport": true }]
  }
}
```

See the [Oxlint rules documentation](https://oxc.rs/docs/guide/usage/linter/rules) for the full list of rules and categories.

## Authentication (Microsoft Entra ID)

Every API endpoint requires a bearer token, so the SPA signs in with MSAL
(`@azure/msal-browser` + `@azure/msal-react`) using the redirect flow and attaches
an access token to every request.

Configure these build-time variables (see `.env.example`; copy it to `.env.local`
for local development):

| Variable | Example |
| --- | --- |
| `VITE_ENTRA_CLIENT_ID` | `00000000-0000-0000-0000-000000000000` |
| `VITE_ENTRA_AUTHORITY` | `https://login.microsoftonline.com/{tenant-id}/v2.0` |
| `VITE_ENTRA_API_SCOPE` | `api://{api-client-id}/access` |

The app registration must be of type **Single-page application** with the site
origin (e.g. `http://localhost:5173`) registered as a redirect URI.

If any variable is missing the app renders a notice naming the missing variables
instead of failing every request.
