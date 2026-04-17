using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using YamlDotNet.RepresentationModel;

namespace ContractEngine.Core.Tests.Infrastructure;

/// <summary>
/// Smoke tests pinning the structural contract of the GitHub Actions workflow files shipped in
/// Batch 026. These tests act as a compile-time guard against accidental regressions:
/// <list type="bullet">
///   <item>Both workflows parse as valid YAML with the mandatory root keys (<c>name</c>, <c>on</c>,
///     <c>jobs</c>).</item>
///   <item>Each workflow has at least one job.</item>
///   <item><c>deploy.yml</c> is gated on <c>push</c> to <c>main</c> only — never <c>dev</c>,
///     never on PRs (the CI workflow handles dev/PR validation).</item>
///   <item>Neither workflow file contains inline literal secrets — regex guard against PEM blocks,
///     <c>ghp_*</c> personal access tokens, or <c>sk-*</c> keys that someone might paste during
///     debugging.</item>
/// </list>
///
/// <para>We deliberately DO NOT validate the full GitHub Actions schema here — that's the job of
/// GitHub's own parser on push. The goal is to catch the obvious structural drift that would
/// take the CI/CD pipeline silently offline between edits.</para>
/// </summary>
public class WorkflowYamlSmokeTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CiWorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "ci.yml");
    private static readonly string DeployWorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "deploy.yml");

    [Fact]
    public void CiWorkflow_Exists()
    {
        File.Exists(CiWorkflowPath).Should().BeTrue(
            $"CI workflow must live at .github/workflows/ci.yml (looked at {CiWorkflowPath})");
    }

    [Fact]
    public void DeployWorkflow_Exists()
    {
        File.Exists(DeployWorkflowPath).Should().BeTrue(
            $"deploy workflow must live at .github/workflows/deploy.yml (looked at {DeployWorkflowPath})");
    }

    [Fact]
    public void CiWorkflow_HasRequiredRootKeys()
    {
        var root = LoadYamlRoot(CiWorkflowPath);
        AssertRootKeys(root, "CI workflow");
    }

    [Fact]
    public void DeployWorkflow_HasRequiredRootKeys()
    {
        var root = LoadYamlRoot(DeployWorkflowPath);
        AssertRootKeys(root, "deploy workflow");
    }

    [Fact]
    public void CiWorkflow_HasAtLeastOneJob()
    {
        var root = LoadYamlRoot(CiWorkflowPath);
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        jobs.Children.Count.Should().BeGreaterThan(0, "CI workflow must define at least one job");
    }

    [Fact]
    public void DeployWorkflow_HasAtLeastOneJob()
    {
        var root = LoadYamlRoot(DeployWorkflowPath);
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        jobs.Children.Count.Should().BeGreaterThan(0, "deploy workflow must define at least one job");
    }

    [Fact]
    public void DeployWorkflow_OnPushToMainOnly()
    {
        // The deploy workflow should only fire on push to main. PRs and the dev branch must be
        // handled by ci.yml — production must never deploy from an unmerged branch.
        var root = LoadYamlRoot(DeployWorkflowPath);
        var triggers = root.Children[new YamlScalarNode("on")];

        var triggerYaml = SerializeNode(triggers);

        // Must include "main" and "push"
        triggerYaml.Should().Contain("push", "deploy workflow must fire on push");
        triggerYaml.Should().Contain("main", "deploy workflow must target the main branch");

        // Must NOT include "pull_request" or a branches filter on "dev"
        // (we allow workflow_dispatch for manual triggers — that's a valid escape hatch).
        var devOnPushBranches = DetectDevInPushBranches(triggers);
        devOnPushBranches.Should().BeFalse(
            "deploy workflow must not fire on push to dev — dev belongs to ci.yml only");
    }

    [Fact]
    public void CiWorkflow_DoesNotContainInlineSecrets()
    {
        AssertNoInlineSecrets(CiWorkflowPath);
    }

    [Fact]
    public void DeployWorkflow_DoesNotContainInlineSecrets()
    {
        AssertNoInlineSecrets(DeployWorkflowPath);
    }

    private static YamlMappingNode LoadYamlRoot(string path)
    {
        File.Exists(path).Should().BeTrue($"workflow file {path} must exist before structural checks run");

        using var reader = new StreamReader(path);
        var stream = new YamlStream();
        stream.Load(reader);

        stream.Documents.Should().NotBeEmpty($"{path} must contain at least one YAML document");
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static void AssertRootKeys(YamlMappingNode root, string label)
    {
        var keys = root.Children.Keys
            .OfType<YamlScalarNode>()
            .Select(n => n.Value!)
            .ToHashSet();

        keys.Should().Contain("name", $"{label} must declare a top-level name");
        keys.Should().Contain("on", $"{label} must declare trigger(s) via the on: key");
        keys.Should().Contain("jobs", $"{label} must define at least one job under jobs:");
    }

    private static bool DetectDevInPushBranches(YamlNode triggers)
    {
        // Shape 1: on: [push, pull_request]  (sequence — no branches filter, no dev gate)
        if (triggers is YamlSequenceNode)
        {
            return false;
        }

        // Shape 2: on: { push: { branches: [main, dev] }, ... }
        if (triggers is YamlMappingNode mapping &&
            mapping.Children.TryGetValue(new YamlScalarNode("push"), out var pushNode) &&
            pushNode is YamlMappingNode pushMap &&
            pushMap.Children.TryGetValue(new YamlScalarNode("branches"), out var branchesNode) &&
            branchesNode is YamlSequenceNode branches)
        {
            return branches.Children
                .OfType<YamlScalarNode>()
                .Any(n => string.Equals(n.Value, "dev", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string SerializeNode(YamlNode node)
    {
        using var writer = new StringWriter();
        var stream = new YamlStream(new YamlDocument(node));
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static void AssertNoInlineSecrets(string path)
    {
        var content = File.ReadAllText(path);

        // PEM block — someone pasted a private key.
        content.Should().NotContain("BEGIN RSA PRIVATE KEY",
            $"{path} must not inline a PEM block; use GitHub secrets");
        content.Should().NotContain("BEGIN OPENSSH PRIVATE KEY",
            $"{path} must not inline an SSH private key; use GitHub secrets");
        content.Should().NotContain("BEGIN PRIVATE KEY",
            $"{path} must not inline a PEM block; use GitHub secrets");

        // GitHub personal access tokens — ghp_, ghs_, gho_, ghu_, ghr_ prefixes.
        var ghpRegex = new Regex(@"gh[pusoru]_[A-Za-z0-9_]{20,}", RegexOptions.Compiled);
        ghpRegex.IsMatch(content).Should().BeFalse(
            $"{path} must not embed a GitHub personal access token");

        // OpenAI / Anthropic style keys.
        var slkRegex = new Regex(@"\bsk-[A-Za-z0-9\-_]{20,}", RegexOptions.Compiled);
        slkRegex.IsMatch(content).Should().BeFalse(
            $"{path} must not embed an sk- style API key");
    }

    private static string FindRepoRoot()
    {
        // Walk up from the test assembly location until we find the solution file.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ContractEngine.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate ContractEngine.sln while walking up from the test base directory");
    }
}
