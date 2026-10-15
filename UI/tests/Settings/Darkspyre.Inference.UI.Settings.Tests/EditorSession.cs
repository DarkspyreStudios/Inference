using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Darkspyre.Inference.UI.Settings.Tests;

/// <summary>The headless Avalonia session the editor tests run in. A test runs on its UI thread and shows the editor in a window.</summary>
internal static class EditorSession
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(EditorSession));

    /// <summary>Avalonia's entry point for the headless session.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    /// <summary>Runs <paramref name="test"/> on the session's UI thread.</summary>
    public static Task RunAsync(Func<Task> test) => Session.Dispatch(async () =>
    {
        await test();
        return true;
    }, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(1));

    /// <summary>Shows <paramref name="content"/> in a window and lets layout and queued work run.</summary>
    public static Window Show(Control content)
    {
        Window window = new() { Content = content, Width = 900, Height = 900 };
        window.Show();
        Settle();
        return window;
    }

    /// <summary>Runs every job queued on the UI thread.</summary>
    public static void Settle()
    {
        for (int pass = 0; pass < 5; pass++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Settles until <paramref name="condition"/> holds, or fails after five seconds.</summary>
    public static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            Settle();
            if (condition()) return;
            if (DateTime.UtcNow > end) throw new TimeoutException("The editor never reached the expected state.");
            await Task.Delay(10);
        }
    }

    /// <summary>Every control of type <typeparamref name="T"/> under <paramref name="root"/> that carries <paramref name="classes"/>.</summary>
    public static IEnumerable<T> All<T>(Visual root, params string[] classes) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Where(control => classes.All(control.Classes.Contains));

    /// <summary>The text of every visible text block under <paramref name="root"/>, in visual order.</summary>
    public static string TextOf(Visual root) => string.Join("\n", root.GetVisualDescendants().OfType<TextBlock>()
        .Where(block => block.IsEffectivelyVisible).Select(block => block.Text).Where(text => !string.IsNullOrEmpty(text)));

    /// <summary>The input showing the field <paramref name="id"/>.</summary>
    public static SetupFieldInput Field(Visual root, string id) => Assert.Single(All<SetupFieldInput>(root), field => field.Field.Description.Id == id);

    /// <summary>The visible buttons under <paramref name="root"/> whose text is <paramref name="label"/>.</summary>
    public static Button Button(Visual root, string label) => Assert.Single(All<Button>(root),
        button => button.IsEffectivelyVisible && (button.Content as string == label || Avalonia.Automation.AutomationProperties.GetName(button) == label));

    /// <summary>Clicks <paramref name="button"/> and lets the work it starts run.</summary>
    public static void Click(Button button)
    {
        Assert.True(button.IsEffectivelyEnabled, $"{button.Content} is disabled.");
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle();
    }

    /// <summary>Types <paramref name="text"/> into the field's first text box, as a person replacing its text does.</summary>
    public static void Type(SetupFieldInput field, string text)
    {
        All<TextBox>(field).First(box => box.IsEffectivelyVisible).Text = text;
        Settle();
    }

    /// <summary>The text of the field's first text box.</summary>
    public static string? TextIn(SetupFieldInput field) => All<TextBox>(field).First(box => box.IsEffectivelyVisible).Text;

    private sealed class TestApplication : Application
    {
        public override void Initialize() => this.Styles.Add(new FluentTheme());
    }
}
