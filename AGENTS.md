# AGENTS.md

## Scope & intent
- Single-solution Blazor Server app (`DocumentAnalyzer.sln`) with one web project: `DocumentAnalyzer/DocumentAnalyzer.csproj`.
- Main feature: upload PDF invoice, run Azure Document Intelligence analysis, render OCR + field overlays.
- Existing AI guidance found via glob search: `README.md` (minimal), so this file captures practical repo knowledge.

## Architecture map (read first)
- Composition root: `DocumentAnalyzer/Program.cs` registers interactive Razor Components, Identity, EF Core Sqlite, options binding, and `DocumentIntelligenceService`; it also calls `app.MapAdditionalIdentityEndpoints()`.
- Routing/auth: `DocumentAnalyzer/Components/Routes.razor` uses `<AuthorizeRouteView>` and redirects unauthorized users.
- Feature page: `DocumentAnalyzer/Components/Pages/Home.razor` (`@page "/"`, `@rendermode InteractiveServer`).
- Service boundary: `DocumentAnalyzer/Services/DocumentIntelligenceService.cs` is the only Azure SDK caller and returns UI DTOs from `DocumentAnalyzer/Services/DocumentAnalysisUiResult.cs`.

## End-to-end data flow
- `Home.razor` receives PDF via `<InputFile>` and validates extension/size using `DocumentIntelligenceService.MaxFileSizeBytes`.
- `AnalyzeAsync()` calls `AnalyzePdfAsync(stream)` and maps `RequestFailedException` to user-visible Spanish errors.
- Service sends `BinaryData` to `AnalyzeDocumentAsync(WaitUntil.Completed, ModelId, payload)`.
- Service maps result into `DocumentPagePreview`/`DocumentWordPreview`, `DocumentFieldOverlay`, and pretty `RawJson` for “Ver JSON crudo”.
- UI draws OCR text as positioned spans and field overlays as SVG polygons; field colors are deterministic by sorted field name.

## Local workflows (validated)
- Build, smoke-test, run:
```bash
dotnet build DocumentAnalyzer.sln
dotnet test DocumentAnalyzer.sln
dotnet run --project DocumentAnalyzer/DocumentAnalyzer.csproj
```
- `dotnet test` currently succeeds without executing tests (no test projects in the solution).
- Dev URLs from `DocumentAnalyzer/Properties/launchSettings.json`: `http://localhost:5029`, `https://localhost:7194`.

## Database & identity specifics
- Sqlite connection in `DocumentAnalyzer/appsettings.json` points to `Data/app.db`.
- `DocumentAnalyzer/Data/ApplicationDbContext.cs` is identity-only (`IdentityDbContext<ApplicationUser>`).
- Initial migration: `DocumentAnalyzer/Data/Migrations/00000000000000_CreateIdentitySchema.cs`.
- `Data/app.db` is tracked and copied to output (`CopyToOutputDirectory=PreserveNewest`), so schema/data can show up in git diffs.
- `dotnet ef database update` can fail with "table already exists" when DB schema exists but migration history is not aligned; inspect DB state before applying migrations.

## Configuration & integration rules
- Config model: `DocumentAnalyzer/Options/DocumentIntelligenceOptions.cs` (`Endpoint`, `Key`, `ModelId`, `MaxFileSizeMb`).
- Bound from `DocumentIntelligence` section in `Program.cs`.
- User-secrets are enabled (`UserSecretsId` in `.csproj`); service throws explicit error if `Endpoint`/`Key` are missing.
- External integration package is `Azure.AI.DocumentIntelligence` (see `DocumentAnalyzer/DocumentAnalyzer.csproj`).

## Conventions to preserve
- Keep Azure response mapping in `DocumentIntelligenceService`; components should consume `DocumentAnalysisUiResult` only.
- Keep page-specific UI/CSS colocated (`Home.razor` + `Home.razor.css`) for overlay behavior.
- Preserve Spanish UX text in `Home.razor` unless intentionally localizing the whole app.
- For auth/account changes, update both DI/pipeline in `Program.cs` and endpoint mappings in `IdentityComponentsEndpointRouteBuilderExtensions.cs`.

