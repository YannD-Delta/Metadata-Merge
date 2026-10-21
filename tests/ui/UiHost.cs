using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>A plain WPF host for the plugin views: one STA thread with an Application that has the localization dictionaries and a few theme brushes.</summary>
public static class UiHost
{
    private static Dispatcher dispatcher;
    private static Application app;
    private static readonly object Gate = new object();
    public static readonly List<string> BindingErrors = new List<string>();

    private sealed class Capture : TraceListener
    {
        public override void Write(string message) { }
        public override void WriteLine(string message) { lock (BindingErrors) BindingErrors.Add(message); }
    }

    public static string Repo { get { return Environment.GetEnvironmentVariable("METADATAMERGE_REPO"); } }

    public static void Run(Action action)
    {
        Ensure();
        Exception failure = null;
        dispatcher.Invoke(new Action(() => { try { action(); } catch (Exception ex) { failure = ex; } }));
        if (failure != null) throw new Exception(failure.GetType().Name + ": " + failure.Message);
    }

    private static void Ensure()
    {
        lock (Gate)
        {
            if (dispatcher != null) return;
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                dispatcher = Dispatcher.CurrentDispatcher;
                foreach (var key in new[] { "TextBrush", "TextBrushDarker", "GlyphBrush", "PopupBorderBrush" })
                    app.Resources[key] = new SolidColorBrush(Colors.Gray);
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                PresentationTraceSources.DataBindingSource.Listeners.Add(new Capture());
                UseLanguage("en_US");
                ready.Set();
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            ready.Wait();
        }
    }

    /// <summary>Loads en_US first, then the language over it (Playnite does the same).</summary>
    public static void UseLanguage(string code)
    {
        foreach (string name in code == "en_US" ? new[] { "en_US" } : new[] { "en_US", code })
        {
            using (var stream = File.OpenRead(Path.Combine(Repo, "src", "Localization", name + ".xaml")))
            {
                var dict = (ResourceDictionary)XamlReader.Load(stream);
                foreach (var key in dict.Keys) app.Resources[key] = dict[key];
            }
        }
    }

    public static void Layout(FrameworkElement view, double width = 1000, double height = 700)
    {
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
        view.UpdateLayout();
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    public static List<string> Texts(FrameworkElement view)
    {
        var list = new List<string>();
        foreach (var d in Descendants(view))
        {
            var tb = d as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text)) list.Add(tb.Text);
            var cp = d as ContentPresenter;
            if (cp != null && cp.Content is string) list.Add((string)cp.Content);
        }
        return list;
    }

    public static void ClearErrors() { lock (BindingErrors) BindingErrors.Clear(); }
    public static string Errors() { lock (BindingErrors) return string.Join(" | ", BindingErrors.Take(5)); }
}
