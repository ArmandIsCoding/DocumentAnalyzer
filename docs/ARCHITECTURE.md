# Architecture and implementation walkthrough

## Responsibilities

| File | Responsibility and reason |
| --- | --- |
| `Settings.cs` | Typed defaults, nested JSON/environment overrides, validation, secret-redacted configuration snapshots, and policy fingerprints. |
| `BatchRunner.cs` | Durable manifest, hash deduplication, bounded workers, strategy scheduling, resumption, and explicit ambiguity handling. |
| `AzureInvoiceClient.cs` | Binary REST submission, persisted Operation-Location callback, constrained polling destination, rate pacing, Retry-After, and submission-count budget. |
| `PdfPreparation.cs` | Local PDF characteristics, immutable source snapshots, and optional bounded PDFium rendering plus PdfPig assembly. |
| `QualityEngine.cs` | Business candidate extraction, conservative normalization, confidence preservation, conflicts, and validation. |
| `ExtractionParser.cs` | Generic recursive raw-field parser, including nested line items, OCR, tables, key-value pairs, and source evidence. |
| `InvoiceRepository.cs` | Original invoice/run/field store, raw JSON, full text, and serialized CSV. |
| `PipelineRepository.cs` | Additive batch/execution/attempt/decision/label schema and atomic writes. |
| `Reporting.cs` | Long/wide business CSV and separately labeled baseline/final evaluation. |
| Test `SyntheticInvoiceResponse.cs` | Synthetic responses used exclusively by automated tests; not included in application binaries. |
| CLI `Program.cs` | Argument validation, process lock, command dispatch, exit codes, and cancellation. |
| Web `Dashboard.razor` | Invoice readiness, field coverage, evaluated accuracy, vendor groups, filters, and CSV. |
| Web `Execution.razor` | Original source, ordered execution history, pass trace, candidate evidence, and append-only reference labels. |
| Web `Home.razor` at `/history` | Complete raw history and per-field Azure confidence, including line items beyond the target schema. |

## Three levels of history

An **invoice identity** is a SHA-256 hash of original bytes. An **execution** is an attempt to run a configured pipeline on that source in a batch. An execution has one or more **raw runs**, each corresponding to a strategy/model submission. This prevents a fallback pass from being confused with a separate business invoice.

Within an execution, all attempts retain request JSON, model/API version, start/end timestamps, operation URL, raw response, extracted fields, and CSV. A field decision records its selected run, raw confidence, threshold, state, reason, and every candidate. Repeating a file with `--rerun` creates another execution; historical results remain unchanged. Different binary versions of a visually identical invoice have different hashes. There is intentionally no fuzzy business-ID merge that could combine unrelated invoices.

## Persistence and crash boundaries

SQLite uses WAL, foreign keys, parameterized SQL, a busy timeout, and short transactions. One console owns `*.runner.lock`; Blazor may read and append labels concurrently. The database must be on local storage, not a network share. Additive schema version 2 leaves legacy history intact. Newer schemas are rejected instead of being downgraded.

The console stores a Prepared raw attempt before POST and marks Submitting just before transmission. A confirmed Operation-Location is stored before the first GET. After a restart:

- Succeeded runs reuse their saved JSON without calling Azure.
- A saved operation URL is polled with GET; no duplicate POST is sent.
- Prepared requests rejected by a budget, authentication failure, or exhausted 429 retry may be safely attempted again.
- Submitting/Ambiguous without an operation URL is blocked. A process can crash between Azure accepting a request and the local commit; exactly-once submission cannot be guaranteed across that boundary. Conservative ambiguity prevents silent duplicate billing.
- A timeout or transient polling transport failure retains its URL and leaves the batch resumable.
- A definitive operation failure is retained. An explicit new batch is needed to retry that invoice.

429 responses explicitly reject a request and may be retried. GET 5xx responses may be retried. POST transport failures and POST 5xx are not automatically retried. Every actual POST attempt consumes the durable request-count budget, even a rejected retry. Polling consumes rate allowance but not analyze budget. The rate limiter is shared across workers.

Configuration fingerprints include the API/model, query fields, page settings, retry configuration, and quality policy. Credentials, endpoint, machine paths, worker settings, and the request budget are excluded. This permits key rotation and raising the budget while resuming. Resuming an operation still requires the original resource hostname; polling will reject a different destination rather than sending the credential there.

