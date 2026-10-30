using System.Runtime.InteropServices;
using DevDriveBench.Engine.Platform;
using DevDriveBench.Engine.Run;

namespace DevDriveBench.Engine.Tests;

public class InstallerTests
{
    [Fact]
    public void EachWorkloadMapsToItsWingetPackages()
    {
        Assert.Equal("Git.Git", Assert.Single(InstallCatalog.For(TestCatalog.GitClone)).Id);
        Assert.Equal("OpenJS.NodeJS.LTS", Assert.Single(InstallCatalog.For(TestCatalog.NpmCi)).Id);
        Assert.Equal("Microsoft.DotNet.SDK.10", Assert.Single(InstallCatalog.For(TestCatalog.DotnetBuild)).Id);
        Assert.Empty(InstallCatalog.For(TestCatalog.DiskGroup));

        // cargo needs the MSVC linker, installed before rustup; rustup then needs a default toolchain.
        IReadOnlyList<WingetPackage> cargo = InstallCatalog.For(TestCatalog.CargoBuild);
        Assert.Equal(new[] { "Microsoft.VisualStudio.2022.BuildTools", "Rustlang.Rustup" }, cargo.Select(p => p.Id));
        Assert.Equal(("rustup", "default stable"), cargo[1].PostInstall);
        Assert.Null(cargo[0].PostInstall);
    }

    [Fact]
    public void BuildToolsAddTheArm64ToolsetOnlyOnArm64()
    {
        string arm = InstallCatalog.MsvcBuildTools(Architecture.Arm64).Override!;
        string x64 = InstallCatalog.MsvcBuildTools(Architecture.X64).Override!;
        Assert.Contains("Microsoft.VisualStudio.Workload.VCTools", arm);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.ARM64", arm);
        Assert.Contains("Microsoft.VisualStudio.Workload.VCTools", x64);
        Assert.DoesNotContain("ARM64", x64);
    }

    [Fact]
    public void ArgumentsAreExactNonInteractiveAndQuoteTheOverride()
    {
        string plain = InstallCatalog.Arguments(new WingetPackage("Git.Git", "Git"), Architecture.Arm64);
        Assert.Equal("install --id Git.Git --exact --source winget --accept-package-agreements --accept-source-agreements --disable-interactivity --architecture arm64", plain);

        Assert.EndsWith(" --architecture x64", InstallCatalog.Arguments(new WingetPackage("Git.Git", "Git"), Architecture.X64));

        string withOverride = InstallCatalog.Arguments(new WingetPackage("X", "X", "--wait --add A"), Architecture.Arm64);
        Assert.EndsWith(" --architecture arm64 --override \"--wait --add A\"", withOverride);
    }

    [Fact]
    public void ToolsArePinnedToTheOsArchitectureButBuildToolsAreNot()
    {
        foreach (string group in new[] { TestCatalog.GitClone, TestCatalog.NpmCi, TestCatalog.DotnetBuild })
        {
            Assert.Contains("--architecture arm64", InstallCatalog.Arguments(InstallCatalog.For(group)[0], Architecture.Arm64));
        }

        IReadOnlyList<WingetPackage> cargo = InstallCatalog.For(TestCatalog.CargoBuild);
        Assert.DoesNotContain("--architecture", InstallCatalog.Arguments(cargo[0], Architecture.Arm64));
        Assert.Contains("--architecture arm64", InstallCatalog.Arguments(cargo[1], Architecture.Arm64));
    }

    [Theory]
    [InlineData((ushort)0xAA64, "arm64")]
    [InlineData((ushort)0x8664, "x64")]
    [InlineData((ushort)0x014C, "x86")]
    [InlineData((ushort)0x01C4, null)]
    public void PeMachineTypeIsReadFromTheHeader(ushort machine, string? expected)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ddb-pe-{Guid.NewGuid():N}.exe");
        try
        {
            var bytes = new byte[0x100];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
            "PE\0\0"u8.ToArray().CopyTo(bytes, 0x80);
            BitConverter.GetBytes(machine).CopyTo(bytes, 0x84);
            File.WriteAllBytes(path, bytes);

            Assert.Equal(expected, ToolProbe.PeArchitecture(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("x64", "arm64", @"C:\Program Files\nodejs\node.exe", "x64 node, emulated")]
    [InlineData("arm64", "arm64", @"C:\Program Files\nodejs\node.exe", null)]
    [InlineData(null, "arm64", null, null)]
    [InlineData("x64", "", @"C:\x\cargo.exe", null)]
    [InlineData("x86", "x64", null, "x86 npm, emulated")]
    public void EmulationIsFlaggedOnlyOnAMismatch(string? toolArch, string osArch, string? binary, string? expected)
    {
        var test = new Model.TestDescriptor { RequiredTool = "npm", ToolArchitecture = toolArch, ToolBinary = binary };
        Assert.Equal(expected, TestCatalog.EmulationNote(test, new Model.MachineInfo { OsArchitecture = osArch }));
    }

    [Fact]
    public void NonPeFilesHaveNoArchitecture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ddb-pe-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\r\nnode %*\r\n");
        try
        {
            Assert.Null(ToolProbe.PeArchitecture(path));
            Assert.Null(ToolProbe.PeArchitecture(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0, "", true)]
    [InlineData(-1978335189, "Found an existing package already installed. Trying to upgrade...\nNo available upgrade found.", true)]
    [InlineData(-1978335212, "No package found matching input criteria.", false)]
    [InlineData(1603, "Installer failed with exit code: 1603", false)]
    public void SuccessIncludesNothingToDo(int exit, string output, bool expected)
    {
        Assert.Equal(expected, WingetInstaller.IsSuccess(exit, output));
    }

    [Theory]
    [InlineData("  ██████████▒▒▒▒▒  12.0 MB / 30.5 MB", "12.0 MB / 30.5 MB")]
    [InlineData("   - \r   \\ \r   | ", null)]
    [InlineData("Successfully installed", "Successfully installed")]
    [InlineData("Downloading https://nodejs.org/dist/v26.13.0/node-v26.13.0-arm64.msi", "Downloading https://nodejs.org/dist/v26.13.0/node-v26.13.0-arm64.msi")]
    public void StatusLineKeepsWhatSaysSomething(string raw, string? expected)
    {
        Assert.Equal(expected, WingetInstaller.StatusLine(raw));
    }

    [Fact]
    public void PathMergeIsMachineThenUserThenProcessDeduplicated()
    {
        Environment.SetEnvironmentVariable("DDB_TEST_HOME", @"C:\Users\me");
        string merged = EnvironmentRefresher.MergePath(
            @"C:\Windows\system32;C:\Program Files\Git\cmd\",
            @"%DDB_TEST_HOME%\.cargo\bin;C:\Windows\System32",
            @"C:\Windows\system32;C:\only-in-process");

        Assert.Equal(@"C:\Windows\system32;C:\Program Files\Git\cmd;C:\Users\me\.cargo\bin;C:\only-in-process", merged);
    }
}
