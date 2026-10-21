# Validation record

Development validation was performed on Windows x64 with .NET SDK 10.0.402. Tests are deterministic and use fictional data; no client invoice data or Azure credential is required.

## Automated checks

- Release build of Core, CLI, Blazor Server, and Tests.
- Parser and CSV tests, including nested fields, OCR, zero/null confidence, evidence preservation, and spreadsheet-formula neutralization.
- Quality decisions: coverage, required/optional absence, typed formats, per-field thresholds, unmodified confidence, derived currency, date/amount warnings, cross-pass conflicts, and document-index isolation.
- Durable pipeline: selective fallback, early stopping, source integrity, content deduplication, explicit reruns, service-truncated documents, explicit page subsets, configuration changes, optional request budgets, interruption, saved-operation resumption, and ambiguous submission blocking.
- Minimal configuration: endpoint/key only, ignored obsolete tier/API-version settings, full-folder discovery beyond ten inputs, all-page requests, and explicit limit overrides.
- HTTP behavior: original binary request and polling, supported API paths, query-field encoding, 429 retry, POST ambiguity, and credential destination restrictions.
- Repository/evaluation: concurrent budget reservation, append-only labels, atomic imports, immutable dataset split, missing/failed prediction denominators, and confidence interval behavior.
- Legacy synthetic records are excluded from operational history, batches, exports, and evaluation. Synthetic parser/HTTP fixtures are compiled only into the test project.
- Native PDFium/Skia/PdfPig test with a fictional two-page invoice, including page count, dimensions, unchanged original bytes, and rendering limits.
- NuGet vulnerability audit including transitive packages, using currently available feeds.

## Manual integration checks

The current UI is checked with an empty application database: extraction summary, raw history, method documentation, unavailable metrics, and empty CSV exports. Previous integration checks exercised status filtering, execution trace, raw run navigation, and reference-label persistence using isolated synthetic records; those records and the application seeding command have been removed. Native PDF rendering is tested with temporary synthetic inputs. Git excludes databases, invoice inputs, credentials, extraction artifacts, and screenshots. Deployment is validated by building and running from the repository root.

## Not established by these checks

- Actual Azure resource authentication, region/model/add-on compatibility, quota, service latency, or billed cost.
- Accuracy, calibration, throughput, or a 98% outcome on the client's invoice population.
- Custom-model training effectiveness; it requires approved representative client annotations.
- Corporate VM application-control approval or environment-specific firewall/proxy behavior.

The client pilot is therefore an integration and accuracy acceptance step, not a formality. Use `VM-RUNBOOK.md` and `EVALUATION.md`, retain failures, and compare final selections with the original-pass baseline on independent reference labels.
