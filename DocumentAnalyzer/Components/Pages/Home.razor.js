import * as pdfjsLib from "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.5.136/pdf.min.mjs";

pdfjsLib.GlobalWorkerOptions.workerSrc = "https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.5.136/pdf.worker.min.mjs";

let cachedFileKey = null;
let cachedDocument = null;

async function getElementWithRetry(elementId, retries = 8) {
    for (let attempt = 0; attempt <= retries; attempt += 1) {
        const element = document.getElementById(elementId);

        if (element) {
            return element;
        }

        await new Promise((resolve) => window.requestAnimationFrame(resolve));
    }

    return null;
}

function buildFileKey(file) {
    return `${file.name}:${file.size}:${file.lastModified}`;
}

async function loadPdfDocument(fileInputId) {
    const input = await getElementWithRetry(fileInputId);

    if (!input || !input.files || input.files.length === 0) {
        throw new Error("No hay archivo PDF seleccionado.");
    }

    const file = input.files[0];
    const currentKey = buildFileKey(file);

    if (cachedDocument && cachedFileKey === currentKey) {
        return cachedDocument;
    }

    const fileBuffer = await file.arrayBuffer();
    const loadingTask = pdfjsLib.getDocument({ data: fileBuffer });
    const pdfDocument = await loadingTask.promise;

    cachedDocument = pdfDocument;
    cachedFileKey = currentKey;

    return pdfDocument;
}

export async function renderPageFromInput(fileInputId, canvasId, pageNumber) {
    const canvas = await getElementWithRetry(canvasId);

    if (!canvas) {
        throw new Error("No se encontro el canvas para renderizar el PDF.");
    }

    const pdfDocument = await loadPdfDocument(fileInputId);
    const safePageNumber = Math.max(1, Math.min(pageNumber, pdfDocument.numPages));
    const page = await pdfDocument.getPage(safePageNumber);

    const deviceScale = window.devicePixelRatio || 1;
    const viewport = page.getViewport({ scale: deviceScale * 1.35 });
    const context = canvas.getContext("2d", { alpha: false });

    if (!context) {
        throw new Error("No se pudo obtener el contexto 2D del canvas.");
    }

    canvas.width = Math.floor(viewport.width);
    canvas.height = Math.floor(viewport.height);

    await page.render({ canvasContext: context, viewport }).promise;
}

