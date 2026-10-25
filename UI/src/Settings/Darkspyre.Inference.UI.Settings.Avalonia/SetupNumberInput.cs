using System.Globalization;
using System.Numerics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Darkspyre.Inference.UI.Settings.Avalonia;

/// <summary>
/// A numeric text box with decrease and increase buttons that parses typed text exactly and reports each edit with its
/// value or error. Parsing and stepping never narrow a long or a decimal through a double and never coerce typed input.
/// </summary>
/// <typeparam name="T">The number type: <see cref="long"/> or <see cref="decimal"/>.</typeparam>
public sealed class SetupNumberInput<T> : UserControl where T : struct, INumber<T>, IMinMaxValue<T>
{
    private readonly TextBox box = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button decrease = new() { Content = "−" };
    private readonly Button increase = new() { Content = "+" };
    private bool syncing;
    private string shown = string.Empty;
    private bool disabled;
    private bool invalid;

    /// <summary>Creates the input.</summary>
    /// <param name="label">The accessible name of the box, also used in the buttons' names.</param>
    /// <param name="minimum">The lowest accepted value, or null for the type's minimum.</param>
    /// <param name="maximum">The highest accepted value, or null for the type's maximum.</param>
    /// <param name="step">The positive amount the buttons and arrow keys add or subtract.</param>
    /// <param name="required">True reports an empty box as an error.</param>
    public SetupNumberInput(string label, T? minimum, T? maximum, T step, bool required)
    {
        if (step <= T.Zero || minimum is { } min && maximum is { } max && min > max)
            throw new InvalidOperationException("Numeric metadata requires a positive step and ordered bounds.");
        this.Minimum = minimum;
        this.Maximum = maximum;
        this.Step = step;
        this.Required = required;
        global::Avalonia.Automation.AutomationProperties.SetName(this.box, label);
        global::Avalonia.Automation.AutomationProperties.SetName(this.decrease, $"Decrease {label}");
        global::Avalonia.Automation.AutomationProperties.SetName(this.increase, $"Increase {label}");
        this.decrease.Classes.Add("outline");
        this.increase.Classes.Add("outline");
        this.decrease.Click += (_, _) => this.StepBy(false);
        this.increase.Click += (_, _) => this.StepBy(true);
        this.box.TextChanged += (_, _) =>
        {
            // The box reports text set by Show after Show returns; only text that differs from it is an edit.
            string text = this.box.Text ?? string.Empty;
            if (!this.syncing && !this.disabled && text != this.shown) this.Edited?.Invoke(this, this.Parse(text));
        };
        this.box.AddHandler(KeyDownEvent, (_, args) =>
        {
            if (args.Key is not (Key.Up or Key.Down)) return;
            args.Handled = true;
            this.StepBy(args.Key == Key.Up);
        }, RoutingStrategies.Tunnel);
        DockPanel layout = new();
        DockPanel.SetDock(this.decrease, Dock.Left);
        DockPanel.SetDock(this.increase, Dock.Right);
        layout.Children.Add(this.decrease);
        layout.Children.Add(this.increase);
        layout.Children.Add(this.box);
        layout.Classes.Add("setup-number-input");
        this.Content = layout;
    }

    /// <summary>The lowest accepted value, or null for the type's minimum.</summary>
    public T? Minimum { get; }

    /// <summary>The highest accepted value, or null for the type's maximum.</summary>
    public T? Maximum { get; }

    /// <summary>The positive amount the buttons and arrow keys add or subtract.</summary>
    public T Step { get; }

    /// <summary>True reports an empty box as an error.</summary>
    public bool Required { get; }

    /// <summary>The text shown in the box.</summary>
    public string Text => this.box.Text ?? string.Empty;

    /// <summary>Raised on each input or step with the text, its parsed value and any error.</summary>
    public event EventHandler<SetupNumberEdit<T>>? Edited;

    /// <summary>Shows <paramref name="text"/>; <paramref name="isDisabled"/> disables the box and both buttons; <paramref name="isInvalid"/> draws the error border.</summary>
    public void Show(string text, bool isDisabled, bool isInvalid)
    {
        this.disabled = isDisabled;
        this.invalid = isInvalid;
        this.shown = text;
        if (this.box.Text != text)
        {
            this.syncing = true;
            this.box.Text = text;
            this.syncing = false;
        }
        this.box.IsEnabled = !isDisabled;
        this.box.Classes.Set("invalid", isInvalid);
        this.decrease.IsEnabled = !isDisabled && this.CanStep(false);
        this.increase.IsEnabled = !isDisabled && this.CanStep(true);
    }

    /// <summary>The edit that <paramref name="text"/> makes: its value and, when it is not accepted, the reason.</summary>
    public SetupNumberEdit<T> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(text, null, this.Required ? "A value is required." : null);
        NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;
        if (typeof(T) != typeof(long)) styles |= NumberStyles.AllowDecimalPoint;
        if (!T.TryParse(text, styles, CultureInfo.InvariantCulture, out T value))
            return new(text, null, typeof(T) == typeof(long) ? "Enter a whole number within the supported range." : "Enter a decimal number within the supported range.");
        if (typeof(T) == typeof(decimal) && NormalizeDecimal(text) != NormalizeDecimal(Format(value)!))
            return new(text, null, "The number has more precision than the supported decimal range.");
        if (this.Minimum is { } min && value < min || this.Maximum is { } max && value > max)
            return new(text, value, $"Enter a value between {Format(this.Minimum) ?? "the supported minimum"} and {Format(this.Maximum) ?? "the supported maximum"}.");
        return new(text, value, null);
    }

    private static string? Format(T? value) => value?.ToString(null, CultureInfo.InvariantCulture);

    private static string NormalizeDecimal(string text)
    {
        text = text.Trim();
        bool negative = text.StartsWith('-');
        text = text.TrimStart('+', '-');
        string[] parts = text.Split('.');
        string whole = parts[0].TrimStart('0');
        string fraction = parts.Length > 1 ? parts[1].TrimEnd('0') : string.Empty;
        if (whole.Length == 0) whole = "0";
        return (negative && (whole != "0" || fraction.Length > 0) ? "-" : string.Empty) + whole + (fraction.Length > 0 ? "." + fraction : string.Empty);
    }

    private bool CanStep(bool up)
    {
        SetupNumberEdit<T> parsed = this.Parse(this.Text);
        if (!string.IsNullOrWhiteSpace(this.Text) && parsed.Error is not null) return false;
        return parsed.Value is not { } value || (up ? value < (this.Maximum ?? T.MaxValue) : value > (this.Minimum ?? T.MinValue));
    }

    private void StepBy(bool up)
    {
        if (this.disabled || !this.CanStep(up)) return;
        T current = this.Parse(this.Text).Value ?? T.Zero;
        T next;
        try { next = up ? checked(current + this.Step) : checked(current - this.Step); }
        catch (OverflowException) { next = up ? T.MaxValue : T.MinValue; }
        next = T.Clamp(next, this.Minimum ?? T.MinValue, this.Maximum ?? T.MaxValue);
        SetupNumberEdit<T> edit = this.Parse(Format(next)!);
        this.Show(edit.Text, this.disabled, this.invalid);
        this.Edited?.Invoke(this, edit);
    }
}
