/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_ENTRA_CLIENT_ID?: string
  readonly VITE_ENTRA_AUTHORITY?: string
  readonly VITE_ENTRA_API_SCOPE?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
