using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace LocalAI.Developer.VisualStudio
{
    internal static class VisualStudioTheme
    {
        public static void Apply(FrameworkElement root)
        {
            if (root == null) return;

            SetForeground(root);
            if (root is Window || root is UserControl || root is TextBoxBase ||
                root is PasswordBox)
                root.SetValue(Control.BackgroundProperty,
                    ThemeBrush(EnvironmentColors.ToolWindowBackgroundColorKey));
            else if (root is Panel)
                root.SetValue(Panel.BackgroundProperty,
                    ThemeBrush(EnvironmentColors.ToolWindowBackgroundColorKey));

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var element = child as FrameworkElement;
                if (element != null) Apply(element);
            }
        }

        public static void Apply(FlowDocument document)
        {
            if (document == null) return;
            document.Foreground = ThemeBrush(EnvironmentColors.ToolWindowTextColorKey);
            document.Background = ThemeBrush(EnvironmentColors.ToolWindowBackgroundColorKey);
        }

        private static void SetForeground(FrameworkElement element)
        {
            if (element is ProgressBar) return;

            var tabControl = element as TabControl;
            if (tabControl != null)
            {
                tabControl.Background = ThemeBrush(EnvironmentColors.ToolWindowBackgroundColorKey);
                tabControl.Foreground = ThemeBrush(EnvironmentColors.ToolWindowTextColorKey);
                tabControl.Template = CreateTabControlTemplate();
                return;
            }

            var tabItem = element as TabItem;
            if (tabItem != null)
            {
                tabItem.Background = ThemeBrush(EnvironmentColors.ToolWindowTabGradientBeginColorKey);
                tabItem.Foreground = ThemeBrush(EnvironmentColors.ToolWindowTabTextColorKey);
                tabItem.Template = CreateTabItemTemplate();
                return;
            }

            var button = element as Button;
            if (button != null)
            {
                button.Foreground = ThemeBrush(EnvironmentColors.ToolWindowTextColorKey);
                button.Background = ThemeBrush(EnvironmentColors.ToolWindowBackgroundColorKey);
                button.BorderBrush = ThemeBrush(EnvironmentColors.ToolWindowBorderColorKey);
                button.Template = CreateButtonTemplate();
                return;
            }

            var comboBox = element as ComboBox;
            if (comboBox != null)
            {
                comboBox.Foreground = ThemeBrush(EnvironmentColors.ComboBoxTextColorKey);
                comboBox.Background = ThemeBrush(EnvironmentColors.ComboBoxBackgroundColorKey);
                comboBox.BorderBrush = ThemeBrush(EnvironmentColors.ComboBoxBorderColorKey);
                comboBox.ItemContainerStyle = CreateComboBoxItemStyle();
                comboBox.Template = CreateComboBoxTemplate();
                return;
            }

            if (element is TextBlock)
                element.SetValue(TextBlock.ForegroundProperty,
                    ThemeBrush(EnvironmentColors.ToolWindowTextColorKey));
            else if (element is Control)
                element.SetValue(Control.ForegroundProperty,
                    ThemeBrush(EnvironmentColors.ToolWindowTextColorKey));
        }

        private static Style CreateComboBoxItemStyle()
        {
            var style = new Style(typeof(ComboBoxItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxItemTextColorKey)));
            style.Setters.Add(new Setter(Control.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxPopupBackgroundBeginColorKey)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(5, 3, 5, 3)));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ItemBorder";
            border.SetValue(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxPopupBackgroundBeginColorKey));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 2, 4, 2));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(ComboBoxItem)) { VisualTree = border };

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxItemMouseOverBackgroundColorKey), "ItemBorder"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxItemMouseOverTextColorKey)));
            template.Triggers.Add(hover);

            var selected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxSelectionColorKey), "ItemBorder"));
            template.Triggers.Add(selected);
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            return style;
        }

        private static ControlTemplate CreateButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ButtonBorder";
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content")
                { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("ContentTemplate")
                { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 2, 5, 2));
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowButtonHoverActiveColorKey), "ButtonBorder"));
            template.Triggers.Add(hover);
            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowButtonDownColorKey), "ButtonBorder"));
            template.Triggers.Add(pressed);
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
            template.Triggers.Add(disabled);
            return template;
        }

        private static ControlTemplate CreateComboBoxTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));

            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ComboBorder";
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            grid.AppendChild(border);

            var selection = new FrameworkElementFactory(typeof(ContentPresenter));
            selection.Name = "SelectionContent";
            selection.SetValue(ContentPresenter.IsHitTestVisibleProperty, false);
            selection.SetValue(FrameworkElement.MarginProperty, new Thickness(7, 2, 28, 2));
            selection.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            selection.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectionBoxItem")
                { RelativeSource = RelativeSource.TemplatedParent });
            selection.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("SelectionBoxItemTemplate")
                { RelativeSource = RelativeSource.TemplatedParent });
            grid.AppendChild(selection);

            var editor = new FrameworkElementFactory(typeof(TextBox));
            editor.Name = "PART_EditableTextBox";
            editor.SetValue(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            editor.SetValue(Control.ForegroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxTextColorKey));
            editor.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            editor.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 1, 28, 1));
            editor.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            editor.SetBinding(TextBox.TextProperty, new Binding("Text")
            {
                RelativeSource = RelativeSource.TemplatedParent,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
            grid.AppendChild(editor);

            var toggle = new FrameworkElementFactory(typeof(ToggleButton));
            toggle.Name = "DropDownToggle";
            toggle.SetValue(FrameworkElement.WidthProperty, 25.0);
            toggle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            toggle.SetValue(Control.FocusableProperty, false);
            toggle.SetValue(ContentControl.ContentProperty, "▼");
            toggle.SetValue(Control.ForegroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxGlyphColorKey));
            toggle.SetValue(Control.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxBackgroundColorKey));
            toggle.SetValue(Control.BorderBrushProperty,
                ThemeBrush(EnvironmentColors.ComboBoxBorderColorKey));
            toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding("IsDropDownOpen")
                { RelativeSource = RelativeSource.TemplatedParent, Mode = BindingMode.TwoWay });
            toggle.SetValue(Control.TemplateProperty, CreateToggleTemplate());
            grid.AppendChild(toggle);

            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.FocusableProperty, false);
            popup.SetBinding(Popup.IsOpenProperty, new Binding("IsDropDownOpen")
                { RelativeSource = RelativeSource.TemplatedParent });

            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxPopupBackgroundBeginColorKey));
            popupBorder.SetValue(Border.BorderBrushProperty,
                ThemeBrush(EnvironmentColors.ComboBoxPopupBorderColorKey));
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetBinding(FrameworkElement.MinWidthProperty, new Binding("ActualWidth")
                { RelativeSource = RelativeSource.TemplatedParent });
            popupBorder.SetBinding(FrameworkElement.MaxHeightProperty, new Binding("MaxDropDownHeight")
                { RelativeSource = RelativeSource.TemplatedParent });
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
            scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
            popupBorder.AppendChild(scroll);
            popup.AppendChild(popupBorder);
            grid.AppendChild(popup);

            var template = new ControlTemplate(typeof(ComboBox)) { VisualTree = grid };
            var editable = new Trigger { Property = ComboBox.IsEditableProperty, Value = true };
            editable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed, "SelectionContent"));
            editable.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "PART_EditableTextBox"));
            template.Triggers.Add(editable);
            return template;
        }

        private static ControlTemplate CreateToggleTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "ToggleBorder";
            border.SetBinding(Border.BackgroundProperty, new Binding("Background")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush")
                { RelativeSource = RelativeSource.TemplatedParent });
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1, 0, 0, 0));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content")
                { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("ContentTemplate")
                { RelativeSource = RelativeSource.TemplatedParent });
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxButtonMouseOverBackgroundColorKey), "ToggleBorder"));
            template.Triggers.Add(hover);
            var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ComboBoxButtonMouseDownBackgroundColorKey), "ToggleBorder"));
            template.Triggers.Add(checkedTrigger);
            return template;
        }

        private static ControlTemplate CreateTabControlTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(DockPanel));
            panel.SetValue(DockPanel.LastChildFillProperty, true);

            var headers = new FrameworkElementFactory(typeof(TabPanel));
            headers.SetValue(DockPanel.DockProperty, Dock.Top);
            headers.SetValue(Panel.IsItemsHostProperty, true);
            headers.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 1));
            panel.AppendChild(headers);

            var contentBorder = new FrameworkElementFactory(typeof(Border));
            contentBorder.SetValue(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowBackgroundColorKey));
            contentBorder.SetValue(Border.BorderBrushProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabBorderColorKey));
            contentBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
            content.SetBinding(ContentPresenter.ContentProperty, new Binding("SelectedContent")
                { RelativeSource = RelativeSource.TemplatedParent });
            content.SetBinding(ContentPresenter.ContentTemplateProperty, new Binding("SelectedContentTemplate")
                { RelativeSource = RelativeSource.TemplatedParent });
            contentBorder.AppendChild(content);
            panel.AppendChild(contentBorder);

            return new ControlTemplate(typeof(TabControl)) { VisualTree = panel };
        }

        private static ControlTemplate CreateTabItemTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "TabBorder";
            border.SetValue(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabGradientBeginColorKey));
            border.SetValue(Border.BorderBrushProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabBorderColorKey));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1, 1, 1, 0));
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 5, 10, 5));
            border.AppendChild(header);

            var template = new ControlTemplate(typeof(TabItem)) { VisualTree = border };
            var selected = new Trigger { Property = Selector.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabSelectedTabColorKey), "TabBorder"));
            selected.Setters.Add(new Setter(Control.ForegroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabSelectedTextColorKey)));
            template.Triggers.Add(selected);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabMouseOverBackgroundBeginColorKey), "TabBorder"));
            hover.Setters.Add(new Setter(Control.ForegroundProperty,
                ThemeBrush(EnvironmentColors.ToolWindowTabMouseOverTextColorKey)));
            template.Triggers.Add(hover);
            return template;
        }

        private static SolidColorBrush ThemeBrush(ThemeResourceKey key)
        {
            System.Drawing.Color color = VSColorTheme.GetThemedColor(key);
            var brush = new SolidColorBrush(Color.FromArgb(
                color.A, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }
}
