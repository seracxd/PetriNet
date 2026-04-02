using PetriEditor.Shared.Contracts;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace PetriEditor.Server.Services;

/// <summary>
/// Generates a PDF report for a Petri net using QuestPDF (server-side only —
/// QuestPDF is not compatible with WebAssembly).
///
/// The report contains:
///   1. Title page with net name
///   2. Net structure — places, transitions, arcs
///   3. Analysis results — scalar properties and per-property test results
///   4. Invariants summary
/// </summary>
public sealed class PdfExportService
{
    public byte[] Generate(ExportRequestDto request)
    {
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(2, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(ComposeHeader(request.Options.DocumentTitle));
                page.Content().Element(ComposeContent(request));
                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        });

        return doc.GeneratePdf();
    }

    // ── Header ────────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeHeader(string title) =>
        c => c.BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingBottom(5)
              .Row(row =>
              {
                  row.RelativeItem().Column(col =>
                  {
                      col.Item().Text(title).Bold().FontSize(18);
                      col.Item().Text("Petri Net Analysis Report").FontColor(Colors.Grey.Darken2);
                  });
              });

    // ── Content ───────────────────────────────────────────────────────────

    private static Action<IContainer> ComposeContent(ExportRequestDto request) =>
        c => c.PaddingTop(10).Column(col =>
        {
            col.Spacing(15);

            // ── Net structure ─────────────────────────────────────────────
            col.Item().Element(ComposeNetStructure(request.Net));

            // ── Analysis results (if included) ────────────────────────────
            if (request.Options.IncludeAnalysis && request.Options.AnalysisResult is { } analysis)
            {
                col.Item().Element(ComposeAnalysisSummary(analysis));
                col.Item().Element(ComposePropertyResults(analysis));
                col.Item().Element(ComposeInvariants(analysis, request.Net));
            }
        });

    // ── Net structure section ─────────────────────────────────────────────

    private static Action<IContainer> ComposeNetStructure(PetriNetDto net) =>
        c => c.Column(col =>
        {
            col.Item().Text("Net Structure").Bold().FontSize(13);
            col.Spacing(8);

            var nameById = net.Places.ToDictionary(p => p.Id, p => p.Name)
                .Concat(net.Transitions.ToDictionary(t => t.Id, t => t.Name))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            // Places
            col.Item().Text($"Places ({net.Places.Count})").Bold();
            if (net.Places.Count == 0)
            {
                col.Item().Text("  None").FontColor(Colors.Grey.Medium);
            }
            else
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2);
                        c.RelativeColumn(3);
                        c.ConstantColumn(60);
                    });

                    table.Header(h =>
                    {
                        h.Cell().Text("ID").Bold();
                        h.Cell().Text("Name").Bold();
                        h.Cell().AlignRight().Text("Tokens").Bold();
                    });

                    foreach (var p in net.Places)
                    {
                        table.Cell().Text(p.Id);
                        table.Cell().Text(p.Name);
                        table.Cell().AlignRight().Text(p.Tokens.ToString());
                    }
                });
            }

            // Transitions
            col.Item().Text($"Transitions ({net.Transitions.Count})").Bold();
            if (net.Transitions.Count == 0)
            {
                col.Item().Text("  None").FontColor(Colors.Grey.Medium);
            }
            else
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2);
                        c.RelativeColumn(3);
                        c.ConstantColumn(60);
                    });

                    table.Header(h =>
                    {
                        h.Cell().Text("ID").Bold();
                        h.Cell().Text("Name").Bold();
                        h.Cell().AlignRight().Text("Priority").Bold();
                    });

                    foreach (var t in net.Transitions)
                    {
                        table.Cell().Text(t.Id);
                        table.Cell().Text(t.Name);
                        table.Cell().AlignRight().Text(t.Priority.ToString());
                    }
                });
            }

            // Arcs
            col.Item().Text($"Arcs ({net.Arcs.Count})").Bold();
            if (net.Arcs.Count == 0)
            {
                col.Item().Text("  None").FontColor(Colors.Grey.Medium);
            }
            else
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(3);
                        c.RelativeColumn(3);
                        c.RelativeColumn(2);
                        c.ConstantColumn(50);
                    });

                    table.Header(h =>
                    {
                        h.Cell().Text("Source").Bold();
                        h.Cell().Text("Target").Bold();
                        h.Cell().Text("Type").Bold();
                        h.Cell().AlignRight().Text("Weight").Bold();
                    });

                    foreach (var a in net.Arcs)
                    {
                        table.Cell().Text(nameById.GetValueOrDefault(a.SourceId, a.SourceId));
                        table.Cell().Text(nameById.GetValueOrDefault(a.TargetId, a.TargetId));
                        table.Cell().Text(a.ArcType.ToString());
                        table.Cell().AlignRight().Text(a.Weight.ToString());
                    }
                });
            }
        });

    // ── Analysis summary section ──────────────────────────────────────────

    private static Action<IContainer> ComposeAnalysisSummary(AnalysisResultDto r) =>
        c => c.Column(col =>
        {
            col.Item().Text("Analysis Summary").Bold().FontSize(13);
            col.Spacing(4);

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(cols =>
                {
                    cols.RelativeColumn(3);
                    cols.RelativeColumn(2);
                });

                void BoolRow(string label, bool value)
                {
                    table.Cell().Text(label);
                    table.Cell().Text(value ? "Yes" : "No")
                         .FontColor(value ? Colors.Green.Darken2 : Colors.Red.Darken2);
                }

                BoolRow("Bounded",      r.IsBounded);
                BoolRow("Safe",         r.IsSafe);
                BoolRow("Live",         r.IsLive);
                BoolRow("Deadlock-free", r.IsDeadlockFree);
                BoolRow("Reversible",   r.IsReversible);

                table.Cell().Text("Structural class");
                table.Cell().Text(r.ClassificationSummary);

                table.Cell().Text("State count");
                table.Cell().Text(r.StateCount.ToString());
            });
        });

    // ── Per-property results section ──────────────────────────────────────

    private static Action<IContainer> ComposePropertyResults(AnalysisResultDto r) =>
        c => c.Column(col =>
        {
            col.Item().Text("Property Test Results").Bold().FontSize(13);
            col.Spacing(6);

            foreach (var p in r.PropertyResults)
            {
                col.Item().Column(inner =>
                {
                    inner.Item().Row(row =>
                    {
                        row.RelativeItem().Text(p.Property).Bold();
                        row.ConstantItem(100).AlignRight().Text(p.StatusLabel)
                           .FontColor(p.StatusColor);
                    });

                    foreach (var reason in p.Reasons)
                        inner.Item().PaddingLeft(10).Text($"• {reason}").FontSize(9);

                    foreach (var error in p.Errors)
                        inner.Item().PaddingLeft(10).Text($"⚠ {error}")
                             .FontSize(9).FontColor(Colors.Orange.Darken3);
                });
            }
        });

    // ── Invariants section ────────────────────────────────────────────────

    private static Action<IContainer> ComposeInvariants(AnalysisResultDto r, PetriNetDto net) =>
        c => c.Column(col =>
        {
            col.Item().Text("Invariants").Bold().FontSize(13);
            col.Spacing(6);

            var nameById = net.Places.ToDictionary(p => p.Id, p => p.Name)
                .Concat(net.Transitions.ToDictionary(t => t.Id, t => t.Name))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

            string Resolve(string id) => nameById.GetValueOrDefault(id, id);

            col.Item().Text($"P-invariants: {r.PInvariants.Count}").Bold();
            foreach (var inv in r.PInvariants)
            {
                var text = string.Join(" + ", inv.Structure.Select(kv =>
                    kv.Value == 1 ? Resolve(kv.Key) : $"{kv.Value}·{Resolve(kv.Key)}"));
                col.Item().PaddingLeft(10).Text($"• {text}").FontSize(9);
            }

            col.Item().Text($"T-invariants: {r.TInvariants.Count}").Bold();
            foreach (var inv in r.TInvariants)
            {
                var text = string.Join(" + ", inv.Structure.Select(kv =>
                    kv.Value == 1 ? Resolve(kv.Key) : $"{kv.Value}·{Resolve(kv.Key)}"));
                col.Item().PaddingLeft(10).Text($"• {text}").FontSize(9);
            }
        });
}
