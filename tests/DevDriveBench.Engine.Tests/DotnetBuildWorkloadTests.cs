using System.Reflection;
using System.Xml.Linq;
using DevDriveBench.Engine.Abstractions;
using DevDriveBench.Engine.Platform;

namespace DevDriveBench.Engine.Tests;

public class DotnetBuildWorkloadTests
{
    [Fact]
    public void SeedConfigInheritsConfiguredFeedsBeforeRestore()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevDriveBench.Tests", "dotnet-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runner = new SeedRunner();
            PrepareSeed(root, runner);

            Assert.True(runner.RestoreObserved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("public-only", "https://public.example.test/v3/index.json")]
    [InlineData("internal-only", "https://internal.example.test/v3/index.json")]
    public void SeedConfigResolvesOnlyTheConfiguredParentFeed(string feedName, string feedUrl)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevDriveBench.Tests", "dotnet-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            new XDocument(
                new XElement("configuration",
                    new XElement("packageSources",
                        new XElement("clear"),
                        new XElement("add", new XAttribute("key", feedName), new XAttribute("value", feedUrl)))))
                .Save(Path.Combine(root, "NuGet.Config"));
            var runner = new SeedRunner();
            PrepareSeed(root, runner);
            WorkloadProcessRequest restore = Assert.IsType<WorkloadProcessRequest>(runner.RestoreRequest);
            Assert.StartsWith(root, restore.Environment!["DOTNET_CLI_HOME"], StringComparison.OrdinalIgnoreCase);

            // This CLI command only reads configuration; it does not contact the listed sources.
            ProcessRunResult result = new WorkloadProcessRunner().Run(new WorkloadProcessRequest(
                "dotnet", "nuget list source --format Short",
                restore.WorkingDirectory, restore.Environment, TimeoutMs: 30_000));

            Assert.False(result.TimedOut);
            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            Assert.Equal($"E {feedUrl}", result.StandardOutput.Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void PrepareSeed(string root, IWorkloadProcessRunner runner)
    {
        var workload = new DotnetBuildWorkload(runner);
        // Bypass the wrapper's disk-space checks/staging; only the seed-restore boundary is needed.
        // Git and restore are fake, so production preparation cannot make a network request.
        typeof(WorkloadBenchmarkBase).GetProperty("SeedDirectory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(workload, root);
        typeof(DotnetBuildWorkload).GetMethod("PrepareCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(workload, [new WorkloadEnvironment(root, root, root), CancellationToken.None]);
    }

    private sealed class SeedRunner : IWorkloadProcessRunner
    {
        public bool RestoreObserved { get; private set; }
        public WorkloadProcessRequest? RestoreRequest { get; private set; }

        public ProcessRunResult Run(WorkloadProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName == "git" && request.Arguments.Contains("checkout"))
            {
                File.WriteAllText(Path.Combine(request.WorkingDirectory!, "nuget.config"),
                    "<configuration><packageSources><clear/><add key=\"public\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources></configuration>");
            }

            if (request.FileName == "dotnet" && request.Arguments.StartsWith("restore "))
            {
                string text = File.ReadAllText(Path.Combine(request.WorkingDirectory!, "nuget.config"));
                XElement config = XElement.Parse(text);
                XElement sources = Assert.Single(config.Elements("packageSources"));
                Assert.Empty(sources.Elements());
                Assert.Empty(config.Descendants("clear"));
                Assert.DoesNotContain("https://", text);
                Assert.DoesNotContain("--source", request.Arguments);
                RestoreObserved = true;
                RestoreRequest = request;
            }

            return new ProcessRunResult(0, "", "");
        }
    }
}