## Strategy scheduling

| Order | Strategy | Input and trigger |
| --- | --- | --- |
| 1 | Original | Original PDF; configured invoice model; free key-value-pairs feature. Always runs. |
| 2 | QueryFields | Original PDF; prebuilt-layout with queryFields; unresolved configured names or all in Always mode. |
| 3 | HighResolution | Original PDF; configured invoice model with ocrHighResolution; still Review and fallback enabled. |
| 4 | Raster | Lossless page images in a new PDF; still Review and scanned/mixed/poor/unknown OCR; explicitly enabled. |
| 5 | CustomModel | Original PDF; configured already-trained custom model; still Review. |

`MaxPasses` counts strategies, including failed attempts, not HTTP retries. It is intentionally a ceiling; with all strategies enabled, a value of 3 leaves no room for Raster or CustomModel. To compare a custom model early, disable other fallbacks and use 2 passes, or use the custom model as `ModelId` for a separate experiment. Do not enable every option blindly. Compare one change at a time on the calibration set.

Original snapshots are never altered. Rasterization is off by default. It preserves page geometry and page order; optional grayscale preserves luminance. It performs no deskew, perspective correction, language translation, fabricated text, automatic splitting, thresholding, or sharpening. Native rendering is bounded by pages/pixels and serialized per process to limit memory pressure. Input PDFs are bounded at 64 MiB and raster output at 500 MiB. These local memory bounds are independent of service limits enforced by Azure.

Endpoint and API key are the only required configuration values. The REST API version is fixed by the implementation and included in the fingerprint. Default discovery has no pilot cap, and default requests omit the page range to request the complete PDF. Returned page numbers are compared against source pages to detect service-side truncation. Explicit page subsets also block readiness. Query/high-resolution fallbacks are controlled directly by their own options, without a separate tier or feature gate. Policy version 3 invalidates resumption under the previous scheduling defaults; existing results remain readable.

## Selection and validation

The business schema uses the user's 15 target names. PaymentTerms recognizes the prebuilt `PaymentTerm` alias. Typed amounts and ISO dates are preserved. Addresses keep their visible content, with structured raw objects still retained in JSON. Currency may be recovered from an explicit `valueCurrency.currencyCode`; this derived attribute has **no separate confidence**, so it remains Unscored until directly extracted/reviewed.

Candidates from different document indexes are never mixed. More than one detected invoice blocks readiness, even if one invoice seems complete. For each field, format-valid direct candidates are preferred, followed by the highest returned confidence. Distinct normalized values produce Conflict regardless of confidence. Matching passes do not provide independent evidence and do not multiply, add, or otherwise synthesize a new confidence score.

Normalization trims/collapses whitespace, applies Unicode compatibility normalization, and compares text case-insensitively. Decimal amounts compare numerically. Identifier punctuation is retained. Ambiguous date or money strings are not silently reinterpreted. Arithmetic checks are warnings: shipping, discounts, withholding, and credit-note conventions can explain mismatches. They must never be used to rewrite a value just to make the equation balance.

## Storage and output contracts

The legacy `ExtractionRuns.Csv` column contains the complete long-form extraction CSV; all raw JSON and parsed fields also live in SQLite. Pipeline decisions are persisted as structured JSON plus indexed normalized `PipelineFields`. The `report` command produces a long business CSV, a one-invoice-per-row wide CSV, and field metrics JSON. Raw run CSV/JSON downloads remain available. CSV uses invariant numbers, UTF-8, quoted cells, and spreadsheet-formula neutralization.

Labels are a separate append-only reference dataset, not corrected extraction output. Imports validate all rows in one transaction. Unknown sources/fields, invalid typed values, duplicate import entries, and cross-split reuse are rejected. Once an invoice is assigned to Test or Calibration, all its fields and executions remain in that split. The latest label revision is used for evaluation; previous revisions remain auditable.

## Boundaries

There is no LLM correction layer and no external OCR service. Azure Document Intelligence is the only outbound destination for extraction. A custom-model training workflow is intentionally left inside the client's approved Azure environment. The local UI does not submit documents and cannot silently spend quota. Enterprise multi-user access, SSO, role-based approval, encrypted database management, document retention automation, and production infrastructure are separate deployment work.
