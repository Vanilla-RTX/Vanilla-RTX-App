using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Markdig;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MI = Markdig.Syntax.Inlines;
using MS = Markdig.Syntax;
using MT = Markdig.Extensions.Tables;
using TL = Markdig.Extensions.TaskLists;

namespace Vanilla_RTX_App.Core.Overlays;

/// <summary>Result of <see cref="MarkdownRenderer.Render"/> - the built visual tree, plus every heading's GitHub-style anchor slug mapped to the element it belongs to, so the host can scroll to a URL fragment or an in-document TOC link.</summary>
public sealed class MarkdownRenderResult
{
    public required UIElement Content { get; init; }
    public required IReadOnlyDictionary<string, FrameworkElement> Anchors { get; init; }
    public required IReadOnlyList<MarkdownSearchEntry> SearchEntries { get; init; }
}

/// <summary>One searchable unit of text (a heading, a paragraph, or a code block) and the element it renders as - see <see cref="MarkdownRenderer.SetSearchHighlight"/> for what the host can do with that element.</summary>
public sealed class MarkdownSearchEntry
{
    public required string Text { get; init; }
    public required FrameworkElement Element { get; init; }
}

/// <summary>
/// Turns a Markdig <see cref="MS.MarkdownDocument"/> into a plain WinUI visual tree - no HTML,
/// no WebView2, just <see cref="TextBlock"/>/<see cref="RichTextBlock"/>/<see cref="Grid"/> the
/// same way any other part of this app builds UI in code-behind (compare
/// <c>WebImportOverlay.AddDownloadRow</c>). Deliberately doesn't use Markdig's own HTML
/// renderer - there is no HTML surface to render into here, and hand-walking the AST is what
/// lets images, relative links and GitHub-style heading anchors resolve against the *source*
/// repository rather than against nothing.
///
/// <para><b>Only what shows up in a real GitHub README is covered</b>: headings, paragraphs,
/// bold/italic/strikethrough/code spans, links (including "badge" images that are themselves a
/// link), autolinks, lists (ordered/unordered/nested/task lists), fenced and indented code
/// blocks, block quotes, thematic breaks, and pipe tables. Raw HTML blocks/inlines are skipped
/// outright rather than guessed at - most README HTML is layout-only (centering, `&lt;br&gt;`
/// spacing) that has no honest WinUI equivalent, and guessing wrong reads worse than
/// omitting.</para>
///
/// <para><b>The InlineUIContainer trap.</b> WinUI only allows an <see cref="InlineUIContainer"/>
/// (needed for a real, decoded <see cref="Image"/>) as a *direct* child of a
/// <see cref="Paragraph"/>'s <see cref="InlineCollection"/> - nested one level inside a
/// <see cref="Span"/>/<see cref="Bold"/>/<see cref="Italic"/>/<see cref="Hyperlink"/> and it
/// throws at the point the child is added. Every inline-building method therefore threads an
/// <c>allowInlineUI</c> flag that goes false the moment recursion enters a nested span, so an
/// image inside emphasis degrades to its alt text instead of crashing the whole render. The one
/// case worth doing properly rather than degrading is a *linked* image - `[![badge](img)](url)`,
/// extremely common in READMEs - which is detected before generic dispatch and rendered as one
/// top-level, tappable <see cref="InlineUIContainer"/> instead of a Hyperlink wrapping one.</para>
///
/// <para><b>Anchor slugs are best-effort, not a reimplementation of GitHub's slugger.</b>
/// <see cref="ToGithubAnchor"/> lowercases, keeps letters/digits/hyphen/underscore/space, drops
/// everything else (punctuation, emoji), and turns spaces into hyphens. That alone matches
/// GitHub exactly for every heading observed in this app's own two linked documents (plain
/// "Documentation" -> "documentation", emoji-prefixed "❌ Unresolved" -> "-unresolved" via the
/// dropped emoji leaving a lone leading space). It will drift from GitHub's real algorithm on
/// headings with repeated punctuation runs or non-Latin scripts; the fallback for a miss is
/// simply "the initial fragment doesn't auto-scroll", never a crash.</para>
///
/// <para><b>Search entries exist for in-document find, and every heading/paragraph is wrapped in
/// a bare <see cref="Border"/> only because of it.</b> <see cref="RichTextBlock"/> has no
/// <c>Background</c> property, so there is nothing <see cref="SetSearchHighlight"/> could paint
/// to mark "this is the current match" without that wrapper. The wrapper carries no border,
/// padding or background of its own - it is invisible until highlighted. Only headings, paragraphs and
/// code blocks are indexed; a match inside a list item or table cell is still found because those
/// containers render their content through the same two methods recursively.</para>
///
/// <para><b>Every theme colour on an element comes from a style in App.xaml</b>
/// (<c>Themed*Style</c>), and on a text run from <see cref="ThemeService.AccentTextBrush"/> - never
/// from <c>Application.Current.Resources</c>, which answers for Windows' theme rather than the
/// app's and would paint a light-themed app's documents in dark-theme colours.</para>
///
/// <para><b>Never throws.</b> <see cref="Render"/> wraps the whole structured pass in one
/// try/catch; a parse or layout surprise anywhere degrades to a monospace dump of the raw
/// markdown text rather than an error the caller has to handle.</para>
/// </summary>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>
    /// Inline formatting only, for short texts that were written as plain text first: the
    /// announcement Teletext. Every block parser but the paragraph is removed, so a line starting
    /// "- " or "1. " stays the literal text it has always been rather than turning into a list,
    /// and "---" under a line doesn't become a heading. Raw HTML is off, so a word in angle
    /// brackets is text rather than a tag that vanishes. What is left is emphasis, strikethrough,
    /// code spans, links and bare-URL autolinks - and CommonMark's flanking rules, which are what
    /// keep an intraword underscore like <c>terrain_texture.json</c> from ever turning italic.
    /// </summary>
    private static readonly MarkdownPipeline InlinePipeline = BuildInlinePipeline();

    private static MarkdownPipeline BuildInlinePipeline()
    {
        // DisableHtml first: it removes HtmlBlockParser itself and would find nothing to remove
        // once the block parsers below are gone.
        var builder = new MarkdownPipelineBuilder().UseEmphasisExtras().UseAutoLinks().DisableHtml();
        builder.BlockParsers.RemoveAll(p => p is not Markdig.Parsers.ParagraphBlockParser);
        builder.BlockParsers.Find<Markdig.Parsers.ParagraphBlockParser>()!.ParseSetexHeadings = false;
        return builder.Build();
    }

    private readonly string _blobBaseUrl;
    private readonly string _rawBaseUrl;
    private readonly Action<string> _openLink;
    private readonly bool _softBreaksAreHard;
    private readonly Dictionary<string, FrameworkElement> _anchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _usedAnchorSlugs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MarkdownSearchEntry> _searchEntries = new();

    private MarkdownRenderer(string blobBaseUrl, string rawBaseUrl, Action<string> openLink, bool softBreaksAreHard = false)
    {
        _blobBaseUrl = blobBaseUrl;
        _rawBaseUrl = rawBaseUrl;
        _openLink = openLink;
        _softBreaksAreHard = softBreaksAreHard;
    }

    /// <summary>
    /// Renders <paramref name="markdown"/>'s inline formatting into <paramref name="target"/>,
    /// through <see cref="InlinePipeline"/>. Links hand their URL to <paramref name="openLink"/>
    /// unresolved - there is no source repository to resolve a relative one against.
    ///
    /// <para><b>Every newline is kept.</b> Markdown folds a single newline into a space, and
    /// every Teletext published so far was written expecting it to be a line break, because it always
    /// was one. A blank line is a paragraph break and comes out as the same empty line it always
    /// did; only a run of several blank lines collapses to one.</para>
    ///
    /// <para>Images degrade to their "[alt]" text: nothing here may hold an
    /// <see cref="InlineUIContainer"/>. Never throws - a failure renders the text verbatim.</para>
    /// </summary>
    public static void RenderInlines(InlineCollection target, string markdown, Action<string> openLink)
    {
        try
        {
            var renderer = new MarkdownRenderer(string.Empty, string.Empty, openLink, softBreaksAreHard: true);
            var first = true;
            foreach (var block in Markdig.Markdown.Parse(markdown, InlinePipeline))
            {
                if (block is not MS.ParagraphBlock para) continue;
                if (!first)
                {
                    target.Add(new LineBreak());
                    target.Add(new LineBreak());
                }
                first = false;
                renderer.AppendInlines(target, para.Inline, allowInlineUI: false);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MarkdownRenderer] Inline render failed, falling back to plain text: {ex.Message}");
            target.Clear();
            target.Add(new Run { Text = markdown });
        }
    }

    /// <summary>
    /// <paramref name="markdown"/> as <see cref="RenderInlines"/> would show it, minus the
    /// formatting, for a surface that can only hold plain text (the sidebar log). A link keeps
    /// its address as "label (url)" - the only way it can still be followed there - unless the
    /// label already is the address, as with a bare-URL autolink.
    /// </summary>
    public static string ToPlainText(string markdown)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var block in Markdig.Markdown.Parse(markdown, InlinePipeline))
            {
                if (block is not MS.ParagraphBlock { Inline: { } inline }) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                AppendTeletextPlainText(sb, inline);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MarkdownRenderer] Plain-text conversion failed, using the raw text: {ex.Message}");
            return markdown;
        }
    }

    private static void AppendTeletextPlainText(StringBuilder sb, MI.ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case MI.LinkInline link:
                    var label = GetPlainText(link);
                    sb.Append(label);
                    if (!string.IsNullOrEmpty(link.Url) && label != link.Url)
                        sb.Append(" (").Append(link.Url).Append(')');
                    break;
                case MI.LineBreakInline: sb.Append('\n'); break;
                case MI.LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case MI.CodeInline code: sb.Append(code.Content); break;
                case MI.HtmlEntityInline entity: sb.Append(entity.Transcoded.ToString()); break;
                case MI.AutolinkInline autolink: sb.Append(autolink.Url); break;
                case MI.ContainerInline nested: AppendTeletextPlainText(sb, nested); break;
            }
        }
    }

    /// <summary>
    /// Renders <paramref name="markdown"/> into a visual tree. <paramref name="blobBaseUrl"/> and
    /// <paramref name="rawBaseUrl"/> are the directory-level URLs (trailing slash) that relative
    /// links and relative image sources resolve against, respectively - see
    /// <c>MarkdownOverlay.ResolveGithubUrls</c> for how those are derived from the page URL a
    /// caller passes to <c>MarkdownOverlay.Show</c>. <paramref name="openLink"/> is called with
    /// either an absolute URL (open externally) or a bare <c>#slug</c> (scroll within this
    /// document) whenever the user activates a link or a tappable image.
    /// </summary>
    public static MarkdownRenderResult Render(string markdown, string blobBaseUrl, string rawBaseUrl, Action<string> openLink)
    {
        var renderer = new MarkdownRenderer(blobBaseUrl, rawBaseUrl, openLink);
        try
        {
            var document = Markdig.Markdown.Parse(markdown, Pipeline);
            var panel = renderer.RenderDocument(document);
            return new MarkdownRenderResult { Content = panel, Anchors = renderer._anchors, SearchEntries = renderer._searchEntries };
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[MarkdownRenderer] Structured render failed, falling back to plain text: {ex}");
            var fallback = new TextBlock
            {
                Text = markdown,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13
            };
            return new MarkdownRenderResult
            {
                Content = fallback,
                Anchors = new Dictionary<string, FrameworkElement>(),
                // The structured pass never ran, so there's nothing to index - search just finds
                // nothing against a raw-text fallback rather than crashing.
                SearchEntries = Array.Empty<MarkdownSearchEntry>()
            };
        }
    }

    /// <summary>
    /// Toggles the "this is the current search match" highlight on an element from a
    /// <see cref="MarkdownSearchEntry"/>. Every search entry is a <see cref="Border"/>: the bare
    /// wrapper around a heading or paragraph, or a code block's own card.
    ///
    /// <para><b>Un-highlighting clears the local value rather than assigning one</b>, and that
    /// covers both kinds: a wrapper has no background at all, and a code block's comes from its
    /// style, which a local value only hides. Putting a brush back instead would pin whichever
    /// theme's card colour was current at the time.</para>
    /// </summary>
    public static void SetSearchHighlight(FrameworkElement element, bool highlighted)
    {
        if (element is not Border border) return;

        if (highlighted) border.Background = HighlightBrush();
        else border.ClearValue(Border.BackgroundProperty);
    }

    private static Style AppStyle(string key) => (Style)Application.Current.Resources[key];

    private static Brush HighlightBrush() => new SolidColorBrush
    {
        Color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"],
        Opacity = 0.35
    };

    // =========================================================================
    // Blocks
    // =========================================================================

    private Panel RenderDocument(MS.MarkdownDocument document)
    {
        var root = new StackPanel { Spacing = 14 };
        foreach (var block in document)
            RenderBlockInto(root, block);
        return root;
    }

    private void RenderBlockInto(Panel parent, MS.Block block)
    {
        switch (block)
        {
            case MS.HeadingBlock heading:
                parent.Children.Add(RenderHeading(heading));
                break;

            case MT.Table table:
                parent.Children.Add(RenderTable(table));
                break;

            case MS.QuoteBlock quote:
                parent.Children.Add(RenderQuote(quote));
                break;

            case MS.ListBlock list:
                parent.Children.Add(RenderList(list));
                break;

            case MS.FencedCodeBlock fenced:
                parent.Children.Add(RenderCodeBlock(fenced));
                break;

            case MS.CodeBlock code:
                parent.Children.Add(RenderCodeBlock(code));
                break;

            case MS.ThematicBreakBlock:
                parent.Children.Add(RenderThematicBreak());
                break;

            case MS.ParagraphBlock para:
                parent.Children.Add(RenderParagraph(para));
                break;

            case MS.HtmlBlock:
                // Raw HTML - see the class doc. Skipped rather than guessed at.
                break;

            // Any other container (including one from an extension this renderer doesn't know
            // about) - render its children rather than dropping the whole thing on the floor.
            case MS.ContainerBlock container:
                foreach (var child in container)
                    RenderBlockInto(parent, child);
                break;
        }
    }

    private FrameworkElement RenderHeading(MS.HeadingBlock heading)
    {
        var (fontSize, weight) = heading.Level switch
        {
            1 => (26.0, FontWeights.Bold),
            2 => (22.0, FontWeights.Bold),
            3 => (18.0, FontWeights.SemiBold),
            4 => (16.0, FontWeights.SemiBold),
            5 => (14.0, FontWeights.SemiBold),
            _ => (13.0, FontWeights.SemiBold),
        };

        var rtb = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
        var paragraph = new Paragraph { FontSize = fontSize, FontWeight = weight };
        AppendInlines(paragraph.Inlines, heading.Inline);
        rtb.Blocks.Add(paragraph);

        // RichTextBlock has no Background property to toggle for a search highlight, so every
        // heading/paragraph is wrapped in a bare Border purely to give SetSearchHighlight
        // something to paint - Tag=null is what it restores to when the highlight clears.
        var searchTarget = new Border { Child = rtb, Tag = null };

        FrameworkElement result = searchTarget;
        if (heading.Level <= 2)
        {
            // GitHub underlines H1/H2 - a thin divider is the closest honest equivalent without
            // a border-bottom concept on a RichTextBlock.
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(searchTarget);
            stack.Children.Add(new Border
            {
                Height = 1,
                Style = AppStyle("ThemedDividerRuleStyle")
            });
            result = stack;
        }
        result.Margin = new Thickness(0, heading.Level == 1 ? 6 : 2, 0, 0);

        var plainText = GetPlainText(heading.Inline);
        var slug = ToGithubAnchor(plainText);
        if (!string.IsNullOrEmpty(slug))
            _anchors[MakeUniqueSlug(slug)] = result;

        if (!string.IsNullOrWhiteSpace(plainText))
            _searchEntries.Add(new MarkdownSearchEntry { Text = plainText, Element = searchTarget });

        return result;
    }

    private FrameworkElement RenderParagraph(MS.ParagraphBlock para, bool skipLeadingTaskInline = false)
    {
        var rtb = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var p = new Paragraph();

        if (skipLeadingTaskInline && para.Inline?.FirstChild is TL.TaskList)
        {
            foreach (var inline in para.Inline.Skip(1))
                AppendInline(p.Inlines, inline, allowInlineUI: true);
        }
        else
        {
            AppendInlines(p.Inlines, para.Inline);
        }

        // A paragraph that is nothing but images - a cover shot, a screenshot, a row of badges -
        // is centered. An image sharing a line with text is left inline where the text put it.
        if (IsImageOnly(para.Inline))
            p.TextAlignment = TextAlignment.Center;

        rtb.Blocks.Add(p);

        // See RenderHeading - the Border exists only so a search highlight has a Background to set.
        var searchTarget = new Border { Child = rtb, Tag = null };

        var plainText = GetPlainText(para.Inline);
        if (!string.IsNullOrWhiteSpace(plainText))
            _searchEntries.Add(new MarkdownSearchEntry { Text = plainText, Element = searchTarget });

        return searchTarget;
    }

    private FrameworkElement RenderList(MS.ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list)
        {
            if (item is not MS.ListItemBlock listItem) continue;
            panel.Children.Add(RenderListItem(listItem, list.IsOrdered, index));
            index++;
        }

        return panel;
    }

    private FrameworkElement RenderListItem(MS.ListItemBlock item, bool ordered, int index)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Markdig's task-list extension turns a leading "[ ] "/"[x] " in the item's first
        // paragraph into a TaskList inline rather than a separate block - the only way to find
        // it is to look at that paragraph's first inline.
        var firstBlock = item.FirstOrDefault();
        var isTask = firstBlock is MS.ParagraphBlock { Inline.FirstChild: TL.TaskList } ;
        var isChecked = isTask && ((TL.TaskList)((MS.ParagraphBlock)firstBlock!).Inline!.FirstChild!).Checked;

        FrameworkElement marker = isTask
            ? new CheckBox { IsChecked = isChecked, IsEnabled = false, Padding = new Thickness(0), Margin = new Thickness(0, 1, 0, 0) }
            : new TextBlock { Text = ordered ? $"{index}." : "•", FontSize = 14, Margin = new Thickness(0, 1, 0, 0) };
        Grid.SetColumn(marker, 0);
        row.Children.Add(marker);

        var content = new StackPanel { Spacing = 6 };
        Grid.SetColumn(content, 1);
        foreach (var block in item)
        {
            if (isTask && ReferenceEquals(block, firstBlock))
            {
                content.Children.Add(RenderParagraph((MS.ParagraphBlock)block, skipLeadingTaskInline: true));
                continue;
            }
            RenderBlockInto(content, block);
        }
        row.Children.Add(content);

        return row;
    }

    private FrameworkElement RenderQuote(MS.QuoteBlock quote)
    {
        var content = new StackPanel { Spacing = 8 };
        foreach (var block in quote)
            RenderBlockInto(content, block);

        return new Border
        {
            Style = AppStyle("ThemedAccentEdgeStyle"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 4, 10, 4),
            Opacity = 0.85,
            Child = content
        };
    }

    private FrameworkElement RenderCodeBlock(MS.CodeBlock code)
    {
        var text = code.Lines.ToString();
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true
        };

        var border = new Border
        {
            // Background comes from the style so SetSearchHighlight can clear back to it.
            Style = AppStyle("ThemedCardStyle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = textBlock
            }
        };

        if (!string.IsNullOrWhiteSpace(text))
            _searchEntries.Add(new MarkdownSearchEntry { Text = text, Element = border });

        return border;
    }

    private static FrameworkElement RenderThematicBreak() => new Border
    {
        Height = 1,
        Style = AppStyle("ThemedDividerRuleStyle"),
        Margin = new Thickness(0, 4, 0, 4)
    };

    private FrameworkElement RenderTable(MT.Table table)
    {
        var columnCount = table.ColumnDefinitions.Count;
        if (columnCount == 0 && table.FirstOrDefault() is MT.TableRow firstRow)
            columnCount = firstRow.Count;
        columnCount = Math.Max(columnCount, 1);

        var grid = new Grid();
        foreach (var width in ResolveColumnWidths(table, columnCount))
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });

        var rowIndex = 0;
        foreach (var rowBlock in table)
        {
            if (rowBlock is not MT.TableRow row) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var colIndex = 0;
            foreach (var cellBlock in row)
            {
                if (cellBlock is not MT.TableCell cell)
                {
                    colIndex++;
                    continue;
                }

                var cellContent = new StackPanel { Spacing = 4 };
                foreach (var block in cell)
                    RenderBlockInto(cellContent, block);

                if (row.IsHeader)
                {
                    foreach (var rtb in cellContent.Children.OfType<RichTextBlock>())
                        foreach (var p in rtb.Blocks.OfType<Paragraph>())
                            p.FontWeight = FontWeights.SemiBold;
                }

                if (colIndex < table.ColumnDefinitions.Count)
                {
                    cellContent.HorizontalAlignment = table.ColumnDefinitions[colIndex].Alignment switch
                    {
                        MT.TableColumnAlign.Center => HorizontalAlignment.Center,
                        MT.TableColumnAlign.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Left
                    };
                }

                var border = new Border
                {
                    Style = AppStyle(row.IsHeader ? "ThemedCardStyle" : "ThemedCardOutlineStyle"),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(10, 6, 10, 6),
                    Child = cellContent
                };
                Grid.SetRow(border, rowIndex);
                Grid.SetColumn(border, colIndex);
                var span = Math.Max(1, cell.ColumnSpan);
                if (span > 1) Grid.SetColumnSpan(border, span);
                grid.Children.Add(border);

                colIndex += span;
            }
            rowIndex++;
        }

        return new Border
        {
            Style = AppStyle("ThemedCardOutlineStyle"),
            BorderThickness = new Thickness(1, 1, 0, 0),
            CornerRadius = new CornerRadius(4),
            // A table with nothing to wrap hugs its content. Stretched, the outer border's top
            // edge would run on past the last column, since the cells draw the other edges.
            HorizontalAlignment = grid.ColumnDefinitions.Any(c => c.Width.IsStar)
                ? HorizontalAlignment.Stretch
                : HorizontalAlignment.Left,
            Child = grid
        };
    }

    /// <summary>Longest cell, in characters, that a column may hold and still be sized to its content rather than wrapped.</summary>
    private const int TableAutoColumnMaxChars = 32;

    /// <summary>
    /// How wide each column is: short columns take exactly their content's width, and long
    /// ones share what is left in proportion to how much text they hold, wrapping inside it -
    /// roughly what GitHub does with the same table.
    ///
    /// <para><b>A table never scrolls sideways, deliberately.</b> Every column used to be sized
    /// to its content inside a horizontal ScrollViewer, so a column of sentences ran far off
    /// the edge and had to be scrolled to be read. That nested ScrollViewer also caused two bugs
    /// of its own: its scrollbar draws over content rather than beside it, covering the last
    /// row, and it swallowed mouse-wheel input it had no vertical room to use, so the page
    /// stopped scrolling whenever the pointer crossed a table. Wrapping removes the need for it,
    /// and both bugs with it.</para>
    ///
    /// <para>A table whose short columns alone are wider than the page is clipped rather than
    /// scrollable - a trade taken knowingly, since documentation tables are a few narrow label
    /// columns and at most a couple of prose ones.</para>
    /// </summary>
    private static List<GridLength> ResolveColumnWidths(MT.Table table, int columnCount)
    {
        var longest = new int[columnCount];
        foreach (var rowBlock in table)
        {
            if (rowBlock is not MT.TableRow row) continue;
            var col = 0;
            foreach (var cellBlock in row)
            {
                if (col >= columnCount) break;
                if (cellBlock is MT.TableCell cell && Math.Max(1, cell.ColumnSpan) == 1)
                {
                    var length = cell.OfType<MS.ParagraphBlock>().Sum(p => GetPlainText(p.Inline).Length);
                    longest[col] = Math.Max(longest[col], length);
                }
                col += cellBlock is MT.TableCell c ? Math.Max(1, c.ColumnSpan) : 1;
            }
        }

        return longest
            .Select(chars => chars <= TableAutoColumnMaxChars
                ? GridLength.Auto
                : new GridLength(chars, GridUnitType.Star))
            .ToList();
    }

    // =========================================================================
    // Inlines
    // =========================================================================

    private void AppendInlines(InlineCollection target, MI.ContainerInline? container, bool allowInlineUI = true)
    {
        if (container is null) return;
        foreach (var inline in container)
            AppendInline(target, inline, allowInlineUI);
    }

    private void AppendInline(InlineCollection target, MI.Inline inline, bool allowInlineUI)
    {
        switch (inline)
        {
            case MI.LiteralInline lit:
                target.Add(new Run { Text = lit.Content.ToString() });
                break;

            case MI.CodeInline code:
                // A styled Run rather than an InlineUIContainer chip - inline code shows up
                // inside emphasis and table cells constantly, and only a Run is legal there.
                target.Add(new Run
                {
                    Text = code.Content,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = ThemeService.AccentTextBrush
                });
                break;

            case TL.TaskList task:
                // Reached only if a task marker shows up outside a list item's first paragraph
                // (RenderListItem intercepts the normal case and draws a real CheckBox instead).
                target.Add(new Run { Text = task.Checked ? "☑ " : "☐ " });
                break;

            case MI.EmphasisInline emphasis:
                AppendEmphasis(target, emphasis);
                break;

            case MI.LinkInline { IsImage: true } image:
                target.Add(allowInlineUI ? BuildInlineImage(image) : BuildImageFallbackRun(image));
                break;

            case MI.LinkInline link when !link.IsImage && allowInlineUI && TryGetSoleImageChild(link, out var soleImage):
                target.Add(BuildClickableImage(soleImage, ResolveLink(link.Url ?? string.Empty)));
                break;

            case MI.LinkInline link:
                target.Add(BuildHyperlink(link));
                break;

            case MI.AutolinkInline autolink:
                target.Add(BuildAutolink(autolink));
                break;

            case MI.LineBreakInline lineBreak:
                target.Add(lineBreak.IsHard || _softBreaksAreHard ? new LineBreak() : new Run { Text = " " });
                break;

            case MI.HtmlEntityInline entity:
                target.Add(new Run { Text = entity.Transcoded.ToString() });
                break;

            case MI.HtmlInline:
                // Raw inline HTML - same call as HtmlBlock above, skipped rather than guessed at.
                break;

            case MI.ContainerInline nested:
                foreach (var child in nested)
                    AppendInline(target, child, allowInlineUI);
                break;
        }
    }

    /// <summary>
    /// The face italic text is drawn in. The app's text font, Segoe UI Variable, ships no italic,
    /// so asking it for one gets DirectWrite's synthetic oblique - a steep skew of the upright
    /// glyphs that reads far heavier than italic should. Classic Segoe UI has a real italic face
    /// with the same proportions, so emphasis switches to it.
    ///
    /// <para>Built per renderer, on first use, never in a static initializer: a FontFamily is a
    /// XAML object, and the class's statics also run when <see cref="ToPlainText"/> is reached
    /// first from a background thread. A throw there fails the whole type, and every later
    /// render falls back to raw markdown.</para>
    /// </summary>
    private FontFamily ItalicFontFamily => _italicFontFamily ??= new FontFamily("Segoe UI");
    private FontFamily? _italicFontFamily;

    private void AppendEmphasis(InlineCollection target, MI.EmphasisInline emphasis)
    {
        Span span = emphasis.DelimiterChar == '~'
            ? new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough }
            : emphasis.DelimiterCount >= 2 ? new Bold() : new Italic { FontFamily = ItalicFontFamily };

        // Nested content can never itself hold an InlineUIContainer - see the class doc.
        AppendInlines(span.Inlines, emphasis, allowInlineUI: false);
        target.Add(span);
    }

    private Inline BuildHyperlink(MI.LinkInline link)
    {
        var resolvedUrl = ResolveLink(link.Url ?? string.Empty);
        var hyperlink = new Hyperlink();
        if (!string.IsNullOrEmpty(link.Title))
            ToolTipService.SetToolTip(hyperlink, link.Title);

        AppendInlines(hyperlink.Inlines, link, allowInlineUI: false);
        if (hyperlink.Inlines.Count == 0)
            hyperlink.Inlines.Add(new Run { Text = resolvedUrl });

        hyperlink.Click += (_, _) => _openLink(resolvedUrl);
        return hyperlink;
    }

    private Inline BuildAutolink(MI.AutolinkInline autolink)
    {
        var url = autolink.IsEmail ? $"mailto:{autolink.Url}" : ResolveLink(autolink.Url);
        var hyperlink = new Hyperlink();
        hyperlink.Inlines.Add(new Run { Text = autolink.Url });
        hyperlink.Click += (_, _) => _openLink(url);
        return hyperlink;
    }

    /// <summary>
    /// True when <paramref name="container"/> holds at least one image and nothing else but
    /// line breaks and whitespace. A linked image (<c>[![alt](img)](url)</c>) counts as an image.
    /// </summary>
    private static bool IsImageOnly(MI.ContainerInline? container)
    {
        if (container is null) return false;

        var sawImage = false;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case MI.LinkInline { IsImage: true }:
                    sawImage = true;
                    break;
                case MI.LinkInline link when TryGetSoleImageChild(link, out _):
                    sawImage = true;
                    break;
                case MI.LineBreakInline:
                    break;
                case MI.LiteralInline lit when lit.Content.IsEmptyOrWhitespace():
                    break;
                default:
                    return false;
            }
        }
        return sawImage;
    }

    private static bool TryGetSoleImageChild(MI.LinkInline link, out MI.LinkInline image)
    {
        if (link.FirstChild is MI.LinkInline { IsImage: true } img && ReferenceEquals(link.FirstChild, link.LastChild))
        {
            image = img;
            return true;
        }
        image = null!;
        return false;
    }

    /// <summary>A badge-style "linked image" - `[![alt](img)](url)`. Rendered as one top-level, tappable InlineUIContainer rather than a Hyperlink wrapping an image, which WinUI doesn't allow (see the class doc).</summary>
    private Inline BuildClickableImage(MI.LinkInline image, string linkUrl)
    {
        var built = BuildInlineImage(image);
        if (built is InlineUIContainer { Child: UIElement el })
        {
            el.PointerPressed += (_, _) => _openLink(linkUrl);
            ToolTipService.SetToolTip(el, linkUrl);
        }
        return built;
    }

    private const int ImageDecodeHeight = 800;

    private Inline BuildInlineImage(MI.LinkInline image)
    {
        var altText = GetPlainText(image);
        var src = ResolveImage(image.Url ?? string.Empty);

        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri))
            return BuildImageFallbackRun(image);

        var host = new Grid();
        var img = new Image
        {
            MaxHeight = 400,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTipService.SetToolTip(img, altText);

        img.ImageFailed += (_, _) =>
        {
            host.Children.Clear();
            host.Children.Add(BuildBrokenImageVisual(altText));
        };

        host.Children.Add(img);

        // Height is what bounds these on screen (MaxHeight above, Stretch.Uniform), so it is
        // what bounds the decode: 2x that for 200% scale. The source is a remote URL of
        // unknown size and a decoded image costs width x height x 4 bytes of graphics memory
        // whatever it weighs on the wire. DecodePixelHeight is ignored unless it is set before
        // UriSource.
        var bitmap = new BitmapImage { DecodePixelHeight = ImageDecodeHeight };
        bitmap.UriSource = uri;
        img.Source = bitmap;

        return new InlineUIContainer { Child = host };
    }

    private static FrameworkElement BuildBrokenImageVisual(string altText) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Opacity = 0.6,
        Children =
        {
            new FontIcon { Glyph = "", FontSize = 14 },
            new TextBlock
            {
                Text = string.IsNullOrEmpty(altText) ? "Image failed to load" : altText,
                FontSize = 12,
                FontFamily = new FontFamily("Segoe UI"),
                FontStyle = Windows.UI.Text.FontStyle.Italic
            }
        }
    };

    private Run BuildImageFallbackRun(MI.LinkInline image)
    {
        var alt = GetPlainText(image);
        return new Run { Text = string.IsNullOrEmpty(alt) ? "[image]" : $"[{alt}]" };
    }

    // =========================================================================
    // URL resolution
    // =========================================================================

    private string ResolveLink(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return string.Empty;
        if (href.StartsWith('#')) return href;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(_blobBaseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, href, out var resolved))
            return resolved.ToString();
        return href;
    }

    private string ResolveImage(string src)
    {
        if (string.IsNullOrWhiteSpace(src)) return string.Empty;
        if (Uri.TryCreate(src, UriKind.Absolute, out var abs)) return abs.ToString();
        if (Uri.TryCreate(_rawBaseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, src, out var resolved))
            return resolved.ToString();
        return src;
    }

    // =========================================================================
    // Plain-text extraction (alt text, heading anchors)
    // =========================================================================

    private static string GetPlainText(MI.ContainerInline? container)
    {
        if (container is null) return string.Empty;
        var sb = new StringBuilder();
        AppendPlainText(sb, container);
        return sb.ToString();
    }

    private static void AppendPlainText(StringBuilder sb, MI.ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case MI.LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case MI.CodeInline code: sb.Append(code.Content); break;
                case MI.HtmlEntityInline entity: sb.Append(entity.Transcoded.ToString()); break;
                case MI.LineBreakInline: sb.Append(' '); break;
                case MI.AutolinkInline autolink: sb.Append(autolink.Url); break;
                case MI.ContainerInline nested: AppendPlainText(sb, nested); break;
            }
        }
    }

    /// <summary>Best-effort GitHub heading-anchor slug - see the class doc for exactly how close this gets.</summary>
    private static string ToGithubAnchor(string headingText)
    {
        var kept = new StringBuilder(headingText.Length);
        foreach (var ch in headingText)
        {
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')
                kept.Append(char.ToLowerInvariant(ch));
        }
        return kept.ToString().Replace(' ', '-');
    }

    /// <summary>GitHub disambiguates repeated identical headings with a -1, -2… suffix; matched here so two "Notes" sections don't silently collide onto one anchor.</summary>
    private string MakeUniqueSlug(string baseSlug)
    {
        if (_usedAnchorSlugs.Add(baseSlug)) return baseSlug;
        var i = 1;
        while (!_usedAnchorSlugs.Add($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }
}
