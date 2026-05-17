using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MarkMello.Domain;

namespace MarkMello.Presentation.Views.Markdown;

/// <summary>
/// Block-level visual for <see cref="MarkdownDiagramBlock"/>. Operates on the
/// already-materialized <see cref="DiagramRenderResult"/> attached to the
/// block by <c>RenderMarkdownDocumentUseCase</c> — this view does NOT call
/// the diagram service. Two visual states:
///
/// <list type="bullet">
///   <item>Success: SVG payload from the renderer is fed through
///   <see cref="AotSafeSvgImage"/> and displayed as a native picture inside
///   a horizontal scroller for oversize diagrams.</item>
///   <item>Failure: a quiet error block carrying the dialect name, the
///   renderer message and the original source so the author keeps access to
///   what they wrote (ADR-0005 §6, §8).</item>
/// </list>
///
/// The control is intentionally not part of the document text map — diagram
/// content does not pollute continuous text selection (ADR-0005 §8). Final
/// selection/copy semantics are refined in M6.
/// </summary>
internal sealed class MarkdownDiagramBlockView : ContentControl
{
    private const double DefaultFontSize = 13;

    public MarkdownDiagramBlockView(MarkdownDiagramBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        UseLayoutRounding = true;

        Content = BuildContent(block);
    }

    private static Border BuildContent(MarkdownDiagramBlock block)
        => block.RenderResult switch
        {
            DiagramRenderResult.Success success => BuildSuccess(success, block),
            DiagramRenderResult.Failure failure => BuildFailure(failure, block.Kind),
            _ => BuildPending(block),
        };

    private static Border BuildSuccess(DiagramRenderResult.Success success, MarkdownDiagramBlock block)
    {
        var svgBytes = Encoding.UTF8.GetBytes(success.Svg);

        if (!AotSafeSvgImage.TryLoad(svgBytes, out var image))
        {
            // The SVG produced by the backend uses constructs the native
            // AOT-safe SVG path does not yet support (see ADR-0005 §7 and
            // M5). Surface that honestly without falling back to a code
            // block — the diagram source is still preserved in the
            // document model.
            return BuildSvgUnsupportedPlaceholder(block);
        }

        var imageControl = new Image
        {
            Source = image,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Center,
            UseLayoutRounding = true,
        };

        return new Border
        {
            Classes = { "mm-md-diagram", "mm-md-diagram-success" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = imageControl,
            },
        };
    }

    private static Border BuildFailure(DiagramRenderResult.Failure failure, MarkdownDiagramKind kind)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
        };

        stack.Children.Add(new TextBlock
        {
            Text = $"{kind} diagram could not be rendered",
            FontWeight = FontWeight.SemiBold,
            FontSize = DefaultFontSize,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "mm-md-diagram-error-title" },
        });

        if (!string.IsNullOrWhiteSpace(failure.Message))
        {
            stack.Children.Add(new TextBlock
            {
                Text = failure.Message,
                FontSize = DefaultFontSize,
                TextWrapping = TextWrapping.Wrap,
                Classes = { "mm-md-diagram-error-message" },
            });
        }

        stack.Children.Add(BuildSourceBlock(failure.Source));

        return new Border
        {
            Classes = { "mm-md-diagram", "mm-md-diagram-error" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private static Border BuildPending(MarkdownDiagramBlock block)
    {
        // If we ever reach the view with an un-materialized RenderResult,
        // surface it as an error rather than silently rendering as a code
        // block (success path must not be impersonated by source dump,
        // ADR-0005 §3).
        return BuildFailure(
            new DiagramRenderResult.Failure(
                "Diagram render was not materialized. This is an application composition error.",
                block.Source),
            block.Kind);
    }

    private static Border BuildSvgUnsupportedPlaceholder(MarkdownDiagramBlock block)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
        };

        stack.Children.Add(new TextBlock
        {
            Text = $"{block.Kind} diagram rendered, but the SVG features used are not yet supported by the native viewer",
            FontWeight = FontWeight.SemiBold,
            FontSize = DefaultFontSize,
            TextWrapping = TextWrapping.Wrap,
            Classes = { "mm-md-diagram-error-title" },
        });

        stack.Children.Add(BuildSourceBlock(block.Source));

        return new Border
        {
            Classes = { "mm-md-diagram", "mm-md-diagram-svg-unsupported" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(16, 14),
            Child = stack,
        };
    }

    private static ScrollViewer BuildSourceBlock(string source)
    {
        var sourceText = new TextBlock
        {
            Text = source,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Menlo, Monaco, monospace"),
            FontSize = DefaultFontSize - 1,
            TextWrapping = TextWrapping.NoWrap,
            UseLayoutRounding = true,
        };
        sourceText.Classes.Add("mm-md-diagram-source");

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = sourceText,
        };
    }
}
