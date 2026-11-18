using BlueStar.Infrastructure.Steam;
using FluentAssertions;
using Xunit;

namespace BlueStar.Infrastructure.Tests.Linux;

public class LinuxSteamRegistryTests
{
    private const string Sample = """
        "Registry"
        {
        	"HKCU"
        	{
        		"Software"
        		{
        			"Valve"
        			{
        				"Steam"
        				{
        					"language"		"spanish"
        					"AutoLoginUser"		"deckuser"
        					"ActiveProcess"
        					{
        						"pid"		"4242"
        						"SteamClientDll"		"/home/deck/.local/share/Steam/ubuntu12_32/steamclient.so"
        						"ActiveUser"		"123456"
        					}
        					"apps"
        					{
        						"480"
        						{
        							"Running"		"0"
        						}
        					}
        				}
        			}
        		}
        	}
        }
        """;

    [Fact]
    public void Parse_ReadsPidActiveUserAndAutoLogin()
    {
        var info = LinuxSteamRegistry.Parse(Sample);

        info.Pid.Should().Be(4242);
        info.ActiveUser.Should().Be(123456u);
        info.AutoLoginUser.Should().Be("deckuser");
    }

    [Fact]
    public void Parse_TreatsZeroAsNotRunning()
    {
        var info = LinuxSteamRegistry.Parse(Sample.Replace("\"4242\"", "\"0\"").Replace("\"123456\"", "\"0\""));

        info.Pid.Should().BeNull();
        info.ActiveUser.Should().BeNull();
    }

    [Fact]
    public void Read_ReturnsNull_WhenFileMissing()
    {
        LinuxSteamRegistry.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "registry.vdf")).Should().BeNull();
    }
}
