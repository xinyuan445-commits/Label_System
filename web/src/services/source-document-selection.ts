import type { BusinessDocumentType } from '@/services/business-document-config'

export type OutboundDocumentType = BusinessDocumentType

export interface SourceDocumentSelection {
  outboundDocumentType: OutboundDocumentType
  sourceType: string
  sourceTypeLabel: string
  documentNo: string
  documentDate: string
}

const selectionKey = 'label-system:source-document-selection'

export function saveSourceDocumentSelection(selection: SourceDocumentSelection): void {
  sessionStorage.setItem(selectionKey, JSON.stringify(selection))
}

export function consumeSourceDocumentSelection(
  outboundDocumentType: OutboundDocumentType,
): SourceDocumentSelection | null {
  const raw = sessionStorage.getItem(selectionKey)
  if (!raw) return null

  sessionStorage.removeItem(selectionKey)

  try {
    const selection = JSON.parse(raw) as Partial<SourceDocumentSelection>
    if (
      selection.outboundDocumentType !== outboundDocumentType ||
      !selection.sourceType ||
      !selection.sourceTypeLabel ||
      !selection.documentNo
    ) {
      return null
    }

    return selection as SourceDocumentSelection
  } catch {
    return null
  }
}
