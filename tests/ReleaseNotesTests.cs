using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public class ReleaseNotesTests
{
    private const string Log = "# Changelog\n\n## [0.2.0] Beta\n\n### New\n\n- **Thing.** Does a\n  wrapped line.\n- Other\n\n### Fixed\n\n- Bug\n\n## [0.1.0]\n\n- Old\n";

    [Fact]
    public void Extracts_only_the_requested_section()
    {
        var n = ReleaseNotes.Extract(Log, "0.2.0");
        Assert.Equal("New\n• Thing. Does a wrapped line.\n• Other\n\nFixed\n• Bug", n);
    }

    [Fact]
    public void Missing_version_returns_null() => Assert.Null(ReleaseNotes.Extract(Log, "9.9.9"));
}