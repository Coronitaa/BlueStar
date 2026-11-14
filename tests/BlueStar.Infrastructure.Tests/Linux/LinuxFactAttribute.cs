using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

/// <summary>A test that exercises the real Linux code path (bash, libc). Skipped on Windows, run by the Linux CI.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
            Skip = "Linux only";
    }
}
