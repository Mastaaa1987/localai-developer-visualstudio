using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Newtonsoft.Json.Linq;

namespace LocalAI.Developer.VisualStudio
{
    internal sealed class PatchApprovalWindow : Window
    {
        private readonly ComboBox files = new ComboBox { Margin = new Thickness(8), MinWidth = 360 };
        private readonly RichTextBox diff = DiffBox();
        private readonly List<PatchFileView> views = new List<PatchFileView>();

        public event Action ApplyRequested;
        public event Action SkipRequested;
        public event Action CancelRequested;

        public PatchApprovalWindow(string stepTitle, string risk, JObject patch,
            string applyText = null, string skipText = null,
            string cancelText = null)
        {
            applyText = applyText ?? Localizer.Text("Apply");
            skipText = skipText ?? Localizer.Text("Skip");
            cancelText = cancelText ?? Localizer.Text("Cancel");
            Title = Localizer.Text("PatchApproval") + " · " + stepTitle;
            Width = 1280;
            Height = 780;
            MinWidth = 760;
            MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;

            foreach (JObject file in patch?["files"] as JArray ?? new JArray())
            {
                views.Add(new PatchFileView
                {
                    Path = (string)file["path"] ?? "",
                    Operation = (string)file["operation"] ?? "update",
                    Before = (string)file["before"] ?? "",
                    After = string.Equals((string)file["operation"], "delete",
                        StringComparison.OrdinalIgnoreCase) ? "" : (string)file["content"] ?? ""
                });
            }

            Content = BuildLayout(stepTitle, risk, applyText, skipText, cancelText);
            VisualStudioTheme.Apply(this);
            if (views.Count > 1) files.Items.Add(Localizer.Text("AllFiles") + " (" + views.Count + ")");
            foreach (PatchFileView view in views) files.Items.Add(view);
            files.SelectionChanged += delegate { ShowSelectedFile(); };
            if (files.Items.Count > 0) files.SelectedIndex = 0;
        }

        private UIElement BuildLayout(string stepTitle, string risk,
            string applyText, string skipText, string cancelText)
        {
            var root = new Grid { Margin = new Thickness(10) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new TextBlock
            {
                Text = stepTitle + "   ·   " + Localizer.Text("Risk") + ": " + risk.ToUpperInvariant(),
                Margin = new Thickness(8), FontSize = 16, FontWeight = FontWeights.SemiBold
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);
            Grid.SetRow(files, 1);
            root.Children.Add(files);

            var comparison = new Grid { Margin = new Thickness(8, 2, 8, 8) };
            comparison.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            comparison.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            comparison.Children.Add(Label(Localizer.Text("LineDiff"), 0));
            Grid.SetRow(diff, 1);
            comparison.Children.Add(diff);
            Grid.SetRow(comparison, 2);
            root.Children.Add(comparison);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            buttons.Children.Add(Button(cancelText, delegate { CloseAndRaise(CancelRequested); }));
            if (!string.IsNullOrWhiteSpace(skipText))
                buttons.Children.Add(Button(skipText, delegate { CloseAndRaise(SkipRequested); }));
            buttons.Children.Add(Button(applyText, delegate { CloseAndRaise(ApplyRequested); }, true));
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);
            return root;
        }

        private void ShowSelectedFile()
        {
            var selected = files.SelectedItem as PatchFileView;
            var document = new FlowDocument
            {
                FontFamily = new FontFamily("Consolas"), FontSize = 13,
                PagePadding = new Thickness(6), LineHeight = 18
            };
            VisualStudioTheme.Apply(document);
            IEnumerable<PatchFileView> selectedViews = selected != null
                ? new[] { selected }
                : files.SelectedItem is string ? views : Enumerable.Empty<PatchFileView>();
            foreach (PatchFileView view in selectedViews)
            {
                AddDiffLine(document, "@@ " + view.Path + "  (" + view.Operation + ")", 'h');
                foreach (DiffLine line in BuildDiff(view.Before, view.After))
                    AddDiffLine(document, line.Prefix + " " + line.Text, line.Prefix);
            }
            diff.Document = document;
        }

        private static void AddDiffLine(FlowDocument document, string text, char kind)
        {
            var paragraph = new Paragraph(new Run(text ?? ""))
            {
                Margin = new Thickness(0), Padding = new Thickness(4, 0, 4, 0)
            };
            if (kind == '+')
            {
                paragraph.Background = new SolidColorBrush(Color.FromRgb(32, 66, 42));
                paragraph.Foreground = new SolidColorBrush(Color.FromRgb(190, 245, 202));
            }
            else if (kind == '-')
            {
                paragraph.Background = new SolidColorBrush(Color.FromRgb(75, 35, 39));
                paragraph.Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 190));
            }
            else if (kind == 'h')
            {
                paragraph.Background = new SolidColorBrush(Color.FromRgb(42, 55, 78));
                paragraph.Foreground = Brushes.LightBlue;
                paragraph.FontWeight = FontWeights.SemiBold;
            }
            document.Blocks.Add(paragraph);
        }

        private static IEnumerable<DiffLine> BuildDiff(string before, string after)
        {
            string[] left = Lines(before);
            string[] right = Lines(after);
            if ((long)left.Length * right.Length > 4000000)
                return left.Select(line => new DiffLine('-', line))
                    .Concat(right.Select(line => new DiffLine('+', line))).ToList();
            var lengths = new int[left.Length + 1, right.Length + 1];
            for (int i = left.Length - 1; i >= 0; i--)
            for (int j = right.Length - 1; j >= 0; j--)
                lengths[i, j] = left[i] == right[j]
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            var result = new List<DiffLine>();
            int leftIndex = 0;
            int rightIndex = 0;
            while (leftIndex < left.Length || rightIndex < right.Length)
            {
                if (leftIndex < left.Length && rightIndex < right.Length &&
                    left[leftIndex] == right[rightIndex])
                {
                    result.Add(new DiffLine(' ', left[leftIndex++]));
                    rightIndex++;
                }
                else if (rightIndex < right.Length && (leftIndex == left.Length ||
                         lengths[leftIndex, rightIndex + 1] >= lengths[leftIndex + 1, rightIndex]))
                    result.Add(new DiffLine('+', right[rightIndex++]));
                else
                    result.Add(new DiffLine('-', left[leftIndex++]));
            }
            return result;
        }

        private static string[] Lines(string value) => (value ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private void CloseAndRaise(Action handler)
        {
            Close();
            handler?.Invoke();
        }

        private static TextBlock Label(string text, int column)
        {
            var label = new TextBlock
            {
                Text = text, Margin = new Thickness(4), FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(label, column);
            return label;
        }

        private static Button Button(string text, RoutedEventHandler click, bool primary = false)
        {
            var button = new Button
            {
                Content = text, Margin = new Thickness(5), Padding = new Thickness(18, 8, 18, 8),
                MinWidth = 110, IsDefault = primary
            };
            button.Click += click;
            return button;
        }

        private static RichTextBox DiffBox() => new RichTextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"), FontSize = 13,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            IsDocumentEnabled = true
        };

        private sealed class DiffLine
        {
            public readonly char Prefix;
            public readonly string Text;
            public DiffLine(char prefix, string text) { Prefix = prefix; Text = text; }
        }

        private sealed class PatchFileView
        {
            public string Path;
            public string Operation;
            public string Before;
            public string After;
            public override string ToString() => Operation.ToUpperInvariant() + "   " + Path;
        }
    }
}
