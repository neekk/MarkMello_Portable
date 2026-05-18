using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;

namespace MarkMello.Presentation.Views.Markdown;

/// <summary>
/// Categorical view of a parsed SVG drawable. Used by SVG compatibility
/// tests to assert that real Naiad output produces the expected mix of
/// primitives (text labels, marker-end arrows, rectangles, etc.).
/// </summary>
internal enum AotSafeSvgImageDrawableKind
{
    Unknown = 0,
    Rectangle,
    Ellipse,
    Line,
    Polygon,
    Path,
    Text,
    MarkerInstance,
}

/// <summary>
/// Native AOT-safe SVG subset renderer for Markdown images and Mermaid
/// diagrams produced by Naiad.
///
/// The supported subset is deliberately narrow: static vector primitives
/// (rect/circle/ellipse/line/polyline/polygon/path), the constructs that
/// Naiad-generated diagrams actually use (<c>&lt;g transform="translate"&gt;</c>,
/// <c>&lt;text&gt;</c>, <c>&lt;foreignObject&gt;</c> with HTML labels, markers
/// referenced via <c>marker-end</c>, <c>stroke-dasharray</c>) and inline
/// presentation attributes or <c>style</c> attributes. Unsupported SVG
/// features are ignored rather than activating a reflection-heavy runtime
/// renderer in the viewer path (see ADR-0005 §7 and M5).
/// </summary>
internal sealed class AotSafeSvgImage : IImage
{
    private const double DefaultWidth = 300;
    private const double DefaultHeight = 150;
    private const double ForeignObjectDefaultFontSize = 13;
    private const double TextDefaultFontSize = 14;
    private static readonly SvgStyle DefaultStyle = new(
        Fill: Colors.Black,
        Stroke: null,
        StrokeWidth: 1,
        Opacity: 1,
        DashArray: null);

    private readonly IReadOnlyList<SvgDrawable> _drawables;
    private readonly Rect _viewBox;

    private AotSafeSvgImage(Size size, Rect viewBox, IReadOnlyList<SvgDrawable> drawables)
    {
        Size = size;
        _viewBox = viewBox;
        _drawables = drawables;
    }

    public Size Size { get; }

    // Test-only accessors. The drawable list is otherwise sealed inside the
    // image instance; surfacing primitive counts under InternalsVisibleTo
    // keeps the SVG compatibility tests behaviour-focused without exposing
    // implementation types to production callers.
    internal int DrawableCount => _drawables.Count;

    internal int CountDrawables(AotSafeSvgImageDrawableKind kind)
    {
        var count = 0;
        foreach (var drawable in _drawables)
        {
            if (ClassifyDrawable(drawable) == kind)
            {
                count++;
            }
        }
        return count;
    }

    internal IReadOnlyList<string> EnumerateTextContents()
    {
        var result = new List<string>();
        foreach (var drawable in _drawables)
        {
            if (drawable is SvgTextDrawable text)
            {
                result.Add(text.Text);
            }
        }
        return result;
    }

