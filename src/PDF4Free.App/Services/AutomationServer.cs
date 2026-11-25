#if DEBUG
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using PDF4Free.App.Views;

namespace PDF4Free.App.Services;

/// <summary>
/// Debug builds only, and only when started with --automation: a named pipe that accepts one-line test commands
/// ("tool Highlight", "drag 0 100 150 300 150") so the interactive tools can be verified from a script.
/// </summary>
internal sealed class AutomationServer(Func<PdfViewer?> viewer, Func<PDF4Free.Core.PdfSession?> session, Func<Task> idle)
{
    public const string PipeName = "PDF4Free.Automation";

    public void Start() => _ = Task.Run(ListenAsync);

    private async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var line = await reader.ReadLineAsync() ?? "";
                var reply = await Application.Current.Dispatcher.InvokeAsync(() => RunAsync(line)).Task.Unwrap();
                var bytes = Encoding.UTF8.GetBytes(reply.Replace('\n', ' ').Replace('\r', ' ') + "\n");
                await pipe.WriteAsync(bytes);
                await pipe.FlushAsync();
            }
            catch (IOException)
            {
                // The client went away before reading the reply; wait for the next one.
            }
        }
    }

    private async Task<string> RunAsync(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "error: empty command";
        var view = viewer();
        var doc = session();
        if (view is null || doc is null) return "error: no document";
        static double N(string s) => double.Parse(s, CultureInfo.InvariantCulture);
        try
        {
            switch (parts[0])
            {
                case "tool":
                    view.Tool = Enum.Parse<ViewerTool>(parts[1], ignoreCase: true);
                    return "ok tool=" + view.Tool;
                case "drag":
                    await view.AutomationDragAsync(int.Parse(parts[1]), new Point(N(parts[2]), N(parts[3])), new Point(N(parts[4]), N(parts[5])), parts.Length > 6 ? int.Parse(parts[6]) : 1);
                    return "ok";
                case "type":
                    // "\n" in the command stands for a line break, so multi-line fields can be filled from one line.
                    return view.AutomationType(line[(line.IndexOf(' ') + 1)..].Replace("\\n", "\n")) ? "ok" : "error: no editor is open";
                case "idle":
                    // Returns once every queued edit has been applied and the form fields were re-read.
                    await idle();
                    await view.AutomationFieldsReadyAsync();
                    return "ok";
                case "fields":
                    return view.AutomationFieldsJson();
                case "find":
                    return view.AutomationFindText(int.Parse(parts[1]), line[(line.IndexOf(parts[1], 5, StringComparison.Ordinal) + parts[1].Length + 1)..]) is { Length: > 0 } where ? where : "error: text not found";
                case "describe":
                    return view.AutomationDescribe(parts.Length > 1 ? int.Parse(parts[1]) : view.CurrentPage);
                case "delete":
                    await view.DeleteSelectionAsync();
                    return "ok";
                case "undo":
                    doc.Undo();
                    return "ok";
                case "goto":
                    view.GoToPage(int.Parse(parts[1]));
                    return "ok";
                case "zoom":
                    view.SetZoom(N(parts[1]));
                    return "ok";
                case "info":
                    return $"pages={doc.PageCount} dirty={doc.IsDirty} undo='{doc.UndoDescription}' page={view.CurrentPage} zoom={view.Zoom.ToString("0.##", CultureInfo.InvariantCulture)} bytes={doc.Bytes.Length}";
                default:
                    return "error: unknown command " + parts[0];
            }
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }
}
#endif
