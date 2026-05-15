<!-- docs/superpowers/specs/spikes/2026-05-15-faceemo-bridge-spike.md -->
# FaceEmo Bridge Spike Results

Date: 2026-05-15
Spec: ../2026-05-15-faceemo-realtime-bridge-design.md §11

## 0.2 IExpressionEditor DI resolution
Status: TBD (filled in Task 0.2)
Notes:

## 0.3 ExpressionEditorModelFacade access
Status: TBD (filled in Task 0.3)
Notes:

## 0.4 SetBlendShapeValue live reflection
Status: TBD (filled in Task 0.4)
Notes:

## Decision

| Spike | Result |
|---|---|
| 0.2 IExpressionEditor DI | PASS / FAIL |
| 0.3 Facade access | PASS / FAIL |
| 0.4 SetBlendShapeValue live | PASS / PARTIAL / FAIL |

### Implementation path
- [ ] **Full Live + Degraded** (0.2-0.4 all PASS): Proceed with Phase 2-5 as written.
- [ ] **Degraded only** (any of 0.2-0.4 FAIL): Skip Phase 2 Tasks 2.4/2.5. Bridge.TryOpen still possible (for PreviewWindow display) but SetBlendShape always returns false → Session uses AssetPathFallback only.
- [ ] **Abort** (0.2 itself fails): FaceEmo パッケージ構造が想定と完全に異なる。仕様を見直す。

Selected path: ____________________
