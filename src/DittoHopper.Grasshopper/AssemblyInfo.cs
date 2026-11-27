using System.Drawing;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

public sealed class AssemblyInfo : GH_AssemblyInfo
{
    // Stable plug-in GUID; also listed in packaging/manifest.yml. Never change it.
    public override Guid Id => new("e59805d1-fcad-4d4f-b916-57b439bf9d91");
    public override string Name => "DittoHopper";
    public override string Description => "Compare two Rhino models and see what changed, and what did not.";
    public override string AuthorName => "aectooling by tomoS";
    public override string AuthorContact => "https://github.com/tsoumdoa/dittohopper";
    public override GH_LibraryLicense License => GH_LibraryLicense.opensource;
    public override Bitmap? Icon => null;
}
