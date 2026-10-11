import { getPdaBridge } from '@/bridge/pda-bridge'
import { readSession } from '@/services/auth'
import {
  markOutboundDocumentUploaded,
  markOutboundDocumentUploadFailed,
  type StoredOutboundDocument,
} from '@/services/outbound-document-drafts'
import { getRuntimeConfig } from '@/services/runtime-config'

interface ApiEnvelope<T> {
  success: boolean
  message?: string
  requestId?: string
  data?: T
}

interface UploadResult {
  operationId?: string
}

export async function uploadOutboundDocument(
  document: StoredOutboundDocument,
  autoApprove: boolean,
): Promise<StoredOutboundDocument> {
  const config = getRuntimeConfig()
  try {
    if (config.environment === 'development' && config.enableMockLogin) {
      return markOutboundDocumentUploaded(document.id, `mock-${document.id}`)
    }

    const session = readSession()
    const bridge = getPdaBridge()
    const response = await fetch(
      `${config.apiBaseUrl}/api/v1/business-documents/${document.documentType}`,
      {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-Client-Request-Id': document.id,
        },
        body: JSON.stringify({
          clientRequestId: document.id,
          operatorName: session?.username ?? '',
          h5Version: config.h5Version,
          bridgeVersion: bridge.protocolVersion,
          autoApprove,
          payload: document.payload,
        }),
      },
    )
    const envelope = (await response.json()) as ApiEnvelope<UploadResult>
    if (!response.ok || !envelope.success) {
      throw new Error(envelope.message || '上传失败')
    }
    return markOutboundDocumentUploaded(
      document.id,
      envelope.data?.operationId || envelope.requestId || document.id,
    )
  } catch (error) {
    const message = error instanceof Error ? error.message : '上传失败'
    markOutboundDocumentUploadFailed(document.id, message)
    throw new Error(message)
  }
}
