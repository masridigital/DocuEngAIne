import useSWR from 'swr'

const fetcher = (url: string) => fetch(url).then((r) => r.json())

export function useProfile() {
  return useSWR('/api/me', fetcher)
}

export function useAssets() {
  return useSWR('/api/assets', fetcher)
}

export function useDocuments() {
  return useSWR('/api/documents', fetcher)
}

export function useRunbooks() {
  return useSWR('/api/runbooks', fetcher)
}

export function useKeeperLinks() {
  return useSWR('/api/keeper', fetcher)
}