    public static bool TryLoad(byte[] bytes, out AotSafeSvgImage image)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        image = null!;
        if (bytes.Length == 0)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                XmlResolver = null,
            };

            using var reader = XmlReader.Create(stream, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root is null || !IsElement(root, "svg"))
            {
                return false;
            }

            var viewBox = ParseViewBox(root.Attribute("viewBox")?.Value);
            var width = ParseLength(root.Attribute("width")?.Value);
            var height = ParseLength(root.Attribute("height")?.Value);

            if (viewBox is null)
            {
                var fallbackWidth = NormalizeSize(width ?? DefaultWidth, DefaultWidth);
                var fallbackHeight = NormalizeSize(height ?? DefaultHeight, DefaultHeight);
                viewBox = new Rect(0, 0, fallbackWidth, fallbackHeight);
            }

            var imageWidth = NormalizeSize(width ?? viewBox.Value.Width, DefaultWidth);
            var imageHeight = NormalizeSize(height ?? viewBox.Value.Height, DefaultHeight);
            var state = new ParseState();
            CollectMarkerDefinitions(root, state);
            ParseChildren(root, DefaultStyle, new Point(0, 0), state);

            if (state.Drawables.Count == 0)
            {
                return false;
            }

            image = new AotSafeSvgImage(new Size(imageWidth, imageHeight), viewBox.Value, state.Drawables);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_drawables.Count == 0 || destRect.Width <= 0 || destRect.Height <= 0 || _viewBox.Width <= 0 || _viewBox.Height <= 0)
        {
            return;
        }

        var source = sourceRect.Width > 0 && sourceRect.Height > 0
            ? sourceRect
            : new Rect(Size);
        var scaleX = destRect.Width / source.Width;
        var scaleY = destRect.Height / source.Height;
        var viewScaleX = source.Width / _viewBox.Width;
        var viewScaleY = source.Height / _viewBox.Height;
        var transform = new SvgRenderTransform(
            ScaleX: scaleX * viewScaleX,
            ScaleY: scaleY * viewScaleY,
            OffsetX: destRect.X - source.X * scaleX - _viewBox.X * scaleX * viewScaleX,
            OffsetY: destRect.Y - source.Y * scaleY - _viewBox.Y * scaleY * viewScaleY);

        foreach (var drawable in _drawables)
        {
            drawable.Draw(context, transform);
        }
    }

    private static void CollectMarkerDefinitions(XElement root, ParseState state)
    {
        foreach (var defs in root.Descendants().Where(static element => element.Name.LocalName == "defs"))
        {
            foreach (var element in defs.Elements().Where(static element => element.Name.LocalName == "marker"))
            {
                var id = element.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var definition = TryParseMarkerDefinition(element);
                if (definition is not null)
                {
                    state.Markers[id] = definition;
                }
            }
        }
    }

    private static SvgMarkerDefinition? TryParseMarkerDefinition(XElement element)
    {
        var markerWidth = ParseLength(element.Attribute("markerWidth")?.Value) ?? 3;
        var markerHeight = ParseLength(element.Attribute("markerHeight")?.Value) ?? 3;
        if (markerWidth <= 0 || markerHeight <= 0)
        {
            return null;
        }

        var viewBox = ParseViewBox(element.Attribute("viewBox")?.Value)
            ?? new Rect(0, 0, markerWidth, markerHeight);
        var refX = ParseLength(element.Attribute("refX")?.Value) ?? 0;
        var refY = ParseLength(element.Attribute("refY")?.Value) ?? 0;
        var orient = element.Attribute("orient")?.Value?.Trim() ?? "0";

        // Parse marker children with identity translate and default style —
        // marker bodies are drawn inside a per-instance PushTransform later.
        var childState = new ParseState();
        ParseChildren(element, DefaultStyle, new Point(0, 0), childState);
        if (childState.Drawables.Count == 0)
        {
            return null;
        }

        return new SvgMarkerDefinition(
            ViewBox: viewBox,
            RefX: refX,
            RefY: refY,
            MarkerWidth: markerWidth,
            MarkerHeight: markerHeight,
            OrientAuto: string.Equals(orient, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(orient, "auto-start-reverse", StringComparison.OrdinalIgnoreCase),
            Drawables: childState.Drawables);
    }

    private static void ParseChildren(XElement parent, SvgStyle inheritedStyle, Point currentTranslate, ParseState state)
    {
        foreach (var child in parent.Elements())
        {
            var name = child.Name.LocalName;
            if (IsIgnoredContainer(name))
            {
                continue;
            }

            var style = ParseStyle(child, inheritedStyle);
            var localTranslate = ParseTransformTranslate(child.Attribute("transform")?.Value);
            var translate = new Point(currentTranslate.X + localTranslate.X, currentTranslate.Y + localTranslate.Y);
            switch (name)
            {
                case "svg":
                case "g":
                case "a":
                    ParseChildren(child, style, translate, state);
                    break;
                case "rect":
                    TryAddRect(child, style, translate, state.Drawables);
                    break;
                case "circle":
                    TryAddCircle(child, style, translate, state.Drawables);
                    break;
                case "ellipse":
                    TryAddEllipse(child, style, translate, state.Drawables);
                    break;
                case "line":
                    TryAddLine(child, style, translate, state);
                    break;
                case "polyline":
                    TryAddPoly(child, style, close: false, translate, state.Drawables);
                    break;
                case "polygon":
                    TryAddPoly(child, style, close: true, translate, state.Drawables);
                    break;
                case "path":
                    TryAddPath(child, style, translate, state);
                    break;
                case "text":
                    TryAddText(child, style, translate, state.Drawables);
                    break;
                case "foreignObject":
                    TryAddForeignObject(child, style, translate, state.Drawables);
                    break;
            }
        }
    }

    private static SvgStyle ParseStyle(XElement element, SvgStyle inherited)
    {
        var styleValues = ParseStyleAttribute(element.Attribute("style")?.Value);
        var fillText = GetStyleValue(element, styleValues, "fill");
        var strokeText = GetStyleValue(element, styleValues, "stroke");
        var strokeWidthText = GetStyleValue(element, styleValues, "stroke-width");
        var opacityText = GetStyleValue(element, styleValues, "opacity");
        var fillOpacityText = GetStyleValue(element, styleValues, "fill-opacity");
        var strokeOpacityText = GetStyleValue(element, styleValues, "stroke-opacity");
        var dashArrayText = GetStyleValue(element, styleValues, "stroke-dasharray");

        var opacity = Math.Clamp(ParseOpacity(opacityText, inherited.Opacity), 0, 1);
        var fill = ParseOptionalColor(fillText, inherited.Fill, opacity * ParseOpacity(fillOpacityText, 1));
        var stroke = ParseOptionalColor(strokeText, inherited.Stroke, opacity * ParseOpacity(strokeOpacityText, 1));
        var strokeWidth = ParseLength(strokeWidthText) ?? inherited.StrokeWidth;
        var dashArray = ParseDashArray(dashArrayText) ?? inherited.DashArray;

        return new SvgStyle(fill, stroke, NormalizeStrokeWidth(strokeWidth), opacity, dashArray);
    }

    private static Dictionary<string, string> ParseStyleAttribute(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = declaration.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator >= declaration.Length - 1)
            {
                continue;
            }

            values[declaration[..separator].Trim()] = declaration[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string? GetStyleValue(XElement element, Dictionary<string, string> styleValues, string name)
        => element.Attribute(name)?.Value is { } attributeValue
            ? attributeValue
            : styleValues.TryGetValue(name, out var styleValue) ? styleValue : null;

    private static Point ParseTransformTranslate(string? value)
    {
        // Naiad only emits translate() at the element level (see M5 fixtures).
        // Other transform functions are intentionally ignored rather than
        // bringing in a full transform-list parser the renderer would never
        // exercise — extending here is justified only by real Naiad output.
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Point(0, 0);
        }

        var trimmed = value.Trim();
        const string prefix = "translate(";
        var prefixIndex = trimmed.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
        {
            return new Point(0, 0);
        }

        var inner = trimmed[(prefixIndex + prefix.Length)..];
        var endIndex = inner.IndexOf(')', StringComparison.Ordinal);
        if (endIndex < 0)
        {
            return new Point(0, 0);
        }

        var numbers = ParseNumberList(inner[..endIndex]);
        return numbers.Count switch
        {
            0 => new Point(0, 0),
            1 => new Point(numbers[0], 0),
            _ => new Point(numbers[0], numbers[1]),
        };
    }

    private static List<double>? ParseDashArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var numbers = ParseNumberList(value);
        if (numbers.Count == 0)
        {
            return null;
        }

        // SVG odd-length dash arrays repeat to make the pattern even, e.g.
        // "5" means "5 5". Avalonia's DashStyle accepts arbitrary lists, but
        // mirroring the SVG semantics keeps the visual output predictable.
        if ((numbers.Count & 1) == 1)
        {
            var doubled = new List<double>(numbers.Count * 2);
            doubled.AddRange(numbers);
            doubled.AddRange(numbers);
            return doubled;
        }

        return numbers;
    }

    private static void TryAddRect(XElement element, SvgStyle style, Point translate, List<SvgDrawable> drawables)
    {
        var x = ParseLength(element.Attribute("x")?.Value) ?? 0;
        var y = ParseLength(element.Attribute("y")?.Value) ?? 0;
        var width = ParseLength(element.Attribute("width")?.Value) ?? 0;
        var height = ParseLength(element.Attribute("height")?.Value) ?? 0;
        if (width <= 0 || height <= 0 || !style.HasVisiblePaint)
        {
            return;
        }

        var rx = ParseLength(element.Attribute("rx")?.Value) ?? 0;
        var ry = ParseLength(element.Attribute("ry")?.Value) ?? rx;
        drawables.Add(new SvgRectDrawable(
            new Rect(x + translate.X, y + translate.Y, width, height),
            Math.Max(0, rx),
            Math.Max(0, ry),
            style));
    }

    private static void TryAddCircle(XElement element, SvgStyle style, Point translate, List<SvgDrawable> drawables)
    {
        var radius = ParseLength(element.Attribute("r")?.Value) ?? 0;
        if (radius <= 0 || !style.HasVisiblePaint)
        {
            return;
        }

        var center = new Point(
            (ParseLength(element.Attribute("cx")?.Value) ?? 0) + translate.X,
            (ParseLength(element.Attribute("cy")?.Value) ?? 0) + translate.Y);
        drawables.Add(new SvgEllipseDrawable(center, radius, radius, style));
    }

    private static void TryAddEllipse(XElement element, SvgStyle style, Point translate, List<SvgDrawable> drawables)
    {
        var rx = ParseLength(element.Attribute("rx")?.Value) ?? 0;
        var ry = ParseLength(element.Attribute("ry")?.Value) ?? 0;
        if (rx <= 0 || ry <= 0 || !style.HasVisiblePaint)
        {
            return;
        }

        var center = new Point(
            (ParseLength(element.Attribute("cx")?.Value) ?? 0) + translate.X,
            (ParseLength(element.Attribute("cy")?.Value) ?? 0) + translate.Y);
        drawables.Add(new SvgEllipseDrawable(center, rx, ry, style));
    }

    private static void TryAddLine(XElement element, SvgStyle style, Point translate, ParseState state)
    {
        if (style.Stroke is null || style.StrokeWidth <= 0 || style.Opacity <= 0)
        {
            return;
        }

        var start = new Point(
            (ParseLength(element.Attribute("x1")?.Value) ?? 0) + translate.X,
            (ParseLength(element.Attribute("y1")?.Value) ?? 0) + translate.Y);
        var end = new Point(
            (ParseLength(element.Attribute("x2")?.Value) ?? 0) + translate.X,
            (ParseLength(element.Attribute("y2")?.Value) ?? 0) + translate.Y);
        state.Drawables.Add(new SvgLineDrawable(start, end, style));

        AddEndMarkerIfPresent(element, start, end, state);
    }

    private static void TryAddPoly(XElement element, SvgStyle style, bool close, Point translate, List<SvgDrawable> drawables)
    {
        if (!style.HasVisiblePaint)
        {
            return;
        }

        var points = ParsePoints(element.Attribute("points")?.Value, translate);
        if (points.Count < 2)
        {
            return;
        }

        drawables.Add(new SvgPolyDrawable(points, close, style));
    }

    private static void TryAddPath(XElement element, SvgStyle style, Point translate, ParseState state)
    {
        if (!style.HasVisiblePaint)
        {
            return;
        }

        var pathData = element.Attribute("d")?.Value;
        if (string.IsNullOrWhiteSpace(pathData))
        {
            return;
        }

        // Defer StreamGeometry.Parse until Draw time. Parsing requires the
        // Avalonia platform render interface to be initialized, which is not
        // guaranteed during cold-start unit tests; equally, when parse fails
        // the diagram should degrade silently inside Draw rather than during
        // load.
        state.Drawables.Add(new SvgGeometryDrawable(pathData, translate, style));

        var tangent = TryGetPathEndTangent(pathData, translate);
        if (tangent is { } resolved)
        {
            AddEndMarkerIfPresent(element, resolved.PreviousPoint, resolved.EndPoint, state);
        }
    }

    private static void TryAddText(XElement element, SvgStyle style, Point translate, List<SvgDrawable> drawables)
    {
        var text = ExtractElementText(element);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var x = ParseLength(element.Attribute("x")?.Value) ?? 0;
        var y = ParseLength(element.Attribute("y")?.Value) ?? 0;
        var styleValues = ParseStyleAttribute(element.Attribute("style")?.Value);
        var fontSize = ParseLength(GetStyleValue(element, styleValues, "font-size")) ?? TextDefaultFontSize;
        var fontFamily = GetStyleValue(element, styleValues, "font-family");
        var fontWeight = ParseFontWeight(GetStyleValue(element, styleValues, "font-weight"));
        var textAnchor = ParseTextAnchor(GetStyleValue(element, styleValues, "text-anchor"));
        var dominantBaseline = ParseDominantBaseline(GetStyleValue(element, styleValues, "dominant-baseline"));
        // Mermaid's <text> elements typically rely on the fill attribute or
        // the inherited paint; if neither is set, fall back to a visible
        // ink color rather than rendering invisible glyphs.
        var fill = style.Fill ?? Colors.Black;

        drawables.Add(new SvgTextDrawable(
            new Point(x + translate.X, y + translate.Y),
            text,
            fontSize,
            fontFamily,
            fontWeight,
            fill,
            style.Opacity,
            textAnchor,
            dominantBaseline));
    }

    private static void TryAddForeignObject(XElement element, SvgStyle style, Point translate, List<SvgDrawable> drawables)
    {
        var x = ParseLength(element.Attribute("x")?.Value) ?? 0;
        var y = ParseLength(element.Attribute("y")?.Value) ?? 0;
        var width = ParseLength(element.Attribute("width")?.Value) ?? 0;
        var height = ParseLength(element.Attribute("height")?.Value) ?? 0;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var text = ExtractForeignObjectText(element);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var fill = style.Fill ?? Colors.Black;
        drawables.Add(new SvgTextDrawable(
            new Point(x + translate.X + width / 2, y + translate.Y + height / 2),
            text,
            ForeignObjectDefaultFontSize,
            FontFamilyName: null,
            FontWeight: FontWeight.Normal,
            Fill: fill,
            Opacity: style.Opacity,
            TextAnchor: SvgTextAnchor.Middle,
            Baseline: SvgDominantBaseline.Middle));
    }

    private static void AddEndMarkerIfPresent(XElement element, Point previousPoint, Point endPoint, ParseState state)
    {
        var markerRef = element.Attribute("marker-end")?.Value;
        if (string.IsNullOrWhiteSpace(markerRef))
        {
            return;
        }

        var id = ExtractFragmentReference(markerRef);
        if (id is null || !state.Markers.TryGetValue(id, out var marker))
        {
            return;
        }

        var dx = endPoint.X - previousPoint.X;
        var dy = endPoint.Y - previousPoint.Y;
        var angle = (dx, dy) is (0, 0) ? 0 : Math.Atan2(dy, dx);
        state.Drawables.Add(new SvgMarkerInstanceDrawable(marker, endPoint, angle));
    }

    private static string? ExtractFragmentReference(string value)
    {
        var trimmed = value.Trim();
        const string prefix = "url(";
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !trimmed.EndsWith(')'))
        {
            return null;
        }

        var inside = trimmed[prefix.Length..^1].Trim().Trim('"', '\'');
        return inside.StartsWith('#') ? inside[1..] : null;
    }

    private static string ExtractElementText(XElement element)
    {
        // <text> may contain plain text or tspan children; collect the
        // concatenated text content, normalising whitespace runs that
        // span XML attribute formatting (newlines/tabs).
        var raw = element.Value;
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        return CollapseWhitespace(raw);
    }

    private static string ExtractForeignObjectText(XElement element)
    {
        var builder = new StringBuilder();
        foreach (var paragraph in element.Descendants().Where(static descendant => descendant.Name.LocalName == "p"))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(CollapseWhitespace(paragraph.Value));
        }

        if (builder.Length == 0)
        {
            // Foreign object without explicit <p> elements — fall back to
            // any text descendants (Naiad currently always wraps in <p>,
            // but we keep this as a defensive fallback).
            return CollapseWhitespace(element.Value);
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var inWhitespace = false;
        foreach (var character in value)
        {
            if (character is '\r')
            {
                continue;
            }

            if (char.IsWhiteSpace(character) && character != '\n')
            {
                if (!inWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                    inWhitespace = true;
                }
                continue;
            }

            inWhitespace = false;
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static SvgTextAnchor ParseTextAnchor(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "middle" => SvgTextAnchor.Middle,
            "end" => SvgTextAnchor.End,
            _ => SvgTextAnchor.Start,
        };

    private static SvgDominantBaseline ParseDominantBaseline(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "middle" or "central" => SvgDominantBaseline.Middle,
            "hanging" or "text-before-edge" => SvgDominantBaseline.Hanging,
            "bottom" or "text-after-edge" or "text-bottom" or "ideographic" => SvgDominantBaseline.Bottom,
            _ => SvgDominantBaseline.Alphabetic,
        };

    private static FontWeight ParseFontWeight(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return FontWeight.Normal;
        }

        var trimmed = value.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "bold":
            case "bolder":
                return FontWeight.Bold;
            case "normal":
            case "lighter":
                return FontWeight.Normal;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return (FontWeight)Math.Clamp(numeric, 100, 900);
        }

        return FontWeight.Normal;
    }

    private static (Point PreviousPoint, Point EndPoint)? TryGetPathEndTangent(string pathData, Point translate)
    {
        // Walk SVG path commands to recover the last two anchor points so we
        // can place a marker-end arrow with the correct orientation. Only
        // the command kinds Naiad emits today (M, L, H, V, C, S, Q, T, A,
        // Z, and their relative counterparts) are handled; unknown commands
        // simply terminate parsing with whatever points have been recorded.
        var span = pathData.AsSpan();
        Point? previous = null;
        Point? current = null;
        Point subpathStart = default;
        var hasSubpathStart = false;
        var index = 0;
        char? lastCommand = null;

        while (index < span.Length)
        {
            while (index < span.Length && IsPathWhitespace(span[index]))
            {
                index++;
            }
            if (index >= span.Length)
            {
                break;
            }

            char command;
            if (IsCommandLetter(span[index]))
            {
                command = span[index];
                index++;
            }
            else if (lastCommand is { } continuation)
            {
                command = continuation == 'M' ? 'L' : continuation == 'm' ? 'l' : continuation;
            }
            else
            {
                break;
            }

            lastCommand = command;
            var numbers = TakeNumbers(span, ref index, GetCommandOperandCount(command));
            switch (command)
            {
                case 'M':
                    if (numbers.Count >= 2)
                    {
                        var point = new Point(numbers[0], numbers[1]);
                        previous = current;
                        current = point;
                        subpathStart = point;
                        hasSubpathStart = true;
                        for (var trailing = 2; trailing + 1 < numbers.Count; trailing += 2)
                        {
                            previous = current;
                            current = new Point(numbers[trailing], numbers[trailing + 1]);
                        }
                    }
                    break;
                case 'm':
                    if (numbers.Count >= 2 && current is { } mCur)
                    {
                        var point = new Point(mCur.X + numbers[0], mCur.Y + numbers[1]);
                        previous = current;
                        current = point;
                        subpathStart = point;
                        hasSubpathStart = true;
                        for (var trailing = 2; trailing + 1 < numbers.Count; trailing += 2)
                        {
                            previous = current;
                            current = new Point(current.Value.X + numbers[trailing], current.Value.Y + numbers[trailing + 1]);
                        }
                    }
                    break;
                case 'L':
                case 'T':
                    for (var i = 0; i + 1 < numbers.Count; i += 2)
                    {
                        previous = current;
                        current = new Point(numbers[i], numbers[i + 1]);
                    }
                    break;
                case 'l':
                case 't':
                    for (var i = 0; i + 1 < numbers.Count && current is not null; i += 2)
                    {
                        previous = current;
                        current = new Point(current.Value.X + numbers[i], current.Value.Y + numbers[i + 1]);
                    }
                    break;
                case 'H':
                    foreach (var hx in numbers)
                    {
                        if (current is null)
                        {
                            break;
                        }
                        previous = current;
                        current = new Point(hx, current.Value.Y);
                    }
                    break;
                case 'h':
                    foreach (var hx in numbers)
                    {
                        if (current is null)
                        {
                            break;
                        }
                        previous = current;
                        current = new Point(current.Value.X + hx, current.Value.Y);
                    }
                    break;
                case 'V':
                    foreach (var vy in numbers)
                    {
                        if (current is null)
                        {
                            break;
                        }
                        previous = current;
                        current = new Point(current.Value.X, vy);
                    }
                    break;
                case 'v':
                    foreach (var vy in numbers)
                    {
                        if (current is null)
                        {
                            break;
                        }
                        previous = current;
                        current = new Point(current.Value.X, current.Value.Y + vy);
                    }
                    break;
                case 'C':
                    for (var i = 0; i + 5 < numbers.Count; i += 6)
                    {
                        previous = new Point(numbers[i + 2], numbers[i + 3]);
                        current = new Point(numbers[i + 4], numbers[i + 5]);
                    }
                    break;
                case 'c':
                    for (var i = 0; i + 5 < numbers.Count && current is not null; i += 6)
                    {
                        var baseX = current.Value.X;
                        var baseY = current.Value.Y;
                        previous = new Point(baseX + numbers[i + 2], baseY + numbers[i + 3]);
                        current = new Point(baseX + numbers[i + 4], baseY + numbers[i + 5]);
                    }
                    break;
                case 'S':
                case 'Q':
                    for (var i = 0; i + 3 < numbers.Count; i += 4)
                    {
                        previous = new Point(numbers[i], numbers[i + 1]);
                        current = new Point(numbers[i + 2], numbers[i + 3]);
                    }
                    break;
                case 's':
                case 'q':
                    for (var i = 0; i + 3 < numbers.Count && current is not null; i += 4)
                    {
                        var baseX = current.Value.X;
                        var baseY = current.Value.Y;
                        previous = new Point(baseX + numbers[i], baseY + numbers[i + 1]);
                        current = new Point(baseX + numbers[i + 2], baseY + numbers[i + 3]);
                    }
                    break;
                case 'A':
                    for (var i = 0; i + 6 < numbers.Count; i += 7)
                    {
                        previous = current;
                        current = new Point(numbers[i + 5], numbers[i + 6]);
                    }
                    break;
                case 'a':
                    for (var i = 0; i + 6 < numbers.Count && current is not null; i += 7)
                    {
                        previous = current;
                        current = new Point(current.Value.X + numbers[i + 5], current.Value.Y + numbers[i + 6]);
                    }
                    break;
                case 'Z':
                case 'z':
                    if (hasSubpathStart && current is not null)
                    {
                        previous = current;
                        current = subpathStart;
                    }
                    break;
                default:
                    return null;
            }
        }

        if (current is null || previous is null)
        {
            return null;
        }

        return (
            new Point(previous.Value.X + translate.X, previous.Value.Y + translate.Y),
            new Point(current.Value.X + translate.X, current.Value.Y + translate.Y));
    }

    private static bool IsCommandLetter(char value)
        => (value >= 'A' && value <= 'Z' && value is not 'E') || (value >= 'a' && value <= 'z' && value is not 'e');

    private static bool IsPathWhitespace(char value)
        => char.IsWhiteSpace(value) || value == ',';

    private static List<double> TakeNumbers(ReadOnlySpan<char> span, ref int index, int maxOperands)
    {
        var result = new List<double>(maxOperands > 0 ? maxOperands : 4);
        while (index < span.Length)
        {
            while (index < span.Length && IsPathWhitespace(span[index]))
            {
                index++;
            }
            if (index >= span.Length || IsCommandLetter(span[index]))
            {
                break;
            }

            var start = index;
            if (span[index] is '+' or '-')
            {
                index++;
            }
            var sawDigit = false;
            while (index < span.Length && char.IsDigit(span[index]))
            {
                index++;
                sawDigit = true;
            }
            if (index < span.Length && span[index] == '.')
            {
                index++;
                while (index < span.Length && char.IsDigit(span[index]))
                {
                    index++;
                    sawDigit = true;
                }
            }
            if (index < span.Length && (span[index] is 'e' or 'E'))
            {
                index++;
                if (index < span.Length && span[index] is '+' or '-')
                {
                    index++;
                }
                while (index < span.Length && char.IsDigit(span[index]))
                {
                    index++;
                }
            }

            if (!sawDigit || start == index)
            {
                index = start + 1;
                continue;
            }

            if (double.TryParse(span[start..index], NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                result.Add(number);
            }
        }

        return result;
    }

    private static int GetCommandOperandCount(char command)
        => command switch
        {
            'M' or 'm' or 'L' or 'l' or 'T' or 't' => 2,
            'H' or 'h' or 'V' or 'v' => 1,
            'C' or 'c' => 6,
            'S' or 's' or 'Q' or 'q' => 4,
            'A' or 'a' => 7,
            _ => 0,
        };

    private static List<Point> ParsePoints(string? value, Point translate)
    {
        var points = new List<Point>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return points;
        }

        var numbers = ParseNumberList(value);
        for (var index = 0; index + 1 < numbers.Count; index += 2)
        {
            points.Add(new Point(numbers[index] + translate.X, numbers[index + 1] + translate.Y));
        }

        return points;
    }

    private static Rect? ParseViewBox(string? value)
    {
        var numbers = ParseNumberList(value);
        if (numbers.Count != 4 || numbers[2] <= 0 || numbers[3] <= 0)
        {
            return null;
        }

        return new Rect(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    private static List<double> ParseNumberList(string? value)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var span = value.AsSpan();
        var start = -1;
        for (var index = 0; index <= span.Length; index++)
        {
            var isSeparator = index == span.Length || char.IsWhiteSpace(span[index]) || span[index] == ',';
            if (!isSeparator && start < 0)
            {
                start = index;
            }

            if (!isSeparator || start < 0)
            {
                continue;
            }

            var token = span[start..index].Trim();
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                result.Add(number);
            }

            start = -1;
        }

        return result;
    }

    private static double? ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith('%'))
        {
            return null;
        }

        var span = trimmed.AsSpan();
        var end = 0;
        while (end < span.Length && IsLengthCharacter(span[end]))
        {
            end++;
        }

        if (end == 0)
        {
            return null;
        }

        return double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static bool IsLengthCharacter(char value)
        => char.IsDigit(value) || value is '+' or '-' or '.' or 'e' or 'E';

    private static Color? ParseOptionalColor(string? value, Color? inherited, double opacity)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("inherit", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyOpacity(inherited, opacity);
        }

        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryParseColor(value, out var color)
            ? ApplyOpacity(color, opacity)
            : ApplyOpacity(inherited, opacity);
    }

    private static bool TryParseColor(string value, out Color color)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('#'))
        {
            return TryParseHexColor(trimmed, out color);
        }

        if (trimmed.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && trimmed.EndsWith(')'))
        {
            return TryParseRgbColor(trimmed, out color);
        }

        if (NamedColors.TryGetValue(trimmed, out color))
        {
            return true;
        }

        color = default;
        return false;
    }

    private static bool TryParseHexColor(string value, out Color color)
    {
        color = default;
        var hex = value.AsSpan(1);
        if (hex.Length == 3)
        {
            var r = ParseHexNibble(hex[0]);
            var g = ParseHexNibble(hex[1]);
            var b = ParseHexNibble(hex[2]);
            if (r < 0 || g < 0 || b < 0)
            {
                return false;
            }

            color = Color.FromArgb(255, ExpandNibble(r), ExpandNibble(g), ExpandNibble(b));
            return true;
        }

        if (hex.Length is not (6 or 8))
        {
            return false;
        }

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
        {
            return false;
        }

        color = hex.Length == 6
            ? Color.FromArgb(255, (byte)(raw >> 16), (byte)(raw >> 8), (byte)raw)
            : Color.FromArgb((byte)raw, (byte)(raw >> 24), (byte)(raw >> 16), (byte)(raw >> 8));
        return true;
    }

    private static bool TryParseRgbColor(string value, out Color color)
    {
        color = default;
        var numbers = ParseNumberList(value[4..^1]);
        if (numbers.Count < 3)
        {
            return false;
        }

        color = Color.FromArgb(
            255,
            ClampColorByte(numbers[0]),
            ClampColorByte(numbers[1]),
            ClampColorByte(numbers[2]));
        return true;
    }

    private static byte ClampColorByte(double value)
        => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static int ParseHexNibble(char value)
        => value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };

    private static byte ExpandNibble(int value)
        => (byte)((value << 4) | value);

    private static Color? ApplyOpacity(Color? color, double opacity)
    {
        if (color is not { } source)
        {
            return null;
        }

        var alpha = (byte)Math.Clamp(Math.Round(source.A * Math.Clamp(opacity, 0, 1)), 0, 255);
        return Color.FromArgb(alpha, source.R, source.G, source.B);
    }

    private static double ParseOpacity(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity)
            ? Math.Clamp(opacity, 0, 1)
            : fallback;

    private static double NormalizeSize(double value, double fallback)
        => double.IsFinite(value) && value > 0 ? value : fallback;

    private static double NormalizeStrokeWidth(double value)
        => double.IsFinite(value) && value > 0 ? value : 1;

    private static bool IsElement(XElement element, string name)
        => element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredContainer(string name)
        => name is "defs" or "style" or "script" or "metadata" or "title" or "desc" or "symbol" or "mask" or "clipPath";

    private static readonly Dictionary<string, Color> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Colors.Black,
        ["white"] = Colors.White,
        ["red"] = Colors.Red,
        ["green"] = Colors.Green,
        ["blue"] = Colors.Blue,
        ["gray"] = Colors.Gray,
        ["grey"] = Colors.Gray,
        ["silver"] = Colors.Silver,
        ["yellow"] = Colors.Yellow,
        ["orange"] = Colors.Orange,
        ["purple"] = Colors.Purple,
        ["brown"] = Colors.Brown,
    };

    private static AotSafeSvgImageDrawableKind ClassifyDrawable(SvgDrawable drawable)
        => drawable switch
        {
            SvgRectDrawable => AotSafeSvgImageDrawableKind.Rectangle,
            SvgEllipseDrawable => AotSafeSvgImageDrawableKind.Ellipse,
            SvgLineDrawable => AotSafeSvgImageDrawableKind.Line,
            SvgPolyDrawable => AotSafeSvgImageDrawableKind.Polygon,
            SvgGeometryDrawable => AotSafeSvgImageDrawableKind.Path,
            SvgTextDrawable => AotSafeSvgImageDrawableKind.Text,
            SvgMarkerInstanceDrawable => AotSafeSvgImageDrawableKind.MarkerInstance,
            _ => AotSafeSvgImageDrawableKind.Unknown,
        };

    private sealed class ParseState
    {
        public List<SvgDrawable> Drawables { get; } = new();
        public Dictionary<string, SvgMarkerDefinition> Markers { get; } = new(StringComparer.Ordinal);
    }

    private enum SvgTextAnchor
    {
        Start = 0,
        Middle = 1,
        End = 2,
    }

    private enum SvgDominantBaseline
    {
        Alphabetic = 0,
        Middle = 1,
        Hanging = 2,
        Bottom = 3,
    }

    private sealed record SvgMarkerDefinition(
        Rect ViewBox,
        double RefX,
        double RefY,
        double MarkerWidth,
        double MarkerHeight,
        bool OrientAuto,
        IReadOnlyList<SvgDrawable> Drawables);

    private abstract record SvgDrawable(SvgStyle Style)
    {
        protected IBrush? FillBrush => Style.Fill is { } fill ? new SolidColorBrush(fill) : null;

        protected Pen? StrokePen(SvgRenderTransform transform)
        {
            if (Style.Stroke is not { } stroke || Style.StrokeWidth <= 0)
            {
                return null;
            }

            var thickness = Math.Max(0.5, Style.StrokeWidth * transform.StrokeScale);
            if (Style.DashArray is not { Count: > 0 } dashes)
            {
                return new Pen(new SolidColorBrush(stroke), thickness);
            }

            // Avalonia's DashStyle measures dashes in pen-width units. SVG
            // dash arrays are in user units, so divide by the resolved
            // pen thickness to keep the on-screen pattern accurate.
            var penThicknessForDash = Math.Max(0.5, Style.StrokeWidth);
            var dashArray = new Avalonia.Collections.AvaloniaList<double>(dashes.Count);
            foreach (var dash in dashes)
            {
                dashArray.Add(dash / penThicknessForDash);
            }
            return new Pen(new SolidColorBrush(stroke), thickness)
            {
                DashStyle = new DashStyle(dashArray, 0),
            };
        }

        public abstract void Draw(DrawingContext context, SvgRenderTransform transform);
    }

    private sealed record SvgRectDrawable(Rect Rect, double RadiusX, double RadiusY, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            var rect = transform.Transform(Rect);
            context.DrawRectangle(
                FillBrush,
                StrokePen(transform),
                rect,
                RadiusX * transform.ScaleX,
                RadiusY * transform.ScaleY);
        }
    }

    private sealed record SvgEllipseDrawable(Point Center, double RadiusX, double RadiusY, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            context.DrawEllipse(
                FillBrush,
                StrokePen(transform),
                transform.Transform(Center),
                RadiusX * transform.ScaleX,
                RadiusY * transform.ScaleY);
        }
    }

    private sealed record SvgLineDrawable(Point Start, Point End, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            if (StrokePen(transform) is { } pen)
            {
                context.DrawLine(pen, transform.Transform(Start), transform.Transform(End));
            }
        }
    }

    private sealed record SvgPolyDrawable(IReadOnlyList<Point> Points, bool Close, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            if (Points.Count < 2)
            {
                return;
            }

            var pathData = BuildPathData(Points, Close);
            try
            {
                var geometry = StreamGeometry.Parse(pathData);
                using (context.PushTransform(transform.Matrix))
                {
                    context.DrawGeometry(FillBrush, StrokePen(transform), geometry);
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                DrawAsLines(context, transform);
            }
        }

        private void DrawAsLines(DrawingContext context, SvgRenderTransform transform)
        {
            var pen = StrokePen(transform);
            if (pen is null)
            {
                return;
            }

            for (var index = 1; index < Points.Count; index++)
            {
                context.DrawLine(pen, transform.Transform(Points[index - 1]), transform.Transform(Points[index]));
            }

            if (Close)
            {
                context.DrawLine(pen, transform.Transform(Points[^1]), transform.Transform(Points[0]));
            }
        }

        private static string BuildPathData(IReadOnlyList<Point> points, bool close)
        {
            var builder = new StringBuilder();
            builder.Append(CultureInfo.InvariantCulture, $"M {points[0].X} {points[0].Y}");
            for (var index = 1; index < points.Count; index++)
            {
                builder.Append(CultureInfo.InvariantCulture, $" L {points[index].X} {points[index].Y}");
            }

            if (close)
            {
                builder.Append(" Z");
            }

            return builder.ToString();
        }
    }

    private sealed record SvgGeometryDrawable(string PathData, Point LocalTranslate, SvgStyle Style) : SvgDrawable(Style)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            StreamGeometry geometry;
            try
            {
                geometry = StreamGeometry.Parse(PathData);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                // Unsupported path data degrades to the normal image
                // fallback; do not break the document render path.
                return;
            }

            if (LocalTranslate.X != 0 || LocalTranslate.Y != 0)
            {
                geometry.Transform = new MatrixTransform(Matrix.CreateTranslation(LocalTranslate.X, LocalTranslate.Y));
            }

            using (context.PushTransform(transform.Matrix))
            {
                context.DrawGeometry(FillBrush, StrokePen(transform), geometry);
            }
        }
    }

    private sealed record SvgTextDrawable(
        Point Anchor,
        string Text,
        double FontSize,
        string? FontFamilyName,
        FontWeight FontWeight,
        Color Fill,
        double Opacity,
        SvgTextAnchor TextAnchor,
        SvgDominantBaseline Baseline) : SvgDrawable(new SvgStyle(Fill, Stroke: null, StrokeWidth: 0, Opacity: Opacity, DashArray: null))
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            if (Opacity <= 0 || string.IsNullOrEmpty(Text))
            {
                return;
            }

            var screenAnchor = transform.Transform(Anchor);
            var screenFontSize = Math.Max(1, FontSize * Math.Abs(transform.ScaleY));
            var typeface = ResolveTypeface(FontFamilyName, FontWeight);
            var brush = new SolidColorBrush(ApplyOpacity(Fill, Opacity) ?? Fill);
            var formatted = new FormattedText(
                Text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                screenFontSize,
                brush);

            var offsetX = TextAnchor switch
            {
                SvgTextAnchor.Middle => -formatted.Width / 2,
                SvgTextAnchor.End => -formatted.Width,
                _ => 0,
            };

            var offsetY = Baseline switch
            {
                SvgDominantBaseline.Middle => -formatted.Height / 2,
                SvgDominantBaseline.Hanging => 0,
                SvgDominantBaseline.Bottom => -formatted.Height,
                _ => -formatted.Baseline,
            };

            context.DrawText(formatted, new Point(screenAnchor.X + offsetX, screenAnchor.Y + offsetY));
        }

        private static Typeface ResolveTypeface(string? fontFamily, FontWeight weight)
        {
            if (string.IsNullOrWhiteSpace(fontFamily))
            {
                return new Typeface(FontFamily.Default, FontStyle.Normal, weight);
            }

            // SVG font-family is a comma-separated fallback list; Avalonia's
            // FontFamily accepts a single family. Take the first non-empty
            // token, otherwise fall back to the system default so glyphs
            // always render.
            foreach (var candidate in fontFamily.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var unquoted = candidate.Trim('"', '\'').Trim();
                if (unquoted.Length == 0)
                {
                    continue;
                }

                try
                {
                    return new Typeface(new FontFamily(unquoted), FontStyle.Normal, weight);
                }
                catch (ArgumentException)
                {
                    continue;
                }
            }

            return new Typeface(FontFamily.Default, FontStyle.Normal, weight);
        }
    }

    private sealed record SvgMarkerInstanceDrawable(
        SvgMarkerDefinition Marker,
        Point Endpoint,
        double Angle) : SvgDrawable(DefaultStyle)
    {
        public override void Draw(DrawingContext context, SvgRenderTransform transform)
        {
            var viewBox = Marker.ViewBox;
            if (viewBox.Width <= 0 || viewBox.Height <= 0)
            {
                return;
            }

            // Combined transform: marker-local viewBox → screen.
            // Steps (in user-space ordering):
            //   1. shift the marker's reference point to the origin;
            //   2. scale marker-local units to user units (markerWidth/Height);
            //   3. rotate by the path-end direction so the arrow follows the line;
            //   4. translate to the endpoint;
            //   5. map the resulting user-space point to screen coords.
            var endpointScreen = transform.Transform(Endpoint);
            var markerScaleX = Marker.MarkerWidth / viewBox.Width * transform.ScaleX;
            var markerScaleY = Marker.MarkerHeight / viewBox.Height * transform.ScaleY;
            var cos = Math.Cos(Angle);
            var sin = Math.Sin(Angle);

            var matrix =
                Matrix.CreateTranslation(-Marker.RefX, -Marker.RefY)
                * new Matrix(markerScaleX, 0, 0, markerScaleY, 0, 0)
                * new Matrix(cos, sin, -sin, cos, 0, 0)
                * Matrix.CreateTranslation(endpointScreen.X, endpointScreen.Y);

            using (context.PushTransform(matrix))
            {
                foreach (var drawable in Marker.Drawables)
                {
                    drawable.Draw(context, SvgRenderTransform.Identity);
                }
            }
        }
    }

    private readonly record struct SvgStyle(Color? Fill, Color? Stroke, double StrokeWidth, double Opacity, IReadOnlyList<double>? DashArray)
    {
        public bool HasVisiblePaint => Opacity > 0 && (Fill is not null || Stroke is not null && StrokeWidth > 0);
    }

    private readonly record struct SvgRenderTransform(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
    {
        public static SvgRenderTransform Identity { get; } = new(1, 1, 0, 0);

        public double StrokeScale => Math.Min(Math.Abs(ScaleX), Math.Abs(ScaleY));
        public Matrix Matrix => new(ScaleX, 0, 0, ScaleY, OffsetX, OffsetY);

        public Point Transform(Point point)
            => new(point.X * ScaleX + OffsetX, point.Y * ScaleY + OffsetY);

        public Rect Transform(Rect rect)
            => new(Transform(new Point(rect.X, rect.Y)), new Size(rect.Width * ScaleX, rect.Height * ScaleY));
    }
}
