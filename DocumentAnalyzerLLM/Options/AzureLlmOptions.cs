namespace DocumentAnalyzerLLM.Options;

public sealed class AzureLlmOptions
{
    public const string SectionName = "AzureLlm";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Deployment { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "2025-03-01-preview";

    public int MaxFileSizeMb { get; set; } = 10;

    public string Prompt { get; set; } =
        "Analiza este PDF de factura y devuelve solo un JSON valido con las propiedades pages y fields. " +
        "Cada elemento de fields debe incluir: name, content, confidence, pageNumber y polygon (8 numeros o mas con coordenadas X,Y). " +
        "No agregues markdown, comentarios ni texto adicional.";
}

