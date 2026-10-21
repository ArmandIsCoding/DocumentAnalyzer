# Evaluation plan: improve correctness, not a dashboard number

## Define the target with the business

Agree whether “98%” means field correctness on all invoices, invoice-level correctness, or precision only among automatically accepted invoices. These are different targets. Fifteen fields at 98% individual accuracy do not imply 98% completely correct invoices. Define required fields, genuinely optional fields, critical totals/tax IDs, currencies, credit notes, and acceptable formatting in advance.

The dashboard reports Azure confidence, coverage, policy readiness, and measured accuracy separately. A high-confidence error still counts as an error. Missing predictions count as errors for fields known to be present. Verified absences are reported separately so sparse invoices cannot inflate value accuracy. Failed latest executions remain in the labeled evaluation denominator.

## Build the reference set inside the VM

1. Sample original invoices across vendors, layouts, languages, countries, currencies, page counts, scan quality, and time periods. Include failures and difficult cases, not only clean examples. Keep every file in the client environment.
2. Split by invoice and preferably by vendor/template/time group before tuning. Similar templates in both sets can exaggerate generalization. The application enforces invoice-hash split consistency; you must enforce vendor/template grouping operationally.
3. Use Calibration to select strategies and thresholds. Keep Test untouched until policy choices are frozen. If Test informs changes, create a genuinely new holdout before claiming final performance.
4. Review original PDFs, independently from model predictions where practical. Double-review a subset and adjudicate disagreements, especially for totals, IDs, dates, and currency.
5. Label Present=false only after checking the source. Use ISO dates (`2026-10-20`), invariant decimal amounts (`1234.56`), and uppercase codes (`USD`). A missing label is unmeasured, not a correct absence.

After a pilot extraction, export a blank template:

```powershell
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- labels-template reference-labels.json
# Review originals and complete Present, ExpectedValue, Reviewer, and Split in the VM.
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- labels-import reference-labels.json
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- evaluate --split Calibration
dotnet run --project src/InvoiceExplorer.Cli -c Release --no-build -- evaluate --split Test
```

Templates intentionally do not prefill expected values with predictions. Remove unreviewed rows before import; an incomplete import is rejected atomically. The UI can also record one field at a time. Correct a mistaken label by adding a revision in the same split. Neither mechanism changes Azure output or its confidence.

## Experiments worth running

| Experiment | Configuration change | Question it answers |
| --- | --- | --- |
| Original baseline | QueryMode Off, HighResolutionFallback false, RasterFallback false, CustomModelId empty, MaxPasses 1 | What does prebuilt-invoice get right on the original bytes? |
| Targeted query | AllowPaidFeatures true, QueryMode Fallback, MaxPasses 2; other fallbacks off | Do query fields recover missing/uncertain targets without creating excessive conflicts? |
| High-resolution OCR | QueryMode Off, HighResolutionFallback true, paid gate, MaxPasses 2 | Are failures caused by small or difficult text? |
| Rendered scan | QueryMode Off, HighResolutionFallback false, RasterFallback true, MaxPasses 2 | Does controlled re-rendering help this scan population? |
| Vendor custom model | CustomModelId set, other fallbacks off, MaxPasses 2 | Does representative supervised training improve field assignment? |

Use a new execution (`extract ... --rerun`) for each configuration. Keep the source PDFs unchanged. Each execution stores a redacted configuration snapshot and fingerprint. The evaluation compares the final selection with the Original pass from the same execution on the same labels. Export reports after each experiment before the next becomes “latest”. The current CLI does not implement automatic threshold tuning or hyperparameter sweeps; choices should be justified by the calibration results.

Inspect failures by field and vendor. If OCR is clean but VendorTaxId and CustomerTaxId are confused, image conversion is unlikely to fix semantics. Consider a trained neural extraction model with representative annotations. If text is illegible, obtain a better scan; rendering at 300 DPI cannot invent missing detail. If a native digital PDF works well, retain its text layer.

## Acceptance evidence

Report at least:

- Number of invoices, reviewed present fields, and verified absent fields; vendor/template distribution.
- Baseline versus final value accuracy, overall and by target field; critical-field errors separately.
- Ready invoice count/rate, review rate, failed/interrupted count, and target coverage.
- Precision of the automatically accepted subset, alongside its size. Rejecting almost everything can produce misleadingly high precision.
- Requests/pages and approximate actual Azure cost, elapsed time, and fallback usage.
- Error examples and the approved human review procedure for unresolved invoices.

`evaluation-*.json` and CSV expose per-field outcomes for grouping in the client's reporting tools. The UI's Wilson 95% lower bound is descriptive and assumes independent Bernoulli observations; fields within an invoice are correlated. Do not use it alone to certify production performance. Prefer invoice-level and vendor/template-level analysis, and an independently reviewed sample sized for the business decision. Even 100 correct independent observations out of 100 have a Wilson lower bound below 98%.

Once the policy is frozen, run the held-out test and retain both successes and failures. Roll out with human review on exceptions and an ongoing random audit of Ready invoices. A target may be attainable for some fields/vendors and not others; document those limits instead of averaging them away.
