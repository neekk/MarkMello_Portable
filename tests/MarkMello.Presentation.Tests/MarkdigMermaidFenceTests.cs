using MarkMello.Domain;
using MarkMello.Infrastructure.Markdown;

namespace MarkMello.Presentation.Tests;

public sealed class MarkdigMermaidFenceTests
{
    [Fact]
    public void MermaidFenceBecomesDiagramBlockWithMermaidKind()
    {
        const string markdown = """
            ```mermaid
            flowchart LR
                A --> B
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var diagram = Assert.IsType<MarkdownDiagramBlock>(Assert.Single(document.Blocks));
        Assert.Equal(MarkdownDiagramKind.Mermaid, diagram.Kind);
        Assert.Equal("flowchart LR\n    A --> B", diagram.Source);
        Assert.Null(diagram.Info);
        Assert.Null(diagram.Title);
        Assert.Null(diagram.RenderResult);
    }

    [Fact]
    public void MermaidFenceTokenIsCaseInsensitive()
    {
        const string markdown = """
            ```MERMAID
            flowchart LR
                A --> B
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var diagram = Assert.IsType<MarkdownDiagramBlock>(Assert.Single(document.Blocks));
        Assert.Equal(MarkdownDiagramKind.Mermaid, diagram.Kind);
    }

    [Fact]
    public void MermaidFenceWithExtraInfoTokensKeepsRemainderInInfo()
    {
        const string markdown = """
            ```mermaid title=My Diagram
            flowchart LR
                A --> B
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var diagram = Assert.IsType<MarkdownDiagramBlock>(Assert.Single(document.Blocks));
        Assert.Equal(MarkdownDiagramKind.Mermaid, diagram.Kind);
        Assert.Equal("title=My Diagram", diagram.Info);
        Assert.Null(diagram.Title);
    }

    [Fact]
    public void UnknownFenceLanguageRemainsCodeBlock()
    {
        const string markdown = """
            ```csharp
            var x = 1;
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(document.Blocks));
        Assert.Equal("csharp", code.Info);
        Assert.Equal("var x = 1;", code.Code);
    }

    [Fact]
    public void PlantUmlFenceRemainsCodeBlockUntilRendererIsAdded()
    {
        const string markdown = """
            ```plantuml
            @startuml
            A -> B
            @enduml
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(document.Blocks));
        Assert.Equal("plantuml", code.Info);
        Assert.Equal("@startuml\nA -> B\n@enduml", code.Code);
    }

    [Fact]
    public void IndentedCodeBlockNeverBecomesDiagramBlock()
    {
        // Four-space indent is a CommonMark indented code block; "mermaid" is
        // part of the code itself, not a fence info string.
        const string markdown = "    mermaid\n    flowchart LR\n    A --> B";

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(document.Blocks));
        Assert.Null(code.Info);
        Assert.Contains("mermaid", code.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void FenceWithoutInfoStringStaysCodeBlock()
    {
        const string markdown = """
            ```
            plain text
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var code = Assert.IsType<MarkdownCodeBlock>(Assert.Single(document.Blocks));
        Assert.Null(code.Info);
        Assert.Equal("plain text", code.Code);
    }

    [Fact]
    public void DiagramBlockCarriesSourceSpanOfTheFence()
    {
        const string markdown = """
            # Heading

            ```mermaid
            flowchart LR
                A --> B
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        Assert.Collection(
            document.Blocks,
            block => Assert.IsType<MarkdownHeadingBlock>(block),
            block =>
            {
                var diagram = Assert.IsType<MarkdownDiagramBlock>(block);
                Assert.NotNull(diagram.SourceSpan);
                Assert.True(diagram.SourceSpan!.Value.StartLine >= 2,
                    $"Expected fence to start on or after line 2, got {diagram.SourceSpan!.Value.StartLine}.");
                Assert.True(diagram.SourceSpan!.Value.EndLine >= diagram.SourceSpan!.Value.StartLine);
            });
    }

    [Fact]
    public void DiagramSourcePreservesLineBreaksAndIndentation()
    {
        const string markdown = """
            ```mermaid
            sequenceDiagram
                Alice->>Bob: Hi
                Bob-->>Alice: Hey
            ```
            """;

        var document = new MarkdigMarkdownDocumentRenderer().Render(markdown);

        var diagram = Assert.IsType<MarkdownDiagramBlock>(Assert.Single(document.Blocks));
        Assert.Equal(
            "sequenceDiagram\n    Alice->>Bob: Hi\n    Bob-->>Alice: Hey",
            diagram.Source);
    }
}
