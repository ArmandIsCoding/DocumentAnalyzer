using System.Text.Json;

namespace InvoiceExplorer.Core;

public static class DemoData
{
    public const string Hash = "DEMO-FICTIONAL-INVOICE-001";
    public static string Json(int execution)
    {
        var better = execution > 1;
        object Field(string value, double? confidence) => new { type = "string", valueString = value, content = value, confidence };
        return JsonSerializer.Serialize(new
        {
            status = "succeeded",
            analyzeResult = new
            {
                content = "DEMO · FICTIONAL INVOICE\nSouth Studio · DEMO-001\nCustomer: Riverside Workshop\nBrand identity design · 2 × 150.00\nTotal: ARS 363.00\nThese values and confidence scores are simulated.",
                documents = new[] { new {
                    docType = "invoice", confidence = 0.99,
                    fields = new Dictionary<string, object> {
                        ["InvoiceId"] = Field("DEMO-001", .99),
                        ["VendorName"] = Field(better ? "South Studio" : "South Studlo", better ? .98 : .72),
                        ["CustomerName"] = Field("Riverside Workshop", .96),
                        ["InvoiceDate"] = new { type = "date", valueDate = "2026-10-20", confidence = .97 },
                        ["InvoiceTotal"] = new { type = "currency", valueCurrency = new { amount = 363, currencyCode = "ARS" }, confidence = better ? .99 : .88 },
                        ["PaymentTerm"] = Field("30 days", null),
                        ["Items"] = new { type = "array", valueArray = new[] { new { type = "object",
                            valueObject = new Dictionary<string, object> {
                                ["Description"] = Field("Brand identity design", better ? .97 : .81),
                                ["Quantity"] = new { type = "number", valueNumber = 2, confidence = .99 },
                                ["UnitPrice"] = new { type = "currency", valueCurrency = new { amount = 150, currencyCode = "ARS" }, confidence = .98 }
                            } } } }
                    }
                } },
                pages = new[] { new { pageNumber = 1, words = new[] { new { content = "INVOICE", confidence = .995 } } } }
            }
        });
    }

    public static bool Seed(InvoiceRepository repository, Settings settings)
    {
        if (repository.ListInvoices().Any(i => i.Hash == Hash)) return false;
        for (var i = 1; i <= 2; i++)
        {
            var json = Json(i);
            var id = repository.StartRun(Hash, "DEMO-001 · fictional invoice", settings, true);
            repository.Complete(id, "DEMO-001", json, ExtractionParser.Parse(json));
        }
        return true;
    }
}
