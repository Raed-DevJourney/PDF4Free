using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PDF4Free.Core.Editing;
using Calendar = System.Windows.Controls.Calendar;

namespace PDF4Free.App.Views;

/// <summary>
/// The inline editor of a date field: a box to type the date in, and a button that opens a calendar (with time
/// boxes when the field's format has a time). Whatever is picked is written into the box in the field's format.
/// </summary>
public sealed class DateFieldEditor : ContentControl
{
    private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(200, 30, 30));

    private readonly DateFieldFormat _format;
    private readonly Border _frame;
    private readonly TextBox _box;
    private readonly Button _open;
    private readonly Popup _popup;
    private Calendar? _calendar;
    private TimePart? _hour, _minute, _second;
    private ToggleButton? _afternoon;
    private bool _syncing;

    public DateFieldEditor(DateFieldFormat format, string text, Brush frameBrush, double fontSize)
    {
        _format = format;
        Focusable = true;
        IsTabStop = false;

        _box = new TextBox
        {
            Style = null, Text = text, FontFamily = new FontFamily("Arial"), FontSize = fontSize, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Brushes.Black, CaretBrush = Brushes.Black,
            VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(1, 0, 1, 0), ToolTip = $"{format.Kind}, for example {format.Example}",
        };
        AutomationProperties.SetName(_box, format.Kind);

        _open = new Button
        {
            Style = null, Focusable = false, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(3, 0, 3, 0),
            Cursor = Cursors.Hand, ToolTip = format.HasDate ? "Choose from a calendar (F4)" : "Choose the time (F4)",
            Content = new TextBlock
            {
                Text = format.HasDate ? Icons.Calendar : Icons.Clock, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = Math.Max(9, fontSize), Foreground = frameBrush, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationProperties.SetName(_open, format.HasDate ? "Open calendar" : "Choose time");
        _open.Click += (_, _) => OpenPicker();

        _popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, PlacementTarget = this, Child = BuildPicker() };
        // Without this, a click on the button while the picker is open would close it and open it again.
        _popup.Opened += (_, _) => _open.IsHitTestVisible = false;
        _popup.Closed += (_, _) =>
        {
            _open.IsHitTestVisible = true;
            if (IsLoaded) FocusBox();
        };
        Unloaded += (_, _) => _popup.IsOpen = false;

        var face = new DockPanel();
        DockPanel.SetDock(_open, Dock.Right);
        face.Children.Add(_open);
        face.Children.Add(_box);
        var frame = new Border { Background = Brushes.White, BorderBrush = frameBrush, BorderThickness = new Thickness(1.5), Child = face };
        _frame = frame;
        // Typing clears the red frame an unusable date was given.
        _box.TextChanged += (_, _) => frame.BorderBrush = frameBrush;
        var root = new Grid();
        root.Children.Add(_frame);
        root.Children.Add(_popup);
        Content = root;
    }

    /// <summary>Raised when Enter or Tab is pressed on text that is not a date; the editor stays open.</summary>
    public event Action? Rejected;

    public string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    public bool IsPickerOpen => _popup.IsOpen;

    public bool IsValid => _box.Text.Trim().Length == 0 || _format.TryParse(_box.Text, out _);

    public void SelectAll() => _box.SelectAll();

    /// <summary>Puts a date and time into the box, exactly as choosing it in the picker does.</summary>
    public void Pick(DateTime value)
    {
        Show(value);
        WriteChosen();
    }

    public void OpenPicker()
    {
        Show(_format.TryParse(_box.Text, out var typed) ? typed : DateTime.Now);
        _popup.IsOpen = true;
        Dispatcher.BeginInvoke(() => ((UIElement?)_calendar ?? _hour?.Box)?.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this)) FocusBox();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (_popup.IsOpen)
        {
            // Inside the picker, Enter means "use this" and Esc means "never mind"; neither ends the editing yet.
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            if (e.Key == Key.Enter) WriteChosen();
            _popup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.F4 || (alt && e.SystemKey == Key.Down))
        {
            OpenPicker();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Tab && !IsValid)
        {
            _frame.BorderBrush = ErrorBrush;
            Rejected?.Invoke();
            e.Handled = true;
        }
    }

    private void FocusBox()
    {
        _box.Focus();
        _box.CaretIndex = _box.Text.Length;
    }

    // ------------------------------------------------------------------ the picker

    private Border BuildPicker()
    {
        var panel = new StackPanel { Margin = new Thickness(10) };
        if (_format.HasDate) panel.Children.Add(BuildCalendar());
        if (_format.HasTime) panel.Children.Add(BuildTimeRow());
        panel.Children.Add(BuildButtons());
        var surface = new Border { Child = panel, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0) };
        surface.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorTertiaryBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        surface.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        return surface;
    }

    private Calendar BuildCalendar()
    {
        _calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate, IsTodayHighlighted = true };
        _calendar.SelectedDatesChanged += (_, _) =>
        {
            if (!_syncing) WriteChosen();
        };
        _calendar.PreviewMouseUp += (_, e) =>
        {
            // The calendar keeps the mouse after a click, which would swallow the next click made anywhere else.
            // It goes back to the picker itself, which holds it while it is open (letting go of it altogether can
            // close the picker).
            if (Mouse.Captured is CalendarItem) Mouse.Capture(PresentationSource.FromVisual(_calendar)?.RootVisual as IInputElement, CaptureMode.SubTree);
            // With no time to set, clicking a day finishes the choice.
            if (!_format.HasTime && IsDay(e.OriginalSource as DependencyObject)) Dispatcher.BeginInvoke(() => _popup.IsOpen = false);
        };
        return _calendar;
    }

    private bool IsDay(DependencyObject? element)
    {
        for (; element is not null && !ReferenceEquals(element, _calendar); element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is CalendarDayButton) return true;
        }
        return false;
    }

    private StackPanel BuildTimeRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock { Text = "Time", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        _hour = _format.TwelveHour ? AddTimePart(row, "Hours", 1, 12, "0") : AddTimePart(row, "Hours", 0, 23, "00");
        row.Children.Add(Colon());
        _minute = AddTimePart(row, "Minutes", 0, 59, "00");
        if (_format.HasSeconds)
        {
            row.Children.Add(Colon());
            _second = AddTimePart(row, "Seconds", 0, 59, "00");
        }
        if (_format.TwelveHour) row.Children.Add(BuildAfternoonToggle());
        return row;
    }

    private static TextBlock Colon() => new() { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };

    private TimePart AddTimePart(Panel row, string name, int minimum, int maximum, string format)
    {
        var part = new TimePart(name, minimum, maximum, format);
        part.Box.TextChanged += (_, _) =>
        {
            if (!_syncing) WriteChosen();
        };
        row.Children.Add(part.Box);
        return part;
    }

    private ToggleButton BuildAfternoonToggle()
    {
        _afternoon = new ToggleButton { MinWidth = 52, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Switch between am and pm" };
        AutomationProperties.SetName(_afternoon, "PM");
        void Changed()
        {
            _afternoon.Content = _afternoon.IsChecked == true ? "pm" : "am";
            if (!_syncing) WriteChosen();
        }
        _afternoon.Checked += (_, _) => Changed();
        _afternoon.Unchecked += (_, _) => Changed();
        _afternoon.Content = "am";
        return _afternoon;
    }

    private DockPanel BuildButtons()
    {
        var now = new Button { Content = _format.HasTime ? "Now" : "Today", MinWidth = 70 };
        now.Click += (_, _) =>
        {
            Pick(DateTime.Now);
            if (!_format.HasTime) _popup.IsOpen = false;
        };
        var clear = new Button { Content = "Clear", MinWidth = 70, Margin = new Thickness(8, 0, 0, 0) };
        clear.Click += (_, _) =>
        {
            _box.Text = "";
            _popup.IsOpen = false;
        };
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        row.Children.Add(now);
        row.Children.Add(clear);
        if (!_format.HasTime) return row;

        var done = new Button { Content = "Done", MinWidth = 70, Margin = new Thickness(24, 0, 0, 0) };
        done.Click += (_, _) =>
        {
            WriteChosen();
            _popup.IsOpen = false;
        };
        DockPanel.SetDock(done, Dock.Right);
        row.Children.Add(done);
        return row;
    }

    /// <summary>Sets the calendar and the time boxes without treating it as a choice.</summary>
    private void Show(DateTime value)
    {
        _syncing = true;
        if (_calendar is not null)
        {
            _calendar.SelectedDate = value.Date;
            _calendar.DisplayDate = value.Date;
        }
        if (_hour is not null) _hour.Value = _afternoon is null ? value.Hour : (value.Hour + 11) % 12 + 1;
        if (_minute is not null) _minute.Value = value.Minute;
        if (_second is not null) _second.Value = value.Second;
        if (_afternoon is not null) _afternoon.IsChecked = value.Hour >= 12;
        _syncing = false;
    }

    private DateTime Chosen()
    {
        var day = _calendar?.SelectedDate ?? DateTime.Today;
        if (_hour is null || _minute is null) return day;
        var hour = _afternoon is null ? _hour.Value : _hour.Value % 12 + (_afternoon.IsChecked == true ? 12 : 0);
        return day.AddHours(hour).AddMinutes(_minute.Value).AddSeconds(_second?.Value ?? 0);
    }

    private void WriteChosen() => _box.Text = _format.Format(Chosen());

#if DEBUG
    /// <summary>
    /// Test hook: presses and releases the left button on a day of the open calendar by raising the mouse events on
    /// its button, so the calendar's own click handling runs without the real mouse.
    /// </summary>
    public bool AutomationClickDay(DateTime day)
    {
        if (!_popup.IsOpen || _calendar is null) return false;
        _calendar.DisplayDate = day;
        _calendar.UpdateLayout();
        var button = DayButtons(_calendar).FirstOrDefault(b => b.DataContext is DateTime shown && shown.Date == day.Date);
        if (button is null) return false;
        foreach (var step in new[] { Mouse.PreviewMouseDownEvent, Mouse.MouseDownEvent, Mouse.PreviewMouseUpEvent, Mouse.MouseUpEvent })
        {
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = step });
        }
        return true;
    }

    /// <summary>Test hook: where a key press would arrive (the picker while it is open, otherwise the box).</summary>
    public UIElement KeyTarget => !_popup.IsOpen ? _box : (UIElement?)_calendar ?? _hour?.Box ?? _box;

    private static IEnumerable<CalendarDayButton> DayButtons(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is CalendarDayButton day) yield return day;
            foreach (var nested in DayButtons(child)) yield return nested;
        }
    }

    /// <summary>Test hook: the time boxes and the am/pm switch, so a test can change them the way typing does.</summary>
    public bool AutomationSetTime(int hour, int minute)
    {
        if (!_popup.IsOpen || _hour is null || _minute is null) return false;
        if (_afternoon is not null) _afternoon.IsChecked = hour >= 12;
        _hour.Box.Text = (_afternoon is null ? hour : (hour + 11) % 12 + 1).ToString(CultureInfo.InvariantCulture);
        _minute.Box.Text = minute.ToString("00", CultureInfo.InvariantCulture);
        return true;
    }
#endif

    /// <summary>One number of the time (hours, minutes or seconds): a small box that also answers the arrow keys and the wheel.</summary>
    private sealed class TimePart
    {
        private readonly int _minimum, _maximum;
        private readonly string _format;

        public TimePart(string name, int minimum, int maximum, string format)
        {
            (_minimum, _maximum, _format) = (minimum, maximum, format);
            Box = new TextBox { Width = 46, MaxLength = 2, TextAlignment = TextAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
            AutomationProperties.SetName(Box, name);
            Box.GotKeyboardFocus += (_, _) => Box.SelectAll();
            Box.PreviewMouseWheel += (_, e) =>
            {
                Spin(e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };
            Box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Up or Key.Down)) return;
                Spin(e.Key == Key.Up ? 1 : -1);
                e.Handled = true;
            };
        }

        public TextBox Box { get; }

        public int Value
        {
            get => int.TryParse(Box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed) ? Math.Clamp(typed, _minimum, _maximum) : _minimum;
            set => Box.Text = value.ToString(_format, CultureInfo.InvariantCulture);
        }

        private void Spin(int step)
        {
            var count = _maximum - _minimum + 1;
            Value = _minimum + ((Value - _minimum + step) % count + count) % count;
        }
    }
}
