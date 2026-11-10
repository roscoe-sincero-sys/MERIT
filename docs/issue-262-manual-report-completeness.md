# Issue #262: manual laboratory report completeness

Manual entry transcribes selected test fields. It does not ask staff to inventory every field on the original laboratory report. An absent field in that transcription therefore cannot establish that the source report is incomplete. Patient-chart demographics also cannot establish what the laboratory reported.

## Decision

Use the issue's alternative of skipping automatic missing-information evaluation for manual entry. Identify manual results through `intakeBatch.sourceChannel`, not the JSON `source` marker. No schema change or migration of patient data is needed.

The evaluator first authorizes the result under the caller's clinical scope. An internal owner-executed procedure then reads only the batch's manual-channel flag for that result's batch and root. It is granted to no application role and returns no report content. This preserves provenance when a clinical profile can see an assigned result but cannot see the originating intake batch. Direct calls remain denied.

The manual entry form and result detail explain the limitation and direct staff to review the original and record follow-up in the patient's activity history. This is not an attestation that the original report is complete. A structured original-report inventory would need a separately designed, explicit staff review workflow; chart values must never silently become source evidence.

CSV/HL7 and held-row completeness evaluation continue using mapped source fields and corrections. An imported JSON marker claiming manual entry does not exempt that report. Laboratory reporting eligibility, final-result dates, same-day cutoff review and late-report detection remain independent and continue for manual results. Maryland residence is not inferred from today's patient chart; existing explicit eligibility/timeline verification remains required where the reported source does not establish it.

## Existing findings

Deployment and re-evaluation do not delete, dismiss or rewrite previously detected missing-information findings or their dates. Pending requests remain visible. Staff must review the original report and use **Missing Information Received** to record actual receipt/verification date and evidence, confirming all requested information has been received or verified. That existing action records the reviewer, resolution note and audit event. Do not bulk-complete old findings or substitute a deployment date for actual receipt.

## Validation

`ManualReceiptCompletenessTests` activates each shipped Maryland lab-reporting draft with a synthetic test calendar. For both CSV and HL7 comparisons it exercises real intake processing and manual entry services, checks original demographic evidence is not fabricated, checks missing/unmapped imported fields remain actionable despite a spoofed JSON marker, checks a receiving clinical profile with a hidden intake batch and denies direct provenance-helper calls, preserves pre-fix finding dates, explicitly resolves a pending request with source evidence, and verifies manual eligibility/timeline review still controls late-report detection. Existing receipt-review fixtures now explicitly create imported batches rather than inheriting the demo result's channel.

These tests verify application behavior, not legal approval of a reporting policy or calendar. Deploy regenerated `dbo.uspIsManualReceiptSource` and `dbo.uspReconcileReceiptQuality` with the standard database deployment process. No existing receipt records require automated cleanup.
