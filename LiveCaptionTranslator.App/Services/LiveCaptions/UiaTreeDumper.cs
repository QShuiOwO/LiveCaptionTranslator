using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Automation;
using Rect = System.Windows.Rect;

namespace LiveCaptionTranslator.App.Services.LiveCaptions;

public sealed class UiaTreeDumper
{
    private const int FallbackDumpMaxDepth = 4;
    private const int MaxDumpedNodes = 5000;

    private readonly LiveCaptionsDetector _detector;

    public UiaTreeDumper(LiveCaptionsDetector detector)
    {
        _detector = detector;
    }

    public Task<string> DumpAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Dump(cancellationToken), cancellationToken);
    }

    private string Dump(CancellationToken cancellationToken)
    {
        var logsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "logs");
        Directory.CreateDirectory(logsDirectory);

        var outputPath = Path.Combine(logsDirectory, "uia_tree_dump.txt");
        var status = _detector.Detect();
        var builder = new StringBuilder();

        builder.AppendLine($"DumpTime: {DateTimeOffset.Now:O}");
        builder.AppendLine($"LiveCaptionsStatus: {status.Message}");
        if (!string.IsNullOrWhiteSpace(status.ErrorMessage))
        {
            builder.AppendLine($"Error: {status.ErrorMessage}");
        }

        builder.AppendLine();

        if (status.WindowElement is null)
        {
            builder.AppendLine("Live captions window was not found. Dumping desktop top-level windows for diagnostics.");
            builder.AppendLine();

            DumpDesktopTopLevelWindows(builder, cancellationToken);
            File.WriteAllText(outputPath, builder.ToString(), Encoding.UTF8);
            return outputPath;
        }

        var dumpedNodes = 0;
        DumpNode(status.WindowElement, builder, 0, cancellationToken, maxDepth: null, ref dumpedNodes);
        File.WriteAllText(outputPath, builder.ToString(), Encoding.UTF8);
        return outputPath;
    }

    private static void DumpDesktopTopLevelWindows(StringBuilder builder, CancellationToken cancellationToken)
    {
        try
        {
            var root = AutomationElement.RootElement;
            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            var dumpedNodes = 0;

            foreach (AutomationElement window in windows)
            {
                if (dumpedNodes >= MaxDumpedNodes)
                {
                    builder.AppendLine("Max dumped node count reached.");
                    return;
                }

                DumpNode(window, builder, 0, cancellationToken, FallbackDumpMaxDepth, ref dumpedNodes);
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"DesktopDumpError: {ex.Message}");
        }
    }

    private static void DumpNode(
        AutomationElement element,
        StringBuilder builder,
        int depth,
        CancellationToken cancellationToken,
        int? maxDepth,
        ref int dumpedNodes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dumpedNodes >= MaxDumpedNodes)
        {
            return;
        }

        dumpedNodes++;
        var indent = new string(' ', depth * 2);
        try
        {
            builder.AppendLine($"{indent}- Name: {SafeRead(() => element.Current.Name)}");
            builder.AppendLine($"{indent}  AutomationId: {SafeRead(() => element.Current.AutomationId)}");
            builder.AppendLine($"{indent}  ClassName: {SafeRead(() => element.Current.ClassName)}");
            builder.AppendLine($"{indent}  ControlType: {SafeRead(() => element.Current.ControlType.ProgrammaticName)}");
            builder.AppendLine($"{indent}  FrameworkId: {SafeRead(() => element.Current.FrameworkId)}");
            builder.AppendLine($"{indent}  BoundingRectangle: {SafeRead(() => FormatRect(element.Current.BoundingRectangle))}");
            builder.AppendLine($"{indent}  IsOffscreen: {SafeRead(() => element.Current.IsOffscreen.ToString())}");
            builder.AppendLine($"{indent}  SupportsTextPattern: {SafeRead(() => SupportsPattern(element, TextPattern.Pattern).ToString())}");
            builder.AppendLine($"{indent}  SupportsValuePattern: {SafeRead(() => SupportsPattern(element, ValuePattern.Pattern).ToString())}");
        }
        catch (Exception ex)
        {
            builder.AppendLine($"{indent}- NodeReadError: {ex.Message}");
        }

        if (maxDepth is not null && depth >= maxDepth.Value)
        {
            builder.AppendLine($"{indent}  Children: <skipped at fallback depth limit>");
            return;
        }

        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var child = walker.GetFirstChild(element);
            while (child is not null && dumpedNodes < MaxDumpedNodes)
            {
                DumpNode(child, builder, depth + 1, cancellationToken, maxDepth, ref dumpedNodes);
                child = walker.GetNextSibling(child);
            }
        }
        catch (Exception ex)
        {
            builder.AppendLine($"{indent}  ChildrenReadError: {ex.Message}");
        }
    }

    private static string SafeRead(Func<string?> read)
    {
        try
        {
            return read() ?? string.Empty;
        }
        catch (Exception ex)
        {
            return $"<read failed: {ex.Message}>";
        }
    }

    private static bool SupportsPattern(AutomationElement element, AutomationPattern pattern)
    {
        try
        {
            return element.TryGetCurrentPattern(pattern, out _);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatRect(Rect rect)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "Left={0}, Top={1}, Width={2}, Height={3}",
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height);
    }
}
