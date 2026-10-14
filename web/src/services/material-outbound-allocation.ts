import type { MaterialRequirementLine } from '@/services/source-document-selection'

export interface SourceQuantityAllocation {
  requirementId: number
  sourceDocumentId: number
  sourceDocumentNo: string
  sourceLineId: number
  sourceLineNo: number | null
  productCode: string
  productName: string
  normalQty: number
  overQty: number
}

export interface MaterialScanLine {
  id: string
  materialCode: string
  requestedQty: number
}

export interface AllocatedMaterialScanLine {
  allocations: SourceQuantityAllocation[]
  unallocatedQty: number
  requiresOverIssueTarget: boolean
}

export interface MaterialAllocationSummary {
  materialCode: string
  materialName: string
  plannedQty: number
  issuedQty: number
  remainingQty: number
  scannedQty: number
  normalQty: number
  overQty: number
  unallocatedQty: number
  sourceLineCount: number
}

const precision = 1_000_000

function qty(value: number): number {
  if (!Number.isFinite(value) || value <= 0) return 0
  return Math.round(value * precision) / precision
}

function materialKey(value: string): string {
  return value.trim().toLocaleUpperCase()
}

function addAllocation(
  allocations: SourceQuantityAllocation[],
  requirement: MaterialRequirementLine,
  normalQty: number,
  overQty: number,
): void {
  const existing = allocations.find(
    (allocation) => allocation.requirementId === requirement.requirementId,
  )
  if (existing) {
    existing.normalQty = qty(existing.normalQty + normalQty)
    existing.overQty = qty(existing.overQty + overQty)
    return
  }
  allocations.push({
    requirementId: requirement.requirementId,
    sourceDocumentId: requirement.sourceDocumentId,
    sourceDocumentNo: requirement.sourceDocumentNo,
    sourceLineId: requirement.sourceLineId,
    sourceLineNo: requirement.sourceLineNo,
    productCode: requirement.productCode,
    productName: requirement.productName,
    normalQty: qty(normalQty),
    overQty: qty(overQty),
  })
}

export function allocateMaterialScans(
  lines: MaterialScanLine[],
  requirements: MaterialRequirementLine[],
  allowOverIssue: boolean,
  overIssueTargets: Record<string, number>,
  quantityBasis: 'remaining' | 'issued' = 'remaining',
  requirementTargets: Record<string, number> = {},
): Record<string, AllocatedMaterialScanLine> {
  const result: Record<string, AllocatedMaterialScanLine> = {}
  const requirementGroups = new Map<string, MaterialRequirementLine[]>()
  for (const requirement of requirements) {
    const key = materialKey(requirement.materialCode)
    const group = requirementGroups.get(key) ?? []
    group.push(requirement)
    requirementGroups.set(key, group)
  }
  for (const group of requirementGroups.values()) {
    group.sort((left, right) =>
      left.sourceDocumentNo.localeCompare(right.sourceDocumentNo) ||
      (left.sourceLineNo ?? 0) - (right.sourceLineNo ?? 0) ||
      left.requirementId - right.requirementId,
    )
  }

  const remaining = new Map<number, number>()
  for (const requirement of requirements) {
    remaining.set(
      requirement.requirementId,
      qty(quantityBasis === 'issued' ? requirement.issuedQuantity : requirement.remainingQuantity),
    )
  }

  for (const line of lines) {
    const allocations: SourceQuantityAllocation[] = []
    const materialMatches = requirementGroups.get(materialKey(line.materialCode)) ?? []
    const requirementTarget = requirementTargets[line.id]
    const matches = requirementTarget
      ? materialMatches.filter((item) => item.requirementId === requirementTarget)
      : materialMatches
    let pending = qty(line.requestedQty)
    for (const requirement of matches) {
      if (pending <= 0) break
      const available = remaining.get(requirement.requirementId) ?? 0
      if (available <= 0) continue
      const allocated = Math.min(pending, available)
      addAllocation(allocations, requirement, allocated, 0)
      remaining.set(requirement.requirementId, qty(available - allocated))
      pending = qty(pending - allocated)
    }

    let requiresOverIssueTarget = false
    if (pending > 0 && allowOverIssue && matches.length) {
      const targetId = overIssueTargets[line.id]
      const target = matches.length === 1
        ? matches[0]
        : matches.find((requirement) => requirement.requirementId === targetId)
      if (target) {
        addAllocation(allocations, target, 0, pending)
        pending = 0
      } else {
        requiresOverIssueTarget = true
      }
    }
    result[line.id] = {
      allocations,
      unallocatedQty: pending,
      requiresOverIssueTarget,
    }
  }
  return result
}

export function summarizeMaterialAllocations(
  lines: MaterialScanLine[],
  requirements: MaterialRequirementLine[],
  allocated: Record<string, AllocatedMaterialScanLine>,
): MaterialAllocationSummary[] {
  const codes = new Map<string, string>()
  for (const line of [...requirements, ...lines]) {
    const key = materialKey(line.materialCode)
    if (!codes.has(key)) codes.set(key, line.materialCode)
  }
  return [...codes].map(([key, materialCode]) => {
    const requirementLines = requirements.filter((line) => materialKey(line.materialCode) === key)
    const scanLines = lines.filter((line) => materialKey(line.materialCode) === key)
    const allocations = scanLines.flatMap((line) => allocated[line.id]?.allocations ?? [])
    return {
      materialCode,
      materialName: requirementLines[0]?.materialName ?? '',
      plannedQty: qty(requirementLines.reduce((sum, line) => sum + line.plannedQuantity, 0)),
      issuedQty: qty(requirementLines.reduce((sum, line) => sum + line.issuedQuantity, 0)),
      remainingQty: qty(requirementLines.reduce((sum, line) => sum + line.remainingQuantity, 0)),
      scannedQty: qty(scanLines.reduce((sum, line) => sum + qty(line.requestedQty), 0)),
      normalQty: qty(allocations.reduce((sum, line) => sum + line.normalQty, 0)),
      overQty: qty(allocations.reduce((sum, line) => sum + line.overQty, 0)),
      unallocatedQty: qty(scanLines.reduce(
        (sum, line) => sum + (allocated[line.id]?.unallocatedQty ?? 0), 0,
      )),
      sourceLineCount: new Set(requirementLines.map((line) => line.sourceLineId)).size,
    }
  })
}
