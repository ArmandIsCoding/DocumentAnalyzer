# DocumentAnalyzerLLM

Blazor Server app para analizar facturas PDF usando un modelo LLM en Azure y devolver un JSON estructurado.

## Configuracion

Configura la seccion `AzureLlm` en `appsettings.json` o `user-secrets`:

- `Endpoint`: endpoint base de Azure OpenAI (ej: `https://<recurso>.openai.azure.com`)
- `ApiKey`: clave de API
- `Deployment`: nombre del deployment del modelo
- `ApiVersion`: version del API (por defecto `2025-03-01-preview`)
- `MaxFileSizeMb`: tamano maximo de PDF
- `Prompt`: instruccion para que el modelo devuelva solo JSON

JSON esperado del modelo:

```json
{
  "pages": [
    { "pageNumber": 1, "width": 2480, "height": 3508 }
  ],
  "fields": [
    {
      "name": "InvoiceTotal",
      "content": "$50.10",
      "confidence": 0.98,
      "pageNumber": 1,
      "polygon": [100, 100, 400, 100, 400, 160, 100, 160]
    }
  ]
}
```

## Ejecutar

```bash
dotnet run --project /Users/armandomeabe/Desarrollo/DocumentAnalyzer/DocumentAnalyzerLLM/DocumentAnalyzerLLM.csproj
```

