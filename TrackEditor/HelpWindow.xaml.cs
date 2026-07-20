using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TrackEditor;

/// <summary>
/// Shows the embedded user guide (the repo README) rendered from Markdown into a FlowDocument,
/// so the in-app help and the README stay a single source.
/// </summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Viewer.Document = BuildDocument(LoadGuide());
    }

    // The window is non-modal (Show, not ShowDialog), so IsCancel can't set DialogResult — close explicitly.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) { Close(); e.Handled = true; }
    }

    private static string LoadGuide()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("TrackEditor.UserGuide.md");
        if (stream is null) return "# User guide\n\nThe guide resource could not be loaded.";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---- a small, forgiving Markdown -> FlowDocument renderer (headings, lists, rules, inline) ----

    private static FlowDocument BuildDocument(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            PagePadding = new Thickness(28, 20, 28, 24),
            Background = Brushes.White,
            ColumnWidth = double.PositiveInfinity, // single column
        };

        var para = new StringBuilder(); // accumulates lines of the current paragraph

        void FlushParagraph()
        {
            if (para.Length == 0) return;
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineHeight = 20 };
            AddInline(p, para.ToString());
            doc.Blocks.Add(p);
            para.Clear();
        }

        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            // Drop image markdown — the README's screenshots live on disk (not embedded), so the
            // in-app guide stays text-only. An image on its own line then reads as a blank line.
            string line = Regex.Replace(raw, @"!\[[^\]]*\]\([^)]*\)", "").TrimEnd();

            if (line.Length == 0) { FlushParagraph(); continue; }

            // Horizontal rule
            if (line == "---")
            {
                FlushParagraph();
                doc.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 6, 0, 12),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                });
                continue;
            }

            // Headings
            if (line.StartsWith("### "))
            {
                FlushParagraph();
                doc.Blocks.Add(Heading(line[4..], 15, FontWeights.SemiBold, 14, 4));
                continue;
            }
            if (line.StartsWith("## "))
            {
                FlushParagraph();
                doc.Blocks.Add(Heading(line[3..], 18, FontWeights.Bold, 18, 6));
                continue;
            }
            if (line.StartsWith("# "))
            {
                FlushParagraph();
                doc.Blocks.Add(Heading(line[2..], 24, FontWeights.Bold, 6, 8));
                continue;
            }

            // Fenced code block delimiters — skip the fence lines, keep contents as body text
            if (line.StartsWith("```")) { FlushParagraph(); continue; }

            // Bullet / numbered list item
            var listMatch = Regex.Match(line, @"^(\s*)([-*]|\d+\.)\s+(.*)$");
            if (listMatch.Success)
            {
                FlushParagraph();
                int indent = listMatch.Groups[1].Value.Length;
                string marker = listMatch.Groups[2].Value == "-" || listMatch.Groups[2].Value == "*"
                    ? "•" : listMatch.Groups[2].Value;
                var item = new Paragraph
                {
                    Margin = new Thickness(18 + indent * 14, 0, 0, 5),
                    TextIndent = -14,
                    LineHeight = 20,
                };
                item.Inlines.Add(new Run(marker + "  "));
                AddInline(item, listMatch.Groups[3].Value);
                doc.Blocks.Add(item);
                continue;
            }

            // Otherwise: part of a paragraph (join wrapped lines with a space)
            if (para.Length > 0) para.Append(' ');
            para.Append(line);
        }
        FlushParagraph();
        return doc;
    }

    private static Paragraph Heading(string text, double size, FontWeight weight, double top, double bottom)
    {
        var p = new Paragraph { FontSize = size, FontWeight = weight, Margin = new Thickness(0, top, 0, bottom) };
        AddInline(p, text);
        return p;
    }

    /// <summary>Parses **bold** and `code` spans within a line into styled Runs.</summary>
    private static void AddInline(Paragraph target, string text)
    {
        // Split on **bold** and `code`, keeping the delimiters via capture groups.
        foreach (string token in Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)"))
        {
            if (token.Length == 0) continue;
            if (token.Length >= 4 && token.StartsWith("**") && token.EndsWith("**"))
            {
                target.Inlines.Add(new Bold(new Run(token[2..^2])));
            }
            else if (token.Length >= 2 && token[0] == '`' && token[^1] == '`')
            {
                target.Inlines.Add(new Run(token[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF2)),
                });
            }
            else
            {
                target.Inlines.Add(new Run(token));
            }
        }
    }
}
