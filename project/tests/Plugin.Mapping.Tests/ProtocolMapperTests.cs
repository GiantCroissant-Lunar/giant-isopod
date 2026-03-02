using GiantIsopod.Contracts.Core;
using GiantIsopod.Contracts.Protocol.Aieos;
using GiantIsopod.Contracts.Protocol.PiRpc;

namespace GiantIsopod.Plugin.Mapping.Tests;

public class ProtocolMapperMapToolEventToStateTests
{
    private readonly ProtocolMapper _mapper = new();

    [Theory]
    [InlineData("write", AgentActivityState.Typing)]
    [InlineData("edit", AgentActivityState.Typing)]
    [InlineData("bash", AgentActivityState.Typing)]
    [InlineData("WRITE", AgentActivityState.Typing)]
    [InlineData("Edit", AgentActivityState.Typing)]
    public void WriteLikeTools_ReturnTyping(string toolName, AgentActivityState expected)
    {
        var evt = new RpcToolEvent { ToolName = toolName };
        Assert.Equal(expected, _mapper.MapToolEventToState(evt));
    }

    [Theory]
    [InlineData("read", AgentActivityState.Reading)]
    [InlineData("grep", AgentActivityState.Reading)]
    [InlineData("find", AgentActivityState.Reading)]
    [InlineData("ls", AgentActivityState.Reading)]
    [InlineData("READ", AgentActivityState.Reading)]
    public void ReadLikeTools_ReturnReading(string toolName, AgentActivityState expected)
    {
        var evt = new RpcToolEvent { ToolName = toolName };
        Assert.Equal(expected, _mapper.MapToolEventToState(evt));
    }

    [Theory]
    [InlineData("thinking", AgentActivityState.Thinking)]
    [InlineData("waiting", AgentActivityState.Waiting)]
    public void UnknownTool_FallsBackToStatus(string status, AgentActivityState expected)
    {
        var evt = new RpcToolEvent { ToolName = "unknown_tool", Status = status };
        Assert.Equal(expected, _mapper.MapToolEventToState(evt));
    }

    [Fact]
    public void NullToolName_FallsBackToStatus()
    {
        var evt = new RpcToolEvent { ToolName = null, Status = "thinking" };
        Assert.Equal(AgentActivityState.Thinking, _mapper.MapToolEventToState(evt));
    }

    [Fact]
    public void NullToolNameAndUnknownStatus_ReturnsIdle()
    {
        var evt = new RpcToolEvent { ToolName = null, Status = "some-unknown" };
        Assert.Equal(AgentActivityState.Idle, _mapper.MapToolEventToState(evt));
    }

    [Fact]
    public void NullToolNameAndNullStatus_ReturnsIdle()
    {
        var evt = new RpcToolEvent { ToolName = null, Status = null };
        Assert.Equal(AgentActivityState.Idle, _mapper.MapToolEventToState(evt));
    }
}

public class ProtocolMapperMapAieosTests
{
    private readonly ProtocolMapper _mapper = new();

    [Fact]
    public void MapAieosToVisualInfo_UsesFirstName()
    {
        var entity = new AieosEntity
        {
            Identity = new AieosIdentity
            {
                Names = new AieosNames { First = "Alice" }
            }
        };

        var result = _mapper.MapAieosToVisualInfo("agent-1", entity);

        Assert.Equal("Alice", result.DisplayName);
        Assert.Equal("agent-1", result.AgentId);
    }

    [Fact]
    public void MapAieosToVisualInfo_FallsBackToAlias()
    {
        var entity = new AieosEntity
        {
            Identity = new AieosIdentity { Names = new AieosNames { First = null } },
            Metadata = new AieosMetadata { Alias = "bot-alice" }
        };

        var result = _mapper.MapAieosToVisualInfo("agent-1", entity);

        Assert.Equal("bot-alice", result.DisplayName);
    }

    [Fact]
    public void MapAieosToVisualInfo_FallsBackToAgentId()
    {
        var entity = new AieosEntity();

        var result = _mapper.MapAieosToVisualInfo("agent-1", entity);

        Assert.Equal("agent-1", result.DisplayName);
    }

    [Fact]
    public void MapAieosToVisualInfo_ExtractsPhysicality()
    {
        var entity = new AieosEntity
        {
            Identity = new AieosIdentity { Names = new AieosNames { First = "Alice" } },
            Physicality = new AieosPhysicality
            {
                Face = new AieosFace
                {
                    Skin = new AieosSkin { Tone = "warm" },
                    Hair = new AieosHair { Style = "short", Color = "brown" }
                },
                Style = new AieosStyle { AestheticArchetype = "minimalist" }
            }
        };

        var result = _mapper.MapAieosToVisualInfo("agent-1", entity);

        Assert.Equal("warm", result.SkinTone);
        Assert.Equal("short", result.HairStyle);
        Assert.Equal("brown", result.HairColor);
        Assert.Equal("minimalist", result.AestheticArchetype);
    }

    [Fact]
    public void MapAieosToCapabilities_ExtractsSkillNames()
    {
        var entity = new AieosEntity
        {
            Capabilities = new AieosCapabilities
            {
                Skills = [
                    new AieosSkill { Name = "code_review" },
                    new AieosSkill { Name = "testing" },
                    new AieosSkill { Name = null } // null names filtered
                ]
            }
        };

        var result = _mapper.MapAieosToCapabilities(entity);

        Assert.Equal(2, result.Count);
        Assert.Contains("code_review", result);
        Assert.Contains("testing", result);
    }

    [Fact]
    public void MapAieosToCapabilities_NullCapabilities_ReturnsEmpty()
    {
        var entity = new AieosEntity();

        var result = _mapper.MapAieosToCapabilities(entity);

        Assert.Empty(result);
    }

    [Fact]
    public void MapAieosToCapabilities_NullSkills_ReturnsEmpty()
    {
        var entity = new AieosEntity
        {
            Capabilities = new AieosCapabilities { Skills = null }
        };

        var result = _mapper.MapAieosToCapabilities(entity);

        Assert.Empty(result);
    }
}
