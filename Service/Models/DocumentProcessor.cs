using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using NPOI.HWPF;
using NPOI.HWPF.Extractor;
using NPOI.SS.Formula.Functions;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Util;
using Drawing = DocumentFormat.OpenXml.Drawing;
using DrawingSpreadsheet = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using Wp = DocumentFormat.OpenXml.Drawing.Wordprocessing;

public class DocumentProcessor : IDocumentProcessor, IDisposable
{
    private readonly ILogger<DocumentProcessor> _logger;
    private readonly string _tessDataPath;
    private readonly string _ocrLanguage;

    // ✅ NEW: OCR engine setup.
    //
    // DEPLOYMENT REQUIREMENTS — this will NOT work out of the box without
    // these two things present on the server:
    //   1. NuGet package "Tesseract" (the .NET wrapper around the native
    //      Tesseract OCR engine). Recent versions bundle the native
    //      leptonica/tesseract binaries for common runtimes (win-x64,
    //      linux-x64), so a separate native install usually isn't needed —
    //      but this should be verified on the actual deployment server,
    //      since native-binary bundling behavior can vary by package
    //      version and target runtime identifier.
    //   2. Trained language data files (e.g. "eng.traineddata") — these are
    //      NOT included in the NuGet package and must be downloaded
    //      separately from https://github.com/tesseract-ocr/tessdata
    //      (or tessdata_fast for a smaller/faster variant) and placed in
    //      the folder configured below (default "./tessdata"). Missing
    //      this file is the most common cause of OCR silently failing.
    //
    // Singleton lifetime + lazy init: creating a TesseractEngine is
    // expensive (loads the trained model into memory), so one shared
    // instance is created on first use and reused for the app's lifetime,
    // guarded by a lock since this Singleton service can be called
    // concurrently from multiple simultaneous document-processing requests.
    // If initialization fails (missing tessdata, unsupported platform),
    // that failure is logged ONCE and OCR is disabled for the rest of the
    // app's lifetime rather than re-attempting and re-failing on every
    // single image in every subsequent document.
    private readonly object _ocrInitLock = new();
    private TesseractEngine? _ocrEngine;
    private bool _ocrInitAttempted;

