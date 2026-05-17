using Avalonia.Controls;
using MarkMello.Domain;
using MarkMello.Presentation.Views;
using MarkMello.Presentation.Views.Markdown;

namespace MarkMello.Presentation.Tests;

public sealed class MarkdownDiagramBlockViewTests
{
    private const string MinimalSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="red"/></svg>""";

    [Fact]
    public void DocumentWithDiagramSuccessProducesDiagramBlockView()
    {
        var diagram = new MarkdownDiagramBlock(
            MarkdownDiagramKind.Mermaid,
            "flowchart LR\nA --> B")
        {
            RenderResult = new DiagramRenderResult.Success(MinimalSvg),
        };

        var rendered = RenderToVisualTree(diagram);

        Assert.IsType<MarkdownDiagramBlockView>(rendered);
    }

    [Fact]
    public void DocumentWithDiagramFailureStillProducesDiagramBlockView()
    {
        var diagram = new MarkdownDiagramBlock(
            MarkdownDiagramKind.Mermaid,
            "broken")
        {
            RenderResult = new DiagramRenderResult.Failure("invalid syntax", "broken"),
        };

        var rendered = RenderToVisualTree(diagram);

        // The view is responsible for both success and failure visuals — the
        // failure path must not fall through to the document's generic
        // BuildFallback (which would surface the diagram as a plain
        // TextBlock).
        Assert.IsType<MarkdownDiagramBlockView>(rendered);
    }

    [Fact]
    public void DocumentWithUnmaterializedDiagramStillProducesDiagramBlockView()
    {
        // Defensive: if the use case did not materialize a result, the view
        // must NOT silently render the diagram as a code block. The view
        // produces an error visual so the composition gap is visible.
        var diagram = new MarkdownDiagramBlock(
            MarkdownDiagramKind.Mermaid,
            "flowchart LR\nA --> B");

        var rendered = RenderToVisualTree(diagram);

        Assert.IsType<MarkdownDiagramBlockView>(rendered);
    }

    [Fact]
    public void DiagramBlockDoesNotContributeToDocumentText()
    {
        var diagram = new MarkdownDiagramBlock(
            MarkdownDiagramKind.Mermaid,
            "flowchart LR\nA --> B")
        {
            RenderResult = new DiagramRenderResult.Success(MinimalSvg),
        };
        var paragraph = new MarkdownParagraphBlock([new MarkdownTextInline("after")]);

        var document = new RenderedMarkdownDocument([diagram, paragraph]);
        var textMap = MarkdownDocumentTextMap.Create(document);

        // The diagram source must not appear in the document's selectable text
        // (ADR-0005 §8); only the surrounding paragraph contributes content.
        Assert.DoesNotContain("flowchart", textMap.Text, StringComparison.Ordinal);
        Assert.Contains("after", textMap.Text, StringComparison.Ordinal);
    }

    private static Control RenderToVisualTree(MarkdownDiagramBlock diagram)
    {
        var document = new RenderedMarkdownDocument([diagram]);
        var view = new MarkdownDocumentView
        {
            Document = document,
            ReadingPreferences = ReadingPreferences.Default,
        };

        var viewport = Assert.IsType<Border>(view.Content);
        var root = Assert.IsType<StackPanel>(viewport.Child);
        return Assert.Single(root.Children);
    }
}
