using DittoHopper.Core;
using DittoHopper.Rhino;
using Rhino;
using Rhino.Commands;
using Rhino.Input;
using Rhino.Input.Custom;

namespace DittoHopper.PlugIn;

/// <summary>
/// DittoReview: compares two models and opens the review panel. By default A is the document's last save and B the
/// document as it is now; options make A a file, and B a file instead of the document.
/// Files are read on Rhino's main thread, so a large model pauses Rhino while it is read.
/// </summary>
public sealed class DittoReviewCommand : Command
{
    public enum ASide { LastSave, File }
    public enum BSide { Document, File }

    public override string EnglishName => "DittoReview";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var a = ASide.LastSave;
        var b = BSide.Document;
        string? aPath = null, bPath = null;
        var get = new GetOption();
        get.SetCommandPrompt("Compare A (before) with B (after). Press Enter to compare");
        get.AcceptNothing(true);
        while (true)
        {
            get.ClearCommandOptions();
            int optA = get.AddOptionEnumList("A", a);
            int optB = get.AddOptionEnumList("B", b);
            var got = get.Get();
            if (got == GetResult.Nothing) break;
            if (got != GetResult.Option) return Result.Cancel;
            int index = get.Option().Index;
            if (index == optA)
            {
                a = (ASide)get.Option().CurrentListOptionIndex;
                aPath = a == ASide.File ? File("A: the model before", mode) : null;
                if (a == ASide.File && aPath is null) a = ASide.LastSave;
            }
            else if (index == optB)
            {
                b = (BSide)get.Option().CurrentListOptionIndex;
                bPath = b == BSide.File ? File("B: the model after", mode) : null;
                if (b == BSide.File && bPath is null) b = BSide.Document;
            }
        }

        try
        {
            Review(new ReviewSource(doc.RuntimeSerialNumber, aPath, bPath));
            RhinoApp.WriteLine(ReviewSession.Current.Review?.Sentence ?? "");
            return Result.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException)
        {
            RhinoApp.WriteLine("DittoReview: " + ex.Message);
            return Result.Failure;
        }
    }

    /// <summary>
    /// What the command does once A and B are chosen: snapshots both, diffs them with the panel's current scope and
    /// shows the result. Throws when a side can't be read.
    /// </summary>
    public static DiffRun Review(ReviewSource source)
    {
        var (a, b) = source.Load();
        string scope = ReviewSession.Current.Scope;
        var settings = new DiffSettings { ScopeGlob = scope.Length == 0 ? null : scope };
        var run = new DiffRun(RhinoDiff.Diff(a, b, settings), a, b);
        ReviewSession.Current.Show(run, source);
        return run;
    }

    private static string? File(string title, RunMode mode)
    {
        string? path = mode == RunMode.Interactive
            ? RhinoGet.GetFileName(GetFileNameMode.Open, null, title, null)
            : RhinoGet.GetFileNameScripted(GetFileNameMode.Open, null);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
