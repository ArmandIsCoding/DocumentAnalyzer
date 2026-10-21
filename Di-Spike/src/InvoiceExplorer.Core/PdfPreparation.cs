using System.Security.Cryptography;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;
using PDFtoImage;
using SkiaSharp;

namespace InvoiceExplorer.Core;

/// <summary>Local inspection and optional reversible rasterization. Original bytes are always preserved.</summary>
public static class PdfPreparation
{
    private static readonly SemaphoreSlim Renderer = new(1, 1);
    public static DocumentProfile Inspect(byte[] bytes)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(bytes);
        var chars = 0; var images = 0;
        foreach (var page in pdf.GetPages()) { chars += page.Text.Length; images += page.NumberOfImages; }
        var kind = chars < 20 ? "Scanned" : images > 0 ? "Mixed" : "Digital";
        var warnings = new List<string>();
        if (pdf.NumberOfPages == 0) throw new InvalidDataException("The PDF contains no pages.");
        if (chars < 20 && images == 0) warnings.Add("No text layer or images were detected; check whether the document is blank or uses vector outlines.");
        return new(pdf.NumberOfPages, chars, images, kind, warnings.ToArray());
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static async Task<string> SnapshotAsync(byte[] bytes, string hash, Settings settings, CancellationToken token)
    {
        var directory = Path.Combine(settings.Storage.ArtifactDirectory, "sources"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, hash + ".pdf");
        if (!File.Exists(path))
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, token);
            try { File.Move(temp, path, false); } catch (IOException) when (File.Exists(path)) { File.Delete(temp); }
        }
        if (Hash(await File.ReadAllBytesAsync(path, token)) != hash) throw new InvalidDataException("An existing source snapshot failed its SHA-256 integrity check.");
        return path;
    }
    public static async Task<byte[]> RasterizeAsync(byte[] bytes, PipelineOptions options, CancellationToken token)
    {
        await Renderer.WaitAsync(token);
        try
        {
            // This distribution is tested and packaged for Windows x64, including native PDFium binaries.
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This portable distribution supports PDF rasterization on Windows.");
            var sizes = Conversion.GetPageSizes(bytes).ToArray();
            if (sizes.Length > options.MaxRasterPages) throw new InvalidOperationException("Document exceeds MaxRasterPages. The original is retained.");
            using var output = new PdfDocumentBuilder();
            for (var index = 0; index < sizes.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var size = sizes[index];
                var width = size.Width * options.RasterDpi / 72d; var height = size.Height * options.RasterDpi / 72d;
                if (width * height > options.MaxRasterPixels || width > 10000 || height > 10000) throw new InvalidOperationException("Raster page would exceed pixel limits. Use the high-resolution OCR add-on on the original PDF instead.");
                using var bitmap = Conversion.ToImage(bytes, page: index, options: new RenderOptions(Dpi: options.RasterDpi));
                if (options.RasterGrayscale)
                {
                    // Preserve luminance and page geometry. No thresholding, cropping, or destructive sharpening.
                    using var copy = bitmap.Copy(); using var canvas = new SKCanvas(bitmap);
                    using var filter = SKColorFilter.CreateColorMatrix([.2126f,.7152f,.0722f,0,0, .2126f,.7152f,.0722f,0,0, .2126f,.7152f,.0722f,0,0, 0,0,0,1,0]);
                    using var paint = new SKPaint { ColorFilter = filter }; canvas.DrawBitmap(copy, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
                }
                using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                var page = output.AddPage(size.Width, size.Height);
                page.AddPng(encoded.ToArray(), new PdfRectangle(0, 0, size.Width, size.Height));
            }
            return output.Build();
        }
        finally { Renderer.Release(); }
    }
}
