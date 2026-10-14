import type {
  LocationQuery,
  LocationQueryRaw,
  RouteLocationNormalizedLoaded,
  Router,
} from 'vue-router'
import { onBeforeUnmount, onMounted } from 'vue'

import { getPdaBridge } from '@/bridge/pda-bridge'

const documentReturnMarker = 'documentReturn'

export function useDocumentSystemBack(handler: () => void | Promise<void>): void {
  let stopListening: (() => void) | null = null

  onMounted(() => {
    stopListening = getPdaBridge().subscribeBack(() => {
      void handler()
    })
  })

  onBeforeUnmount(() => {
    stopListening?.()
  })
}

export function markDocumentSubpage(query: LocationQuery): LocationQueryRaw {
  return { ...query, [documentReturnMarker]: '1' }
}

function clearDocumentReturnMarker(query: LocationQuery): LocationQueryRaw {
  const result: LocationQueryRaw = { ...query }
  delete result[documentReturnMarker]
  return result
}

export async function returnToParentDocument(
  router: Router,
  route: RouteLocationNormalizedLoaded,
  documentType: string,
): Promise<void> {
  const previousRoute = (window.history.state as { back?: unknown } | null)?.back
  if (route.query[documentReturnMarker] === '1' && typeof previousRoute === 'string') {
    router.back()
    return
  }

  await router.replace({
    name: 'work-function',
    params: { code: documentType },
    query: clearDocumentReturnMarker(route.query),
  })
}
