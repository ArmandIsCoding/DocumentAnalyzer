using InvoiceExplorer.Core;

namespace InvoiceExplorer.Tests;

public class QualityTests
{
    private static FieldCandidate C(string name, string value, double? score = .99, long run = 1, int document = 0) => new(name, value, "string", score, run, "Original", document, "{}", value);
    private static PipelineOptions Options(params string[] fields) => new() { TargetFields = fields, RequiredFields = fields };
    [Fact] public void MissingFieldsStayInCoverageButDoNotFabricateScores()
    { var q = QualityEngine.Decide([C("VendorName", "Acme")], Options("VendorName", "InvoiceId")); Assert.Equal(.5, q.Coverage); Assert.Equal(.99, q.MeanConfidence); Assert.Equal("Missing", q.Fields[1].State); Assert.Equal("Review", q.Status); }
    [Fact] public void OptionalAbsenceIsExplicitAndDoesNotBlockReady()
    { var o = Options("InvoiceId", "PurchaseOrder"); o.RequiredFields = ["InvoiceId"]; var q = QualityEngine.Decide([C("InvoiceId", "A")], o); Assert.Equal("NotFound", q.Fields[1].State); Assert.Equal("Ready", q.Status); Assert.Equal(.5, q.Coverage); }
    [Fact] public void AgreementDoesNotInventHigherConfidence()
    { var q = QualityEngine.Decide([C("InvoiceId", "A", .74), C("InvoiceId", "a", .8, 2)], Options("InvoiceId")); Assert.Equal(.8, q.MeanConfidence); Assert.Equal("LowConfidence", q.Fields[0].State); }
    [Fact] public void HighScoreConflictBlocksAcceptance()
    { var q = QualityEngine.Decide([C("InvoiceId", "A", .99), C("InvoiceId", "B", 1, 2)], Options("InvoiceId")); Assert.Equal("B", q.Fields[0].Value); Assert.Equal("Conflict", q.Fields[0].State); Assert.Equal("Review", q.Status); }
    [Fact] public void DocumentIndexesAreNeverMerged()
    { var q = QualityEngine.Decide([C("InvoiceId", "A"), C("VendorName", "Wrong invoice", document: 1)], Options("InvoiceId", "VendorName")); Assert.Null(q.Fields[1].Value); Assert.Contains(q.Issues, i => i.Code == "MultipleDocuments"); }
    [Fact] public void DerivedCurrencyDoesNotBorrowParentConfidence()
    {
        var c = QualityEngine.ReadCandidates("""{"analyzeResult":{"documents":[{"fields":{"InvoiceTotal":{"type":"currency","valueCurrency":{"amount":1100,"currencyCode":"USD"},"confidence":0.999},"PaymentTerm":{"type":"string","valueString":"Net 30","confidence":null}}}]}}""", 1, "Original", ["Currency", "PaymentTerms"]);
        Assert.Equal("USD", c[0].Value); Assert.True(c[0].Derived); Assert.Null(c[0].Confidence); Assert.Equal("Net 30", c[1].Value); Assert.Null(c[1].Confidence);
        Assert.All(QualityEngine.Decide(c, Options("Currency", "PaymentTerms")).Fields, f => Assert.Equal("Unscored", f.State));
    }
    [Theory]
    [InlineData("InvoiceTotal", "1,100.00")][InlineData("InvoiceDate", "10/12/26")][InlineData("Currency", "$")]
    public void AmbiguousFormatsRequireReview(string field, string value)
    { Assert.Equal("Invalid", QualityEngine.Decide([C(field, value)], Options(field)).Fields[0].State); }
    [Fact] public void ArithmeticAndDateRulesFlagWithoutCorrectingValues()
    {
        var values = new[] { C("SubTotal", "100"), C("TotalTax", "10"), C("InvoiceTotal", "999"), C("InvoiceDate", "2026-10-20"), C("DueDate", "2026-09-01") };
        var q = QualityEngine.Decide(values, Options(values.Select(v => v.Name).ToArray()));
        Assert.Contains(q.Issues, i => i.Code == "TotalMismatch"); Assert.Contains(q.Issues, i => i.Code == "DateOrder"); Assert.Equal("999", q.Fields[2].Value); Assert.Equal("Review", q.Status);
    }
    [Fact] public void FieldSpecificThresholdsAreApplied()
    { var o = Options("InvoiceId"); o.FieldThresholds["InvoiceId"] = .999; Assert.Equal("LowConfidence", QualityEngine.Decide([C("InvoiceId", "A")], o).Fields[0].State); }
    [Fact] public void NormalizationPreservesIdentifierPunctuation()
    { Assert.NotEqual(QualityEngine.Normalize("InvoiceId", "INV-01"), QualityEngine.Normalize("InvoiceId", "INV01")); Assert.Equal("1100", QualityEngine.Normalize("InvoiceTotal", "1100.00")); }
    [Fact] public void ZeroAndUnscoredValuesAreDistinct()
    { var q = QualityEngine.Decide([C("SubTotal", "0", 0), C("TotalTax", "0", null)], Options("SubTotal", "TotalTax")); Assert.Equal(0, q.MeanConfidence); Assert.Equal("LowConfidence", q.Fields[0].State); Assert.Equal("Unscored", q.Fields[1].State); }
}