    public DocumentProcessor(ILogger<DocumentProcessor> logger, IConfiguration configuration)
    {
        _logger = logger;
        _tessDataPath = configuration["Ocr:TessDataPath"] ?? "./tessdata";
        _ocrLanguage = configuration["Ocr:Language"] ?? "eng";

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private TesseractEngine? GetOcrEngine()
    {
        if (_ocrEngine != null) return _ocrEngine;
        if (_ocrInitAttempted) return null; // already tried and failed — don't retry every call

        lock (_ocrInitLock)
        {
            if (_ocrEngine != null) return _ocrEngine;
            if (_ocrInitAttempted) return null;

            _ocrInitAttempted = true;
            try
            {
                _ocrEngine = new TesseractEngine(_tessDataPath, _ocrLanguage, EngineMode.Default);
                _logger.LogInformation("✅ OCR engine initialized (tessdata: {Path}, language: {Lang})", _tessDataPath, _ocrLanguage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "❌ Failed to initialize OCR engine — image text extraction will be skipped for the " +
                    "remainder of this session. Check that '{Path}/{Lang}.traineddata' exists and that the " +
                    "Tesseract NuGet package's native binaries are compatible with this server's platform.",
                    _tessDataPath, _ocrLanguage);
                _ocrEngine = null;
            }
        }

        return _ocrEngine;
    }

    public void Dispose()
    {
        _ocrEngine?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<string> ExtractTextAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return extension switch
        {
            ".pdf" => await ExtractFromPdfAsync(filePath),
            ".docx" => await ExtractFromDocxAsync(filePath),
            ".doc" => await ExtractFromDocAsync(filePath), // Added .doc support
            ".xlsx" => await ExtractFromXlsxAsync(filePath), // Flow-diagram-aware xlsx support
            _ => await File.ReadAllTextAsync(filePath)
        };
    }

    private async Task<string> ExtractFromPdfAsync(string filePath)
    {
        try
        {
            using var document = PdfDocument.Open(filePath);
            var text = new StringBuilder();

            foreach (var page in document.GetPages())
            {
                text.AppendLine(page.Text);
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from PDF: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process PDF", ex);
        }
    }

    private async Task<string> ExtractFromDocxAsync(string filePath)
    {
        try
        {
            using var document = WordprocessingDocument.Open(filePath, false);
            var mainPart = document.MainDocumentPart;

            if (mainPart?.Document.Body == null)
                return string.Empty;

            var text = new StringBuilder();

            // ✅ FIX: .Elements<Paragraph>() only returns direct children of Body,
            // silently skipping any paragraph nested inside a table (Table -> TableRow ->
            // TableCell -> Paragraph). Policy documents commonly put substantive content
            // (allowance tables, eligibility criteria, checklists) inside tables, so this
            // was extracting only headings/intro text and losing the real content while
            // still indexing the file under its correct name — producing a chunk that
            // looks legitimate but has nothing for the LLM to actually ground on.
            // .Descendants<Paragraph>() walks the whole tree, including table cells.
            foreach (var paragraph in mainPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
            {
                var paragraphText = paragraph.InnerText.Trim();
                if (!string.IsNullOrEmpty(paragraphText))
                {
                    text.AppendLine(paragraphText);
                    text.AppendLine();
                }
            }

            // Optional but recommended: also walk tables explicitly so row/column
            // structure isn't flattened into a meaningless run of cell text.
            foreach (var table in mainPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>())
            {
                foreach (var row in table.Elements<DocumentFormat.OpenXml.Wordprocessing.TableRow>())
                {
                    var cells = row.Elements<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                        .Select(c => c.InnerText.Trim());
                    text.AppendLine(string.Join(" | ", cells));
                }
                text.AppendLine();
            }

            // ✅ NEW: images embedded in the document — screenshots, pasted
            // diagrams, scanned pages. These are just pixels, not structured
            // text, so getting any content out of them requires OCR. See the
            // deployment-requirements comment on GetOcrEngine() above — this
            // silently produces nothing (not an error) if the OCR engine
            // failed to initialize, so a missing tessdata file won't break
            // document processing, it just means images contribute no text.
            var ocrEngine = GetOcrEngine();
            if (ocrEngine != null)
            {
                foreach (var blip in mainPart.Document.Body.Descendants<Drawing.Blip>())
                {
                    var relId = blip.Embed?.Value;
                    if (string.IsNullOrEmpty(relId)) continue;

                    string? altText = null;
                    var container = blip.Ancestors<Wp.Inline>().FirstOrDefault() as OpenXmlElement
                                     ?? blip.Ancestors<Wp.Anchor>().FirstOrDefault();
                    var docPr = container?.Descendants<Wp.DocProperties>().FirstOrDefault();
                    if (docPr != null)
                    {
                        // Word's own "Description" (alt text) field, when someone
                        // bothered to set it — free, no-OCR-needed content.
                        altText = !string.IsNullOrWhiteSpace(docPr.Description?.Value)
                            ? docPr.Description!.Value
                            : docPr.Name?.Value;
                    }

                    try
                    {
                        if (mainPart.GetPartById(relId) is not ImagePart imagePart) continue;

                        using var imgStream = imagePart.GetStream();
                        using var ms = new MemoryStream();
                        imgStream.CopyTo(ms);
                        var imageBytes = ms.ToArray();
                        if (imageBytes.Length == 0) continue;

                        // Tesseract/Leptonica reads standard raster formats
                        // (PNG/JPEG/BMP/TIFF/GIF) directly, but NOT the
                        // vector EMF/WMF formats Office commonly uses for
                        // diagrams pasted from PowerPoint/Visio or drawn
                        // with Word's own shape canvas and saved as a
                        // picture. Those need converting to a raster image
                        // first. NOTE: this conversion path uses
                        // System.Drawing.Common, which is Windows-only —
                        // fine for a Windows-hosted deployment (confirmed
                        // by this project's IIS/Windows Server paths), but
                        // would need a different approach if ever moved to
                        // Linux (standard raster formats would still work
                        // fine via Tesseract directly either way).
                        var contentType = imagePart.ContentType ?? "";
                        byte[] rasterBytes = imageBytes;

                        if (contentType.Contains("emf", StringComparison.OrdinalIgnoreCase) ||
                            contentType.Contains("wmf", StringComparison.OrdinalIgnoreCase))
                        {
                            using var msIn = new MemoryStream(imageBytes);
                            using var vectorImg = System.Drawing.Image.FromStream(msIn);
                            using var bmp = new System.Drawing.Bitmap(vectorImg.Width, vectorImg.Height);
                            using (var g = System.Drawing.Graphics.FromImage(bmp))
                            {
                                // EMF/WMF often has a transparent background —
                                // fill white first, or OCR sees near-blank pixels.
                                g.Clear(System.Drawing.Color.White);
                                g.DrawImage(vectorImg, 0, 0, vectorImg.Width, vectorImg.Height);
                            }
                            using var msOut = new MemoryStream();
                            bmp.Save(msOut, System.Drawing.Imaging.ImageFormat.Png);
                            rasterBytes = msOut.ToArray();
                        }

                        using var pix = Pix.LoadFromMemory(rasterBytes);
                        using var page = ocrEngine.Process(pix);
                        var ocrText = page.GetText()?.Trim();

                        if (!string.IsNullOrWhiteSpace(ocrText) || !string.IsNullOrWhiteSpace(altText))
                        {
                            text.AppendLine("--- Image Content ---");
                            if (!string.IsNullOrWhiteSpace(altText))
                                text.AppendLine($"[Image description: {altText}]");
                            if (!string.IsNullOrWhiteSpace(ocrText))
                                text.AppendLine($"[Image text (OCR)]: {ocrText}");
                            text.AppendLine();
                        }
                    }
                    catch (Exception ex)
                    {
                        // A single unreadable/corrupt/unsupported image should
                        // never fail the whole document — log and move on.
                        _logger.LogWarning(ex, "OCR failed for an embedded image in {FilePath} — skipping that image", filePath);
                    }
                }
            }

            // ✅ NEW: native shape labeling (as opposed to OCR, which is for
            // actual pictures). The existing paragraph walk above already
            // captures shape text boxes' content via Descendants<Paragraph>()
            // (a shape's <w:txbxContent> still contains standard <w:p>
            // elements underneath, regardless of VML or modern DrawingML
            // wrapping) — so shape text isn't MISSING from the output, it's
            // just UNLABELED. A "circled A" off-page connector currently
            // appears as a bare, undifferentiated "A" somewhere in the
            // extracted text, indistinguishable from a stray character or
            // list item, with no indication it's a flowchart connector or
            // which other occurrence it links to. This pass specifically
            // finds and labels connector-like shapes (mirroring the same
            // heuristic used for the Excel case) as a supplementary
            // annotation section, without needing to re-extract or dedupe
            // against the generic paragraph text above.
            var wordConnectors = new List<string>();
            foreach (var picture in mainPart.Document.Body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Picture>())
            {
                try
                {
                    var xml = XDocument.Parse(picture.OuterXml);
                    XNamespace v = "urn:schemas-microsoft-com:vml";

                    foreach (var shapeEl in xml.Descendants(v + "shape").Concat(xml.Descendants(v + "oval")))
                    {
                        var typeAttr = shapeEl.Attribute("type")?.Value ?? "";
                        var isOval = shapeEl.Name.LocalName == "oval" ||
                                     typeAttr.Contains("oval", StringComparison.OrdinalIgnoreCase);
                        if (!isOval) continue;

                        var shapeText = shapeEl.Descendants(v + "textbox").FirstOrDefault()?.Value?.Trim() ?? "";
                        if (LooksLikeConnectorLabel(shapeText))
                            wordConnectors.Add(shapeText.Trim());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not parse a VML shape in {FilePath} for connector detection — skipping", filePath);
                }
            }

            if (wordConnectors.Any())
            {
                var duplicateLabels = wordConnectors
                    .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key);

                if (duplicateLabels.Any())
                {
                    text.AppendLine("=== FLOW CONTINUITY NOTE ===");
                    text.AppendLine($"This document contains circled connector label(s) appearing more than once: {string.Join(", ", duplicateLabels)}. " +
                                     "Each occurrence marks the same continuation point in a flow diagram that spans multiple locations in this document.");
                    text.AppendLine();
                }
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from DOCX: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process DOCX", ex);
        }
    }

    private async Task<string> ExtractFromDocAsync(string filePath)
    {
        try
        {
            // NPOI parsing is primarily synchronous. We wrap it in Task.Run 
            // to prevent blocking the calling thread during heavy file parsing.
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var document = new HWPFDocument(fs);
                var extractor = new WordExtractor(document);

                // WordExtractor automatically handles paragraph boundaries
                return extractor.Text;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from DOC: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process DOC", ex);
        }
    }

    // ============================================================
    // XLSX extraction — flow-diagram aware
    // ============================================================
    //
    // Handles two distinct kinds of content in a worksheet:
    // 1. Ordinary cell data (rows/columns of text/numbers) — extracted
    //    as simple tab-separated rows, same spirit as reading a table.
    // 2. DRAWING SHAPES — flowchart boxes, decision diamonds, and
    //    circled connector labels (A, B, C...) live in the worksheet's
    //    DrawingML layer, NOT in cells, and are invisible to any
    //    approach that only reads cell values. A "Circled A" that means
    //    "flow continues elsewhere" is an ellipse-preset shape with a
    //    single short label as its text.
    //
    // Cross-sheet continuity: the same connector label (e.g. "A") can
    // appear on multiple sheets in the same workbook, meaning "this is
    // the same logical point in the flow." This method collects every
    // circled/ellipse shape's text across ALL sheets first, groups by
    // label, and then annotates every occurrence with which other
    // sheets share that same connector — so a chunk built from any one
    // occurrence still carries the cross-reference, instead of silently
    // treating each sheet's diagram as unrelated to the others.
    private async Task<string> ExtractFromXlsxAsync(string filePath)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var document = SpreadsheetDocument.Open(filePath, false);
                var workbookPart = document.WorkbookPart;
                if (workbookPart?.Workbook.Sheets == null)
                    return string.Empty;

                var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
                var sheets = workbookPart.Workbook.Sheets.Elements<Sheet>().ToList();

                // Pass 1: collect every shape (with position, for reading order)
                // from every sheet, so connector labels can be cross-referenced
                // across the whole workbook before any text is written out.
                var sheetShapes = new Dictionary<string, List<ExtractedShape>>();

                foreach (var sheet in sheets)
                {
                    var sheetName = sheet.Name?.Value ?? "Sheet";
                    if (sheet.Id?.Value == null) continue;

                    var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);
                    sheetShapes[sheetName] = ExtractShapesFromWorksheet(worksheetPart);
                }

                // Build the connector map: label -> every sheet it appears on.
                // Only shapes flagged IsLikelyConnector (ellipse + short text)
                // participate — a long-text ellipse is more likely a Start/End
                // terminal, not an off-page connector, and shouldn't be forced
                // into cross-references it doesn't actually have.
                var connectorMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (sheetName, shapes) in sheetShapes)
                {
                    foreach (var shape in shapes.Where(s => s.IsLikelyConnector))
                    {
                        if (!connectorMap.TryGetValue(shape.Text, out var sheetSet))
                        {
                            sheetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            connectorMap[shape.Text] = sheetSet;
                        }
                        sheetSet.Add(sheetName);
                    }
                }

                // Pass 2: build the final text, sheet by sheet.
                var output = new StringBuilder();

                foreach (var sheet in sheets)
                {
                    var sheetName = sheet.Name?.Value ?? "Sheet";
                    if (sheet.Id?.Value == null) continue;

                    var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id.Value);

                    output.AppendLine($"=== Sheet: {sheetName} ===");
                    output.AppendLine();

                    var cellText = ExtractCellText(worksheetPart, sharedStrings, workbookPart);
                    if (!string.IsNullOrWhiteSpace(cellText))
                    {
                        output.AppendLine("--- Cell Data ---");
                        output.AppendLine(cellText);
                        output.AppendLine();
                    }

                    var shapes = sheetShapes.TryGetValue(sheetName, out var s) ? s : new List<ExtractedShape>();
                    if (shapes.Any())
                    {
                        // Reading order approximation: top-to-bottom, then
                        // left-to-right. This does NOT reconstruct actual arrow
                        // connections/branching — true flowchart topology would
                        // require parsing connector-line endpoints, which is a
                        // materially harder problem. This gives a reasonable
                        // linear reading of the diagram's content instead.
                        output.AppendLine("--- Flow Diagram Shapes (approximate reading order: top-to-bottom, left-to-right) ---");
                        foreach (var shape in shapes.OrderBy(s => s.Row).ThenBy(s => s.Column))
                        {
                            if (shape.IsLikelyConnector && connectorMap.TryGetValue(shape.Text, out var linkedSheets) && linkedSheets.Count > 1)
                            {
                                var otherSheets = linkedSheets.Where(sn => !string.Equals(sn, sheetName, StringComparison.OrdinalIgnoreCase));
                                output.AppendLine($"  [CONNECTOR \"{shape.Text}\"] — flow continues at the matching \"{shape.Text}\" connector on: {string.Join(", ", otherSheets)}");
                            }
                            else
                            {
                                var shapeType = shape.IsLikelyConnector ? "Connector" : DescribeShapeKind(shape.GeometryPreset);
                                output.AppendLine($"  [{shapeType}] {shape.Text}");
                            }
                        }
                        output.AppendLine();
                    }
                }

                // Summary map at the end — gives the chunker/retrieval a single
                // place that states every cross-sheet link explicitly, so the
                // relationship survives even if a chunk boundary later splits
                // the two occurrences apart.
                if (connectorMap.Any(kv => kv.Value.Count > 1))
                {
                    output.AppendLine("=== FLOW CONTINUITY MAP (cross-sheet connectors) ===");
                    foreach (var (label, sheetsWithLabel) in connectorMap.Where(kv => kv.Value.Count > 1))
                    {
                        output.AppendLine($"  Connector \"{label}\" links: {string.Join(" <-> ", sheetsWithLabel)}");
                    }
                    output.AppendLine();
                }

                return output.ToString();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract text from XLSX: {FilePath}", filePath);
            throw new DocumentProcessingException("Failed to process XLSX", ex);
        }
    }

    private class ExtractedShape
    {
        public string Text { get; set; } = "";
        public string? GeometryPreset { get; set; }
        public bool IsLikelyConnector { get; set; }
        public long Row { get; set; }
        public long Column { get; set; }
    }

    // A connector label is short (flowchart off-page connectors are
    // conventionally a single letter or a letter+digit, e.g. "A", "B1")
    // and sits inside an ellipse/oval shape. A long sentence inside an
    // ellipse is much more likely a Start/End terminal shape, not a
    // cross-reference — deliberately conservative so real content doesn't
    // get mislabeled as a bare connector marker and stripped of its text.
    // Only used for GENERIC ellipse/oval shapes, where the shape type
    // alone is ambiguous (see IsExplicitConnectorShape below for the
    // unambiguous case).
    private static bool LooksLikeConnectorLabel(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.Trim().Length <= 4 &&
        Regex.IsMatch(text.Trim(), @"^[A-Za-z][A-Za-z0-9]?$");

    // ✅ NEW: Excel/PowerPoint's actual "Flowchart" shape gallery has
    // dedicated, purpose-built presets for exactly this scenario —
    // "flowChartConnector" (a small circle, literally labeled "Connector"
    // in the shape picker) and "flowChartOffpageConnector" (a pentagon/
    // "home plate" shape, literally labeled "Off-page Connector"). If a
    // diagram was built using these gallery shapes (very common for real
    // flowcharts, as opposed to a generic freehand oval), the shape TYPE
    // itself is a stronger, unambiguous signal than the text-length
    // heuristic — a connector shape is a connector regardless of whether
    // its label happens to be "A" or something longer like "Continue to
    // Approval Process". Previously only a bare "ellipse" preset was
    // checked, which completely missed these purpose-built shapes.
    private static bool IsExplicitConnectorShape(string? geometryPreset) =>
        geometryPreset is "flowChartConnector" or "flowChartOffpageConnector";

    // Full standard flowchart shape gallery, not just the four most common
    // shapes — a real flowchart built with Office's Flowchart shape set can
    // use any of these, and previously anything outside ellipse/diamond/
    // rect/roundRect fell into a generic, unhelpful "Shape" label.
    private static string DescribeShapeKind(string? geometryPreset) => geometryPreset switch
    {
        "flowChartTerminator" or "ellipse" => "Terminal (Start/End)",
        "flowChartDecision" or "diamond" => "Decision",
        "flowChartProcess" or "rect" or "roundRect" => "Process",
        "flowChartAlternateProcess" => "Alternate Process",
        "flowChartPredefinedProcess" => "Predefined Process (Subroutine)",
        "flowChartInputOutput" or "parallelogram" => "Input/Output",
        "flowChartPreparation" or "hexagon" => "Preparation",
        "flowChartManualInput" => "Manual Input",
        "flowChartManualOperation" => "Manual Operation",
        "flowChartDocument" => "Document",
        "flowChartMultidocument" => "Multiple Documents",
        "flowChartInternalStorage" => "Internal Storage",
        "flowChartOnlineStorage" => "Stored Data",
        "flowChartMagneticDisk" => "Database",
        "flowChartMagneticDrum" => "Direct Access Storage",
        "flowChartMagneticTape" => "Sequential Storage (Tape)",
        "flowChartPunchedCard" => "Punched Card",
        "flowChartPunchedTape" => "Punched Tape",
        "flowChartDisplay" => "Display",
        "flowChartDelay" => "Delay",
        "flowChartSummingJunction" => "Summing Junction",
        "flowChartOr" => "Or",
        "flowChartCollate" => "Collate",
        "flowChartSort" => "Sort",
        "flowChartExtract" => "Extract",
        "flowChartMerge" => "Merge",
        "flowChartConnector" => "Connector",
        "flowChartOffpageConnector" => "Off-page Connector",
        "connectorLine" => "Labeled Arrow",
        _ => "Shape"
    };

    private List<ExtractedShape> ExtractShapesFromWorksheet(WorksheetPart worksheetPart)
    {
        var results = new List<ExtractedShape>();

        // --- Modern DrawingML shapes (Excel 2007+, the common case) ---
        var drawingsPart = worksheetPart.DrawingsPart;
        if (drawingsPart?.WorksheetDrawing != null)
        {
            foreach (var shape in drawingsPart.WorksheetDrawing.Descendants<DrawingSpreadsheet.Shape>())
            {
                var text = string.Join(" ", shape.TextBody?
                    .Descendants<Drawing.Text>()
                    .Select(t => t.Text) ?? Enumerable.Empty<string>()).Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;

                var preset = shape.ShapeProperties?
                    .Descendants<Drawing.PresetGeometry>()
                    .FirstOrDefault()?.Preset?.Value.ToString();

                // Position from the anchor, used only for approximate reading
                // order (top-to-bottom, left-to-right) — not exact pixel
                // coordinates, just the underlying grid row/column the shape
                // is anchored from.
                long row = 0, col = 0;
                var anchor = shape.Parent;
                if (anchor is DrawingSpreadsheet.TwoCellAnchor twoCell)
                {
                    row = long.TryParse(twoCell.FromMarker?.RowId?.Text, out var r) ? r : 0;
                    col = long.TryParse(twoCell.FromMarker?.ColumnId?.Text, out var c) ? c : 0;
                }
                else if (anchor is DrawingSpreadsheet.OneCellAnchor oneCell)
                {
                    row = long.TryParse(oneCell.FromMarker?.RowId?.Text, out var r) ? r : 0;
                    col = long.TryParse(oneCell.FromMarker?.ColumnId?.Text, out var c) ? c : 0;
                }

                var isEllipse = string.Equals(preset, "ellipse", StringComparison.OrdinalIgnoreCase);

                // ✅ CHANGED: check the explicit, purpose-built connector
                // presets first — their shape type alone is unambiguous,
                // regardless of text length. Only fall back to the
                // ellipse+short-text heuristic for a generic oval, which is
                // ambiguous on its own (could be a terminal, a callout, or
                // genuinely a hand-drawn connector).
                var isLikelyConnector = IsExplicitConnectorShape(preset) ||
                                        (isEllipse && LooksLikeConnectorLabel(text));

                results.Add(new ExtractedShape
                {
                    Text = text,
                    GeometryPreset = preset,
                    IsLikelyConnector = isLikelyConnector,
                    Row = row,
                    Column = col
                });
            }

            // ✅ NEW: connection shapes (cxnSp) are the actual connector
            // LINES/arrows between boxes — distinct from Shape (sp) elements.
            // These rarely carry text, but when a diagram labels a decision
            // branch directly on the line itself (e.g. "Yes"/"No" on the arrow
            // leaving a Decision diamond) rather than in a separate nearby text
            // box, that label lives here and was previously never extracted at
            // all — silently dropping a piece of the flow's actual logic.
            foreach (var connector in drawingsPart.WorksheetDrawing.Descendants<DrawingSpreadsheet.ConnectionShape>())
            {
                var text = string.Join(" ", connector.TextBody?
                    .Descendants<Drawing.Text>()
                    .Select(t => t.Text) ?? Enumerable.Empty<string>()).Trim();

                if (string.IsNullOrWhiteSpace(text)) continue;

                long row = 0, col = 0;
                var anchor = connector.Parent;
                if (anchor is DrawingSpreadsheet.TwoCellAnchor twoCell)
                {
                    row = long.TryParse(twoCell.FromMarker?.RowId?.Text, out var r) ? r : 0;
                    col = long.TryParse(twoCell.FromMarker?.ColumnId?.Text, out var c) ? c : 0;
                }

                results.Add(new ExtractedShape
                {
                    Text = text,
                    GeometryPreset = "connectorLine",
                    IsLikelyConnector = false, // a labeled arrow, not an off-page connector
                    Row = row,
                    Column = col
                });
            }
        }

        // --- Legacy VML shapes fallback ---
        // Older workbooks (or ones converted from legacy .xls) sometimes
        // store drawings via the legacy VML format instead of modern
        // DrawingML. The OpenXml SDK doesn't strongly-type VML the same
        // way, so this reads the raw part XML directly. Best-effort: if a
        // workbook's flow diagram doesn't show up via the DrawingML path
        // above, this is the fallback that should catch it. Position
        // ordering isn't attempted here (VML positioning uses a different,
        // less structured coordinate scheme) — these shapes are appended
        // after any DrawingML shapes on the same sheet.
        foreach (var vmlPart in worksheetPart.VmlDrawingParts)
        {
            try
            {
                using var stream = vmlPart.GetStream();
                var xml = XDocument.Load(stream);
                XNamespace v = "urn:schemas-microsoft-com:vml";
                XNamespace o = "urn:schemas-microsoft-com:office:office";

                foreach (var shapeEl in xml.Descendants(v + "shape").Concat(xml.Descendants(v + "oval")))
                {
                    var typeAttr = shapeEl.Attribute("type")?.Value ?? "";
                    var isOval = shapeEl.Name.LocalName == "oval" ||
                                 typeAttr.Contains("oval", StringComparison.OrdinalIgnoreCase);

                    var text = shapeEl.Descendants(v + "textbox").FirstOrDefault()?.Value?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    results.Add(new ExtractedShape
                    {
                        Text = text,
                        GeometryPreset = isOval ? "ellipse" : "rect",
                        IsLikelyConnector = isOval && LooksLikeConnectorLabel(text),
                        Row = long.MaxValue, // sort after all DrawingML shapes
                        Column = 0
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse legacy VML drawing part — skipping");
            }
        }

        return results;
    }

    // Excel's own built-in numeric format IDs that represent dates/times.
    // (14-22 = date/time formats; 45-47 = additional time formats.) A cell
    // formatted as a date is stored internally as a plain serial number —
    // without checking this, "2026-01-15" would extract as the meaningless
    // raw number "46037".
    private static readonly HashSet<uint> DateFormatIds = new() { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };
    private static readonly HashSet<uint> PercentFormatIds = new() { 9, 10 };

    // Resolves a cell's effective number format ID via its style index —
    // needed to tell a plain number apart from a date/percentage that's
    // stored the same way internally (both are just a double).
    private uint? GetNumberFormatId(Cell cell, WorkbookPart workbookPart)
    {
        if (cell.StyleIndex?.Value == null) return null;

        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var cellFormat = stylesheet?.CellFormats?.ElementAtOrDefault((int)cell.StyleIndex.Value) as CellFormat;
        return cellFormat?.NumberFormatId?.Value;
    }

    // Checks a WORKBOOK-DEFINED custom format string (not just the
    // built-in IDs above) for date-like patterns — covers custom formats
    // like "dd-mmm-yyyy" that don't use one of Excel's built-in format IDs
    // but are still clearly dates by their format code.
    private bool IsCustomDateFormat(uint formatId, WorkbookPart workbookPart)
    {
        var customFormats = workbookPart.WorkbookStylesPart?.Stylesheet?.NumberingFormats;
        var formatCode = customFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(f => f.NumberFormatId?.Value == formatId)?.FormatCode?.Value;

        return formatCode != null &&
               (formatCode.Contains("yy", StringComparison.OrdinalIgnoreCase) ||
                formatCode.Contains("mmm", StringComparison.OrdinalIgnoreCase) ||
                formatCode.Contains("dd", StringComparison.OrdinalIgnoreCase));
    }

    private string FormatCellValue(Cell cell, string rawValue, WorkbookPart workbookPart)
    {
        var formatId = GetNumberFormatId(cell, workbookPart);
        if (formatId == null || !double.TryParse(rawValue, out var numericValue))
            return rawValue;

        if (DateFormatIds.Contains(formatId.Value) || IsCustomDateFormat(formatId.Value, workbookPart))
        {
            try
            {
                var date = DateTime.FromOADate(numericValue);
                // Only a handful of built-in IDs represent time-of-day (or
                // date+time); treat those distinctly so a pure date doesn't
                // get a meaningless "00:00:00" appended.
                return (formatId.Value is 18 or 19 or 20 or 21 or 45 or 46 or 47)
                    ? date.ToString("yyyy-MM-dd HH:mm")
                    : date.ToString("yyyy-MM-dd");
            }
            catch
            {
                return rawValue; // out-of-range serial value — fall back to raw
            }
        }

        if (PercentFormatIds.Contains(formatId.Value))
        {
            return $"{numericValue * 100:0.##}%";
        }

        return rawValue;
    }

    private string ExtractCellText(WorksheetPart worksheetPart, SharedStringTable? sharedStrings, WorkbookPart workbookPart)
    {
        var sb = new StringBuilder();

        // Cell comments: keyed by cell reference (e.g. "B4") so they can be
        // appended inline next to the value they annotate, rather than
        // dumped separately at the end where the connection to a specific
        // cell would be lost.
        var comments = new Dictionary<string, string>();
        var commentsPart = worksheetPart.WorksheetCommentsPart;
        if (commentsPart?.CommentList != null)
        {
            foreach (var comment in commentsPart.CommentList.Elements<Comment>())
            {
                var cellRef = comment.Reference?.Value;
                var text = comment.CommentText?.InnerText?.Trim();
                if (!string.IsNullOrEmpty(cellRef) && !string.IsNullOrEmpty(text))
                    comments[cellRef] = text;
            }
        }

        // Cell hyperlinks: the underlying URL is meaningfully different
        // information from the cell's displayed text (e.g. a cell showing
        // "Click here" pointing to a specific policy document/intranet
        // page) — resolved via the worksheet's external relationships.
        var hyperlinks = new Dictionary<string, string>();
        foreach (var hyperlink in worksheetPart.Worksheet.Descendants<Hyperlink>())
        {
            var cellRef = hyperlink.Reference?.Value;
            if (string.IsNullOrEmpty(cellRef)) continue;

            if (hyperlink.Id?.Value != null)
            {
                try
                {
                    var rel = worksheetPart.HyperlinkRelationships.FirstOrDefault(r => r.Id == hyperlink.Id.Value);
                    if (rel != null) hyperlinks[cellRef] = rel.Uri.ToString();
                }
                catch { /* malformed relationship — skip, not fatal */ }
            }
            else if (hyperlink.Location?.Value != null)
            {
                hyperlinks[cellRef] = hyperlink.Location.Value; // internal link (e.g. another sheet/named range)
            }
        }

        List<string>? headers = null;
        bool isFirstDataRow = true;

        foreach (var row in worksheetPart.Worksheet.Descendants<Row>())
        {
            var cellValues = new List<string>();

            foreach (var cell in row.Elements<Cell>())
            {
                var value = cell.CellValue?.Text ?? "";

                if (cell.DataType?.Value == CellValues.SharedString && sharedStrings != null &&
                    int.TryParse(value, out var ssIndex))
                {
                    value = sharedStrings.ElementAtOrDefault(ssIndex)?.InnerText ?? value;
                }
                else if (!string.IsNullOrWhiteSpace(value))
                {
                    // Only non-string cells can be a formatted date/percentage —
                    // shared-string text values are already the literal display text.
                    value = FormatCellValue(cell, value, workbookPart);
                }

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                value = value.Trim();

                var cellRef = cell.CellReference?.Value;
                if (cellRef != null)
                {
                    if (hyperlinks.TryGetValue(cellRef, out var url))
                        value += $" (link: {url})";
                    if (comments.TryGetValue(cellRef, out var commentText))
                        value += $" [comment: {commentText}]";
                }

                cellValues.Add(value);
            }

            if (!cellValues.Any()) continue;

            // ✅ NEW: header-aware formatting. The first non-empty row of a
            // sheet is treated as column headers; every subsequent row is
            // rendered as "Header: Value" pairs instead of a bare
            // pipe-joined list. This matters a great deal for retrieval —
            // "50000 | John | Manager" carries almost no retrievable meaning
            // on its own, while "Employee: John | Salary: 50000 | Grade:
            // Manager" is directly matchable against a question about
            // salary or grade. If a row has more cells than the header row
            // (ragged data), the extra values fall back to being listed
            // plainly rather than dropped.
            if (isFirstDataRow)
            {
                headers = cellValues;
                sb.AppendLine(string.Join(" | ", cellValues));
                isFirstDataRow = false;
            }
            else if (headers != null && headers.Count > 1)
            {
                var paired = cellValues
                    .Select((v, i) => i < headers.Count ? $"{headers[i]}: {v}" : v);
                sb.AppendLine(string.Join(" | ", paired));
            }
            else
            {
                sb.AppendLine(string.Join(" | ", cellValues));
            }
        }

        return sb.ToString();
    }
}