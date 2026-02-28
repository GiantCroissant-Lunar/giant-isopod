using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace GiantIsopod.Hosts.CompleteApp;

/// <summary>
/// Converts Markdown text to Godot BBCode for RichTextLabel rendering.
/// Uses Markdig AST to produce styled output with headings, bold, italic,
/// code blocks, lists, and links.
/// </summary>
public static class MarkdownBBCode
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string Convert(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var doc = Markdown.Parse(markdown, Pipeline);
        var sb = new StringBuilder();
        RenderBlocks(doc, sb);
        return sb.ToString();
    }

    private static void RenderBlocks(ContainerBlock container, StringBuilder sb)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var hSize = heading.Level switch
                    {
                        1 => 28, 2 => 22, 3 => 18, _ => 16
                    };
                    sb.Append($"[font_size={hSize}][b][color=#66cc77]");
                    RenderInlines(heading.Inline, sb);
                    sb.AppendLine("[/color][/b][/font_size]");
                    sb.AppendLine();
                    break;

                case ParagraphBlock para:
                    RenderInlines(para.Inline, sb);
                    sb.AppendLine();
                    sb.AppendLine();
                    break;

                case FencedCodeBlock fenced:
                    sb.AppendLine("[code]");
                    sb.Append("[color=#aabbcc]");
                    foreach (var line in fenced.Lines.Lines)
                    {
                        var s = line.Slice.ToString();
                        if (s != null) sb.AppendLine(s);
                    }
                    sb.AppendLine("[/color]");
                    sb.AppendLine("[/code]");
                    sb.AppendLine();
                    break;

                case CodeBlock code:
                    sb.AppendLine("[code]");
                    foreach (var line in code.Lines.Lines)
                    {
                        var s = line.Slice.ToString();
                        if (s != null) sb.AppendLine(s);
                    }
                    sb.AppendLine("[/code]");
                    sb.AppendLine();
                    break;

                case ListBlock list:
                    foreach (var item in list)
                    {
                        if (item is ListItemBlock listItem)
                        {
                            sb.Append(list.IsOrdered ? "  • " : "  • ");
                            RenderBlocks(listItem, sb);
                        }
                    }
                    break;

                case ThematicBreakBlock:
                    sb.AppendLine("[color=#444455]────────────────────────────[/color]");
                    sb.AppendLine();
                    break;

                case QuoteBlock quote:
                    sb.Append("[color=#888899]▎ ");
                    RenderBlocks(quote, sb);
                    sb.AppendLine("[/color]");
                    break;

                default:
                    if (block is ContainerBlock cb)
                        RenderBlocks(cb, sb);
                    break;
            }
        }
    }

    private static void RenderInlines(ContainerInline? inlines, StringBuilder sb)
    {
        if (inlines == null) return;

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                case EmphasisInline emphasis:
                    var tag = emphasis.DelimiterCount == 2 ? "b" : "i";
                    sb.Append($"[{tag}]");
                    RenderInlines(emphasis, sb);
                    sb.Append($"[/{tag}]");
                    break;

                case CodeInline code:
                    sb.Append($"[code][color=#ddaa66]{code.Content}[/color][/code]");
                    break;

                case LinkInline link:
                    sb.Append($"[color=#6699dd][url={link.Url}]");
                    RenderInlines(link, sb);
                    sb.Append("[/url][/color]");
                    break;

                case LineBreakInline:
                    sb.AppendLine();
                    break;

                case ContainerInline container:
                    RenderInlines(container, sb);
                    break;
            }
        }
    }
}
