using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.Core.Agent;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Tests.Contracts;

namespace PaceLaps.Agent.Tests.Agent;

public sealed class AgentVersionTests
{
    [Theory]
    [InlineData("0.1.0+6f1c0d2a9b8e7f6a5b4c3d2e1f0a9b8c7d6e5f4a", "0.1.0+6f1c0d2")]
    [InlineData("0.1.0+6f1c0d2", "0.1.0+6f1c0d2")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData(null, "unknown")]
    [InlineData("1.0.0-preview.12.something-very-long-label.and-more", "1.0.0-preview.12.something-very-long-lab")]
    public void Keeps_the_short_commit_hash_within_40_characters(string? informational, string expected) =>
        Assert.Equal(expected, AgentVersion.Shorten(informational));

    [Fact]
    public void The_real_build_version_fits_the_pairing_contract()
    {
        // The SDK appends the full commit hash when building in a repository: 46 characters, refused by the server.
        var json = ContractJson.Serialize(new PairingRequestMessage { AgentVersion = SpikeEnvironment.AgentVersion });

        ContractSchemas.AssertValid("agent-pairing.schema.json", json);
    }
}
