# 📑 Invoice AI: Smart Extraction Spike
> **Proof of Concept (PoC)** para la automatización de procesamiento de facturas utilizando **Azure AI Document Intelligence** y **Blazor**.

Este proyecto es un "Spike" técnico diseñado para validar la viabilidad de extraer datos críticos de documentos contables de forma automatizada, reduciendo el error humano y acelerando los tiempos de carga en sistemas internos.

---

## 🚀 Características Principales
*   **Análisis Multiformato:** Soporte para documentos PDF y archivos de imagen (JPG, PNG).
*   **Modelo Pre-entrenado:** Implementación del modelo `prebuilt-invoice` para detección inmediata de metadatos contables.
*   **Confidence Visualizer:** Sistema de semáforo (Verde/Amarillo/Rojo) basado en el *confidence score* devuelto por la API.
*   **Mapeo de Tablas:** Extracción estructurada de ítems de línea (*line items*), cantidades y subtotales.
*   **UI Reactiva:** Interfaz construida en Blazor que permite el feedback inmediato del procesamiento.

---

## 🏗️ Sección Técnica: Arquitectura y Flujo

Para este Spike, se implementó una arquitectura desacoplada donde el frontend gestiona la visualización y el SDK de Azure procesa la carga pesada de visión artificial.

### Flujo de Datos (Data Flow)

1.  **Carga:** El usuario adjunta un archivo mediante `InputFile` en la UI de Blazor.
2.  **Transmisión:** El archivo se envía como un `Stream` al servicio de Azure sin persistencia local (cumplimiento de privacidad).
3.  **Procesamiento:** El motor de OCR y los modelos de Deep Learning de Azure analizan el layout y la semántica del documento.
4.  **Respuesta:** Azure devuelve un objeto `AnalyzeResult` que contiene:
    *   **Content:** El texto bruto.
    *   **Fields:** Diccionario de campos identificados.
    *   **BoundingRegions:** Coordenadas (polígonos) de cada dato en el documento original.
5.  **Renderizado:** Blazor mapea los resultados a una lista visual y utiliza las coordenadas para proyectar los highlights sobre la imagen.

### Diagrama de Secuencia

```text
Usuario -> Blazor App: Sube Factura (PDF/Image)
Blazor App -> Azure Doc Intelligence: AnalyzeDocumentAsync(fileStream)
Azure Doc Intelligence -> Blazor App: AnalyzeResult (JSON + Coordenadas)
Blazor App -> Blazor App: Map fields & Calculate confidence colors
Blazor App -> Usuario: Muestra tabla de resultados + Resaltado visual
```

---

## 🛠️ Tech Stack
*   **Framework:** .NET 8 / Blazor
*   **Servicio de IA:** [Azure AI Document Intelligence](https://azure.microsoft.com/en-us/products/ai-services/ai-document-intelligence)
*   **Librería Cliente:** `Azure.AI.DocumentIntelligence` (SDK oficial)

---

## ⚙️ Configuración del Entorno

Para ejecutar este Spike, es necesario configurar las credenciales del recurso de Azure AI.

### 1. Variables de Configuración
Actualiza tu `appsettings.json` o utiliza **User Secrets** (recomendado):

```json
{
  "DocumentIntelligence": {
    "Endpoint": "https://<tu-recurso>.cognitiveservices.azure.com/",
    "Key": "TU_API_KEY_AQUÍ",
    "ModelId": "prebuilt-invoice"
  }
}
```

### 2. Inyección de Dependencias
El cliente se registra como `Singleton` o `Scoped` para optimizar las conexiones:

```csharp
builder.Services.AddSingleton(sp => 
{
    return new DocumentIntelligenceClient(new Uri(endpoint), new AzureKeyCredential(key));
});
```

---

## 🧪 Objetivos de Validación del Spike
1.  **Precisión Local:** Validar el reconocimiento de facturas de formato local (ej. facturas tipo A/B de AFIP).
2.  **Manejo de Coordenadas:** Comprobar la exactitud de los `BoundingRegions` para el resaltado en pantalla.
3.  **Latencia:** Medir el tiempo de ida y vuelta (RTT) del servicio en la región *Brazil South*.

---

## 📈 Roadmap Post-Spike
- [ ] Implementar **Bounding Boxes** dinámicos sobre un canvas de Blazor.
- [ ] Integración con exportación a JSON/CSV para sistemas de ERP.
- [ ] Implementar **Custom Classification** si se requiere procesar múltiples tipos de documentos (facturas, remitos, contratos).

---

> [!TIP]
> **Nota de Seguridad:** Este repositorio está configurado para ignorar archivos de configuración sensibles. Nunca subas tus llaves de Azure a un repositorio público.

---
*Documentación generada para el proceso de validación técnica - Mayo 2026.*
