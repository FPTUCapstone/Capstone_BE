using FluentAssertions;

using Xunit;

namespace TripMate.Api.IntegrationTests.Infrastructure;

public sealed class TripMateAiComposeConfigurationTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void AiProfile_DocumentsAndMountsTheFineTunedLocalModel()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot, "docker-compose.yml"));
        var envExample = File.ReadAllText(Path.Combine(RepoRoot, ".env.example"));
        var runbook = File.ReadAllText(
            Path.Combine(RepoRoot, "docs", "operations", "uc10-tripmate-ai-measurement-runbook.md"));

        compose.Should().Contain("LLM_MODELFILE: ${LLM_MODELFILE:-}");
        compose.Should().Contain("${TRIPMATE_MODEL_DIR:-../Capstone_AI/models}:/models:ro");
        compose.Should().Contain("scripts/ollama-init.sh:/usr/local/bin/ollama-init.sh:ro");
        envExample.Should().Contain("# LLM_MODEL=tripmate-explainer");
        envExample.Should().Contain("# LLM_MODELFILE=/models/Modelfile");
        runbook.Should().Contain("docker compose --profile ai up -d --build");
        runbook.Should().Contain("docker compose --profile ai exec ai python -c");
        runbook.Should().Contain("http://127.0.0.1:8000/ready");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TripMate.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (TripMate.slnx).");
    }
}
